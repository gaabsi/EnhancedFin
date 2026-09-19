using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Un crédit de filmographie : un média auquel la personne a participé.
    ///
    /// Propriétés en camelCase à dessein — voir <see cref="ListResponse{T}"/>.
    /// </summary>
    public record PersonCredit(
        string mediaKey,
        string mediaType,
        string title,
        int? year,
        string? posterUrl,
        /// Le rôle joué, ou le poste occupé pour l'équipe technique.
        string? role,
        bool inLibrary,
        string? jellyfinId,
        int? rating,
        /// Score TMDB, renvoyé pour que le client puisse retrier sans nouvel appel.
        double popularity);

    /// <summary>
    /// Une personne et sa filmographie complète.
    /// </summary>
    public record PersonResponse(
        int tmdbId,
        string name,
        string? profileUrl,
        string? biography,
        string? birthday,
        string? deathday,
        string? placeOfBirth,
        IReadOnlyList<PersonCredit> credits,
        /// Nombre de crédits retenus par les filtres, **avant** le plafond
        /// d'affichage. `total > credits.Count` est donc le signal que la liste est
        /// tronquée — exactement la sémantique du `total` des routes de liste. Il
        /// valait jusqu'ici `credits.Count`, c'est-à-dire rien.
        int total);

    /// <summary>
    /// Filmographie d'une personne, qu'elle soit sur le serveur ou non.
    ///
    /// Le client Jellyfin ne sait afficher que les rôles dont il possède un fichier ;
    /// cet endpoint rend la carrière entière, en signalant au passage ce qui est
    /// lisible immédiatement.
    ///
    /// Rien n'est stocké : une filmographie n'est pas consultée deux fois par jour,
    /// et la garder en base imposerait de la rafraîchir à chaque nouveau film.
    /// </summary>
    public class PersonController : EnhancedFinController
    {
        /// <summary>
        /// Nombre de crédits retenus, les plus populaires d'abord.
        ///
        /// `combined_credits` rend tout : images d'archive, apparitions non
        /// créditées, émissions de plateau. Sur une carrière fournie cela dépasse
        /// deux cents entrées dont la majorité n'intéresse personne. Un plafond sur
        /// la popularité est plus robuste qu'un seuil chiffré, que l'échelle de
        /// TMDB rendrait caduc.
        /// </summary>
        private const int MaxCredits = 60;

        /// <summary>
        /// Une apparition en tant que soi-même, ancrée en début de libellé.
        ///
        /// Les variantes françaises sont là par précaution, pas par constat :
        /// mesuré sur cinq filmographies (1 079 crédits, Keanu Reeves à Tom Hanks),
        /// TMDB **ne traduit pas** `character` ni `job` dans `combined_credits` —
        /// aucun libellé filtrable n'en revient en français. La donnée étant
        /// contributive, une entrée inutile coûte moins qu'un crédit parasite.
        /// </summary>
        private static readonly Regex SelfRole =
            new(@"^(self|lui-même|elle-même|soi-même)\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Libellés qui trahissent une apparition, où qu'ils se trouvent.</summary>
        private static readonly string[] NonRoleMarkers =
        {
            "archive footage",
            "uncredited",
            "images d'archives",
            "non crédité",
        };

        private readonly Db _db;
        private readonly TmdbClient _tmdb;
        private readonly JellyfinLibrary _library;
        private readonly ILogger<PersonController> _logger;

        public PersonController(
            Db db, TmdbClient tmdb, JellyfinLibrary library, ILogger<PersonController> logger)
        {
            _db = db;
            _tmdb = tmdb;
            _library = library;
            _logger = logger;
        }

        // GET /api/EnhancedFin/v1/person/{tmdbId}
        [HttpGet("person/{tmdbId:int}")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult<PersonResponse>> get(int tmdbId)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            if (tmdbId <= 0)
                return problem(400, "Identifiant invalide", "tmdbId doit être un entier positif.");

            var person = await _tmdb.get_person(tmdbId);
            if (person is null)
                return problem(404, "Personne inconnue", $"TMDB ne connaît pas la personne {tmdbId}.");

            var (credits, total) = build_credits(user_id, person);

            return Ok(new PersonResponse(
                tmdbId: person.Id,
                name: person.Name ?? "Sans nom",
                profileUrl: TmdbClient.image_url(person.ProfilePath),
                biography: string.IsNullOrWhiteSpace(person.Biography) ? null : person.Biography,
                birthday: person.Birthday,
                deathday: person.Deathday,
                placeOfBirth: person.PlaceOfBirth,
                credits: credits,
                total: total));
        }

        /// <summary>
        /// Assemble la filmographie : acteur et équipe fusionnés, bruit écarté,
        /// enrichie de mon état et de ce que porte la bibliothèque.
        ///
        /// Parametres :
        /// - user_id (string) : identité de l'appelant
        /// - person (TmdbPerson) : fiche TMDB
        ///
        /// Output :
        /// - credits (List&lt;PersonCredit&gt;) : du plus récent au plus ancien, plafonnés
        /// - total (int) : nombre de crédits retenus **avant** le plafond
        /// </summary>
        private (List<PersonCredit> Credits, int Total) build_credits(string user_id, TmdbPerson person)
        {
            var raw = new List<TmdbCredit>();
            raw.AddRange(person.CombinedCredits?.Cast ?? new List<TmdbCredit>());
            raw.AddRange(person.CombinedCredits?.Crew ?? new List<TmdbCredit>());

            var eligible = raw
                // Une affiche absente signale presque toujours une entrée sans
                // intérêt.
                .Where(c => c.PosterPath is not null)
                .Where(c => c.MediaType is "movie" or "tv")
                .Where(c => is_real_role(c.Role))
                // Un même média revient deux fois quand la personne y est à la fois
                // devant et derrière la caméra. On garde la première occurrence,
                // c'est-à-dire le rôle joué.
                .GroupBy(c => $"{c.MediaType}:{c.Id}")
                .Select(g => g.First())
                .ToList();

            var retained = eligible
                .OrderByDescending(c => c.Popularity ?? 0)
                .Take(MaxCredits)
                // Tri d'affichage distinct du tri de sélection : on retient les plus
                // connus, on les présente par ordre chronologique inverse.
                .OrderByDescending(c => c.Date ?? string.Empty)
                .ToList();

            // Trois filtres s'empilent — affiche absente, rôle écarté, plafond — et
            // aucun n'était observable. Une filmographie qui rend six crédits sur
            // deux cents ne dit pas lequel des trois a mordu.
            _logger.LogDebug(
                "[EnhancedFin] person {Id} : {Raw} crédits bruts → {Eligible} éligibles → {Retained} rendus",
                person.Id, raw.Count, eligible.Count, retained.Count);

            if (retained.Count == 0) return (new List<PersonCredit>(), 0);

            // L'index est porté par l'utilisateur : il ne doit pas révéler les
            // bibliothèques auxquelles il n'a pas accès.
            var in_library = Guid.TryParse(user_id, out var guid)
                ? _library.index(guid)
                : new Dictionary<string, LibraryMatch>();
            var my_ratings = read_my_ratings(user_id, retained.Select(c => $"{c.MediaType}:{c.Id}"));

            var credits = retained.ConvertAll(c =>
            {
                var media_key = $"{c.MediaType}:{c.Id}";
                in_library.TryGetValue(media_key, out var match);
                var rating = my_ratings.TryGetValue(media_key, out var score) ? score : (int?)null;

                return new PersonCredit(
                    mediaKey: media_key,
                    mediaType: c.MediaType!,
                    title: c.DisplayTitle,
                    year: c.Year,
                    posterUrl: TmdbClient.image_url(c.PosterPath),
                    role: c.Role,
                    inLibrary: match is not null,
                    jellyfinId: match?.JellyfinId.ToString("D"),
                    rating: rating,
                    popularity: c.Popularity ?? 0);
            });

            return (credits, eligible.Count);
        }

        /// <summary>
        /// Écarte les apparitions qui ne sont pas des rôles.
        ///
        /// Filtrer sur la popularité ne suffit pas : une émission de plateau diffusée
        /// depuis 1951 est très populaire, et l'invité d'un soir s'y retrouve classé
        /// devant ses propres films. TMDB ne marque pas ces entrées autrement que
        /// dans le libellé du rôle, d'où cette heuristique sur le texte.
        ///
        /// Parametres :
        /// - role (string | null) : rôle joué ou poste occupé
        ///
        /// Un rôle vide est le même signal : un crédit d'équipe porte toujours un
        /// poste, un crédit d'acteur presque toujours un personnage. Son absence
        /// trahit l'apparition d'un invité. Quelques vrais rôles mal renseignés
        /// chez TMDB en font les frais — c'est le bon compromis, la liste étant
        /// déjà plafonnée.
        ///
        /// Output :
        /// - keep (bool) : faux pour une apparition en tant que soi-même, une image
        ///   d'archive, un rôle non crédité ou un rôle absent
        /// </summary>
        private static bool is_real_role(string? role)
        {
            if (string.IsNullOrWhiteSpace(role)) return false;

            // Limite de mot, et non `StartsWith("Self")` : « Selma », « Selby » ou
            // « Selina Kyle » sont de vrais rôles que le préfixe nu écartait.
            if (SelfRole.IsMatch(role)) return false;

            return !NonRoleMarkers.Any(m => role.Contains(m, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Notes de l'appelant pour un lot de médias, en une requête.
        ///
        /// Parametres :
        /// - user_id (string) : identité de l'appelant
        /// - media_keys (IEnumerable&lt;string&gt;) : clés à interroger
        ///
        /// Output :
        /// - ratings (Dictionary) : clé -&gt; note ; une clé non notée est simplement
        ///   absente, et `TryGetValue` rend alors le défaut
        /// </summary>
        /// <remarks>
        /// Une requête plate plutôt qu'une table fabriquée à coups de `UNION ALL`.
        /// L'ancienne forme produisait un SELECT composé d'autant de branches que de
        /// clés : elle ne tenait que parce que `MaxCredits` vaut 60, contre une
        /// limite SQLite de 500 branches (`SQLITE_MAX_COMPOUND_SELECT`). Une
        /// constante posée pour des raisons d'affichage gardait donc une requête du
        /// côté légal — la porter à 500 aurait fait tomber la route, pas ralenti
        /// l'affichage. Avec `IN (...)`, la seule borne est celle des paramètres
        /// liés (32 766).
        ///
        /// Les clés viennent de TMDB, pas de l'appelant, mais elles restent liées :
        /// une requête paramétrée ne se contourne pas.
        /// </remarks>
        private Dictionary<string, int> read_my_ratings(
            string user_id, IEnumerable<string> media_keys)
        {
            var keys = media_keys.ToList();
            var ratings = new Dictionary<string, int>(StringComparer.Ordinal);
            if (keys.Count == 0) return ratings;

            var placeholders = string.Join(",", keys.Select((_, i) => $"$k{i}"));

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                $"SELECT media_key, score FROM rating WHERE user_id = $u AND media_key IN ({placeholders})";
            cmd.Parameters.AddWithValue("$u", user_id);
            for (var i = 0; i < keys.Count; i++)
                cmd.Parameters.AddWithValue($"$k{i}", keys[i]);

            using var rd = cmd.ExecuteReader();
            while (rd.Read()) ratings[rd.GetString(0)] = rd.GetInt32(1);

            return ratings;
        }
    }
}
