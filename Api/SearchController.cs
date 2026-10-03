using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// L'état de l'utilisateur sur un résultat de recherche.
    /// </summary>
    public record SearchItemMe(int? rating, bool inWatchlist, bool following);

    /// <summary>
    /// Un résultat de `GET /search`.
    ///
    /// Un seul type pour les trois origines (référentiel local, TMDB, bibliothèque
    /// Jellyfin seule), alors que le code produisait trois formes légèrement
    /// différentes. L'unification est **sans effet sur le fil** : les champs propres
    /// à TMDB (`originalTitle`, `releaseDate`, `genreIds`) valent `null` pour les
    /// autres origines, et Jellyfin omet les nuls (`WhenWritingNull`).
    ///
    /// `known` et `inLibrary` sont indépendants : `known` = présent dans mon
    /// référentiel (noté, vu, en watchlist), `inLibrary` = le fichier est sur le
    /// serveur, donc lisible. Un film noté peut avoir quitté la bibliothèque ; un
    /// film présent peut n'avoir jamais été touché.
    /// </summary>
    public record SearchItem(
        string mediaKey,
        string mediaType,
        int tmdbId,
        string title,
        string? originalTitle,
        int? year,
        string? releaseDate,
        string? overview,
        string? posterUrl,
        string? backdropUrl,
        IReadOnlyList<int>? genreIds,
        bool known,
        bool inLibrary,
        string? jellyfinId,
        SearchItemMe me);

    /// <summary>
    /// Recherche de médias par titre.
    ///
    /// Deux différences avec le `DiscoverSearch` actuel :
    ///
    /// 1. **Le référentiel local est interrogé en premier.** La majorité des recherches
    ///    portent sur des médias déjà connus (notés, en watchlist, vus) : ils sortent
    ///    instantanément, sans appel réseau. TMDB ne complète que ce qui manque.
    ///
    /// 2. **L'état utilisateur est inclus dans les résultats.** Le front actuel doit
    ///    rappeler `MyRating` et `Watchlist` pour chaque candidat afin de savoir s'il
    ///    est déjà noté ; ici l'information est jointe en base, sans requête de plus.
    /// </summary>
    public class SearchController : EnhancedFinController
    {
        /// <summary>Nombre maximum de résultats renvoyés, toutes provenances confondues.</summary>
        private const int MaxResults = 20;

        /// <summary>
        /// Longueur maximale du texte cherché.
        ///
        /// Aucun titre n'approche cette borne. Elle empêche qu'une requête de plusieurs
        /// kilo-octets parte vers TMDB et serve de clé de cache — c'est ce qui rendait
        /// le cache mémoire alimentable depuis l'extérieur.
        /// </summary>
        private const int MaxQueryLength = 100;

        private readonly Db _db;
        private readonly TmdbClient _tmdb;
        private readonly JellyfinLibrary _library;

        public SearchController(Db db, TmdbClient tmdb, JellyfinLibrary library)
        {
            _db = db;
            _tmdb = tmdb;
            _library = library;
        }

        // GET /api/EnhancedFin/v1/search?q=inter&type=movie
        [HttpGet("search")]
        [RateLimit("outbound", 60)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        // `string?` et non `string` : avec Nullable activé, un paramètre non-nullable est
        // traité comme requis par [ApiController], qui rejette `?q=` en 400 **avant**
        // d'entrer dans la méthode — rendant le garde ci-dessous inatteignable.
        //
        // `type` omis cherche films **et** séries. C'est le comportement attendu d'une
        // barre de recherche : l'utilisateur tape un titre, pas une catégorie. Le
        // conserver en paramètre permet aux appelants qui savent ce qu'ils veulent de
        // diviser le travail par deux.
        public async Task<ActionResult<ListResponse<SearchItem>>> search([FromQuery] string? q, [FromQuery] string? type = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();

            if (string.IsNullOrWhiteSpace(q))
                return Ok(new ListResponse<SearchItem>(Array.Empty<SearchItem>(), 0));
            if (q.Length > MaxQueryLength)
                return problem(400, "Recherche trop longue",
                               $"q ne peut excéder {MaxQueryLength} caractères.");
            if (!is_valid_media_type(type)) return invalid_media_type();

            var user_id = user.Value.ToString("D");
            var types = type is null ? new[] { "movie", "tv" } : new[] { type };

            // Trois seaux plutôt qu'une liste : sans eux, chercher sans type revient
            // à coller les films devant les séries, et une série très connue se
            // retrouve derrière dix films obscurs qui partagent son titre.
            var known = new List<SearchItem>();
            var discovered = new List<(SearchItem Item, double Popularity)>();
            var library_only = new List<SearchItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var media_type in types)
                await search_one_type(user.Value, user_id, q, media_type, known, discovered, library_only, seen);

            // Ce que je connais d'abord, puis la découverte classée par popularité
            // tous types confondus, puis ce que seule la bibliothèque remonte.
            var items = new List<SearchItem>(known);
            items.AddRange(discovered.OrderByDescending(d => d.Popularity).Select(d => d.Item));
            items.AddRange(library_only);

            return Ok(new ListResponse<SearchItem>(items, items.Count));
        }

        /// <summary>
        /// Ajoute à `items` les résultats d'un type, en fusionnant les trois sources.
        ///
        /// `MaxResults` s'applique **par type** : une recherche sans type renvoie donc
        /// jusqu'à deux fois plus d'items, ce qui évite qu'une catégorie riche en
        /// résultats écrase l'autre.
        ///
        /// Parametres :
        /// - user (Guid) : identité de l'appelant, pour les droits bibliothèque
        /// - user_id (string) : la même, en forme canonique, pour les requêtes SQL
        /// - query (string) : texte saisi
        /// - media_type (string) : 'movie' ou 'tv'
        /// - known (List&lt;SearchItem&gt;) : résultats de mon référentiel
        /// - discovered (List) : résultats TMDB, avec leur popularité pour le tri
        /// - library_only (List&lt;SearchItem&gt;) : items que seule la bibliothèque remonte
        /// - seen (HashSet&lt;string&gt;) : clés déjà ajoutées, tous types confondus
        /// </summary>
        private async Task search_one_type(
            Guid user, string user_id, string query, string media_type,
            List<SearchItem> known, List<(SearchItem, double)> discovered,
            List<SearchItem> library_only, HashSet<string> seen)
        {
            var added = 0;

            // Une seule interrogation de la bibliothèque, consultée ensuite par clé :
            // le front a besoin de savoir quels résultats sont lisibles immédiatement.
            // Elle porte l'utilisateur : sans lui, un compte restreint voyait le titre
            // et le `jellyfinId` de bibliothèques auxquelles il n'a pas accès.
            var in_library = _library.search(user, query, media_type, MaxResults);

            foreach (var local in search_local(user_id, query, media_type, in_library))
            {
                if (!seen.Add(local.MediaKey)) continue;
                known.Add(local.Payload);
                added++;
            }

            // TMDB complète : les candidats déjà trouvés localement sont ignorés.
            if (added < MaxResults)
            {
                foreach (var hit in await _tmdb.search(query, media_type))
                {
                    var media_key = $"{media_type}:{hit.Id}";
                    if (!seen.Add(media_key)) continue;

                    discovered.Add((new SearchItem(
                        mediaKey: media_key,
                        mediaType: media_type,
                        tmdbId: hit.Id,
                        title: hit.DisplayTitle,
                        originalTitle: hit.OriginalTitle ?? hit.OriginalName,
                        year: hit.Year,
                        releaseDate: hit.Date,
                        // Renvoyés par /search sans appel supplémentaire : les tuiles
                        // du front en ont besoin pour le visuel et le survol.
                        overview: hit.Overview,
                        posterUrl: TmdbClient.image_url(hit.PosterPath),
                        backdropUrl: TmdbClient.image_url(hit.BackdropPath),
                        genreIds: hit.GenreIds,
                        known: false,
                        inLibrary: in_library.ContainsKey(media_key),
                        jellyfinId: in_library.TryGetValue(media_key, out var jf)
                                    ? jf.JellyfinId.ToString("D") : null,
                        me: new SearchItemMe(null, false, false)),
                        hit.Popularity ?? 0));

                    if (++added >= MaxResults) break;
                }
            }

            // Items de la bibliothèque qu'aucune des deux sources n'a remontés : le
            // titre du fichier local peut différer du titre TMDB (édition, langue,
            // renommage). Ils sont lisibles immédiatement, donc les omettre serait
            // le pire des cas pour l'utilisateur.
            foreach (var (media_key, match) in in_library)
            {
                if (!seen.Add(media_key)) continue;

                var parts = MediaCatalog.split(media_key);
                library_only.Add(new SearchItem(
                    mediaKey: media_key,
                    mediaType: media_type,
                    tmdbId: parts?.TmdbId ?? 0,
                    title: match.Name,
                    originalTitle: null,
                    year: null,
                    releaseDate: null,
                    overview: null,
                    posterUrl: null,
                    backdropUrl: null,
                    genreIds: null,
                    known: false,
                    inLibrary: true,
                    jellyfinId: match.JellyfinId.ToString("D"),
                    me: new SearchItemMe(null, false, false)));
            }
        }

        /// <summary>
        /// Cherche dans le référentiel local, avec l'état de l'utilisateur.
        ///
        /// Parametres :
        /// - user_id (string) : identifiant normalisé de l'appelant
        /// - query (string) : texte saisi
        /// - media_type (string) : 'movie' ou 'tv'
        ///
        /// Output :
        /// - results (IEnumerable) : couples (clé média, charge utile JSON)
        /// </summary>
        private IEnumerable<(string MediaKey, SearchItem Payload)> search_local(
            string user_id, string query, string media_type,
            IReadOnlyDictionary<string, LibraryMatch> in_library)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // LIKE avec COLLATE NOCASE : suffisant ici, la table `media` compte quelques
            // centaines de lignes. Si elle grossissait, FTS5 serait le bon outil.
            cmd.CommandText = @"
                SELECT m.media_key, m.tmdb_id, m.title, m.year, m.poster_url,
                       (SELECT score FROM rating    WHERE user_id = $u AND media_key = m.media_key),
                       (SELECT 1     FROM watchlist WHERE user_id = $u AND media_key = m.media_key),
                       (SELECT 1     FROM follow    WHERE user_id = $u AND media_key = m.media_key),
                       m.backdrop_url,
                       (SELECT overview FROM media_detail WHERE media_key = m.media_key)
                FROM media m
                WHERE m.media_type = $t AND m.title LIKE $q ESCAPE '\' COLLATE NOCASE
                  AND " + SqlIsMine + @"
                ORDER BY CASE WHEN m.title LIKE $prefix ESCAPE '\' COLLATE NOCASE THEN 0 ELSE 1 END,
                         m.title
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$t", media_type);
            // `%` et `_` saisis sont des caractères, pas des jokers.
            var literal = query.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            cmd.Parameters.AddWithValue("$q", $"%{literal}%");
            cmd.Parameters.AddWithValue("$prefix", $"{literal}%");
            cmd.Parameters.AddWithValue("$limit", MaxResults);

            var results = new List<(string, SearchItem)>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var media_key = rd.GetString(0);
                results.Add((media_key, new SearchItem(
                    mediaKey: media_key,
                    mediaType: media_type,
                    tmdbId: rd.GetInt32(1),
                    title: rd.GetString(2),
                    originalTitle: null,
                    year: rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3),
                    releaseDate: null,
                    overview: rd.IsDBNull(9) ? null : rd.GetString(9),
                    posterUrl: rd.IsDBNull(4) ? null : rd.GetString(4),
                    backdropUrl: rd.IsDBNull(8) ? null : rd.GetString(8),
                    genreIds: null,
                    // `known` = présent dans mon référentiel (noté, vu, en watchlist…).
                    // `inLibrary` = le fichier est sur le serveur et donc lisible.
                    // Les deux sont indépendants : un film noté peut ne plus être en
                    // bibliothèque, un film en bibliothèque peut n'avoir jamais été touché.
                    known: true,
                    inLibrary: in_library.ContainsKey(media_key),
                    jellyfinId: in_library.TryGetValue(media_key, out var jf_local)
                                ? jf_local.JellyfinId.ToString("D") : null,
                    me: new SearchItemMe(
                        rating: rd.IsDBNull(5) ? (int?)null : rd.GetInt32(5),
                        inWatchlist: !rd.IsDBNull(6),
                        following: !rd.IsDBNull(7)))));
            }

            return results;
        }
    }
}
