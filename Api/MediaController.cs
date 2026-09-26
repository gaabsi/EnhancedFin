using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>Réponse de `GET media/{mediaKey}/playable`.</summary>
    public record PlayableResponse(bool playable);

    /// <summary>
    /// Fiche média : métadonnées + données de l'utilisateur courant, en un seul appel.
    ///
    /// Remplace la cascade du front actuel, qui enchaîne 6 à 8 requêtes pour afficher
    /// une fiche (ProviderIds, MyRating, Watchlist, Monitoring, SeerrDetails, TmdbImages,
    /// Anime/Match…) puis assemble le tout en JavaScript.
    ///
    /// Cet endpoint n'était pas réalisable sur l'ancien schéma : les quatre systèmes
    /// d'identifiants concurrents empêchaient toute jointure entre note, watchlist et
    /// progression (cf. DESIGN.md).
    /// </summary>
    public class MediaController : EnhancedFinController
    {
        private readonly Db _db;
        private readonly MediaCatalog _catalog;
        private readonly MdblistClient _mdblist;
        private readonly TmdbClient _tmdb;
        private readonly SeerrClient _seerr;
        private readonly JellyfinLibrary _library;

        public MediaController(
            Db db,
            MediaCatalog catalog,
            MdblistClient mdblist,
            TmdbClient tmdb,
            SeerrClient seerr,
            JellyfinLibrary library)
        {
            _db = db;
            _catalog = catalog;
            _mdblist = mdblist;
            _tmdb = tmdb;
            _seerr = seerr;
            _library = library;
        }

        // GET /api/EnhancedFin/v1/media/{mediaKey}?detail=true&enrich=true
        //
        // `enrich` fait entrer le média dans le référentiel s'il n'y est pas, en le
        // récupérant depuis TMDB. C'est une **demande explicite** de l'appelant, pas
        // un effet de bord : la règle « un GET ne crée pas de données » tient, ce
        // qu'un peuplement automatique aurait rompu en accumulant des milliers de
        // fiches jamais touchées (cf. DESIGN.md).
        //
        // Sert aux fiches de découverte, qui ont besoin du logo et du synopsis pour
        // s'afficher correctement alors que l'utilisateur n'a encore rien fait du média.
        [HttpGet("media/{mediaKey}")]
        public async Task<ActionResult> get(
            string mediaKey,
            [FromQuery] bool detail = false,
            [FromQuery] bool enrich = false)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);

            if (enrich)
            {
                // `force` pour une fiche antérieure au bloc « infos » : voir
                // `MediaCatalog.needs_facts`.
                var force = detail && _catalog.needs_facts(mediaKey);
                if (!await _catalog.ensure_exists(mediaKey, force))
                    return problem(404, "Média inconnu", $"'{mediaKey}' est introuvable sur TMDB.");
            }

            using var con = _db.open();

            // Une seule requête pour le média et toutes les données de l'utilisateur.
            // Les sous-requêtes corrélées évitent la multiplication de lignes qu'aurait
            // produite une cascade de LEFT JOIN sur des tables à cardinalités différentes.
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT m.media_key, m.media_type, m.tmdb_id, m.title, m.year,
                       m.poster_url, m.backdrop_url, m.logo_url, m.refreshed_at,
                       m.vote_average, m.vote_count,
                       (SELECT score FROM rating      WHERE user_id=$u AND media_key=m.media_key),
                       (SELECT 1     FROM watchlist   WHERE user_id=$u AND media_key=m.media_key),
                       (SELECT 1     FROM follow      WHERE user_id=$u AND media_key=m.media_key),
                       (SELECT hidden_at FROM hidden_item WHERE user_id=$u AND media_key=m.media_key)
                FROM media m
                WHERE m.media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read())
                return problem(404, "Média inconnu", $"'{mediaKey}' n'est pas dans le référentiel.");

            // ⚠️ `Dictionary` et non un record, à dessein — et c'est un compromis, pas
            // une préférence.
            //
            // Un record donnerait un contrat typé, mais changerait le JSON : System.Text
            // .Json applique `DefaultIgnoreCondition = WhenWritingNull` aux **propriétés**,
            // pas aux entrées de dictionnaire. Mesuré sur le serveur de test : cette route
            // émet aujourd'hui `"hiddenAt": null` et `"progress": null`, là où tout le
            // reste de l'API omet ses nuls. Passer aux records les ferait disparaître.
            //
            // Les rétablir demanderait un [JsonIgnore(Condition = Never)] sur chaque
            // propriété nullable — soit un type qui décrit une anomalie au lieu du
            // modèle. À arbitrer : soit on accepte la rupture (aucun client connu ne lit
            // ces deux champs), soit on garde le dictionnaire.
            var response = new Dictionary<string, object?>
            {
                ["mediaKey"] = rd.GetString(0),
                ["mediaType"] = rd.GetString(1),
                ["tmdbId"] = rd.GetInt32(2),
                ["title"] = rd.GetString(3),
                ["year"] = rd.IsDBNull(4) ? null : rd.GetInt32(4),
                ["posterUrl"] = rd.IsDBNull(5) ? null : rd.GetString(5),
                ["backdropUrl"] = rd.IsDBNull(6) ? null : rd.GetString(6),
                ["logoUrl"] = rd.IsDBNull(7) ? null : rd.GetString(7),
                ["refreshedAt"] = rd.GetString(8),
                // Ajoutés après coup : le SELECT les place avant les colonnes
                // utilisateur, d'où le décalage des index ci-dessous.
                ["voteAverage"] = rd.IsDBNull(9) ? null : rd.GetDouble(9),
                ["voteCount"] = rd.IsDBNull(10) ? null : rd.GetInt32(10),
            };

            // Index alignés sur l'ordre du SELECT :
            // 11 = score, 12 = watchlist, 13 = follow, 14 = hidden_at.
            var me = new Dictionary<string, object?>
            {
                ["rating"] = rd.IsDBNull(11) ? null : rd.GetInt32(11),
                ["inWatchlist"] = !rd.IsDBNull(12),
                ["following"] = !rd.IsDBNull(13),
                ["hidden"] = !rd.IsDBNull(14),
                ["hiddenAt"] = rd.IsDBNull(14) ? null : rd.GetString(14),
            };
            rd.Close();

            me["progress"] = read_last_progress(con, user_id, mediaKey);
            // `genres` reste la liste d'identifiants : le front JS la lit ainsi et
            // la changer casserait le contrat. Les noms arrivent à côté.
            response["genres"] = read_genres(con, mediaKey);
            response["genreNames"] = read_genre_names(con, mediaKey);
            response["me"] = me;

            if (detail)
            {
                response["detail"] = read_detail(con, mediaKey);

                // MDBList et Seerr sont indépendants : lancés ensemble, la fiche
                // n'attend que le plus lent des deux au lieu de leur somme.
                var scores_task = _mdblist.get_scores(mediaKey);
                var availability_task = MediaCatalog.split(mediaKey) is { } key
                    ? _seerr.details(key.Type, key.TmdbId)
                    : Task.FromResult<SeerrDetails?>(null);

                // Notes RT du bloc « infos », en cache 7 jours. Un objet anonyme en
                // camelCase, comme le reste de la route : un record sortirait en
                // PascalCase (`RtCritics`).
                var scores = await scores_task;
                response["scores"] = scores is null
                    ? null
                    : new { rtCritics = scores.RtCritics, rtAudience = scores.RtAudience };

                // Film ou série : disponibilité selon Seerr (5 = complet). Absent si
                // Seerr n'est pas configuré ou n'a pas répondu — le client n'affiche
                // alors ni carte « saisons manquantes » ni « Demander sur Seerr »,
                // plutôt qu'à tort. Le détail par saison est sur `GET seerr/{key}`.
                if (await availability_task is { } availability)
                {
                    response["seerr"] = new { status = availability.Status };
                }
            }

            return Ok(response);
        }

        // GET /api/EnhancedFin/v1/media/{mediaKey}/seasons
        //
        // Saisons d'une série, avec le nombre d'épisodes vus par l'appelant. Lecture
        // seule : ne fait pas entrer la série au référentiel.
        [HttpGet("media/{mediaKey}/seasons")]
        public async Task<ActionResult> seasons(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (MediaCatalog.split(mediaKey) is not { Type: "tv" } parts)
                return problem(400, "Clé de série invalide", "attendu : 'tv:{tmdb_id}'.");

            var watched = watched_count_by_season(user_id, mediaKey);
            var items = (await _tmdb.get_seasons(parts.TmdbId))
                .Select(s => new
                {
                    number = s.SeasonNumber,
                    name = s.Name,
                    episodeCount = s.EpisodeCount,
                    posterUrl = TmdbClient.image_url(s.PosterPath),
                    watchedCount = watched.GetValueOrDefault(s.SeasonNumber),
                })
                .ToList();

            return Ok(new { mediaKey, items });
        }

        // GET /api/EnhancedFin/v1/media/{mediaKey}/seasons/{season}
        //
        // Épisodes d'une saison (TMDB), chacun avec son état « vu » pour l'appelant.
        [HttpGet("media/{mediaKey}/seasons/{season:int}")]
        public async Task<ActionResult> season(string mediaKey, int season)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (MediaCatalog.split(mediaKey) is not { Type: "tv" } parts)
                return problem(400, "Clé de série invalide", "attendu : 'tv:{tmdb_id}'.");
            if (season < 1) return problem(400, "Saison invalide", "le numéro de saison commence à 1.");

            var data = await _tmdb.get_season(parts.TmdbId, season);
            if (data is null) return problem(404, "Saison inconnue", $"saison {season} introuvable sur TMDB.");

            var watched = watched_episodes(user_id, mediaKey, season);
            var episodes = (data.Episodes ?? new List<TmdbEpisode>())
                .OrderBy(e => e.EpisodeNumber)
                .Select(e => new
                {
                    number = e.EpisodeNumber,
                    name = e.Name,
                    // `""` pour un synopsis absent en fr-FR, comme sur les fiches.
                    overview = string.IsNullOrWhiteSpace(e.Overview) ? null : e.Overview,
                    stillUrl = TmdbClient.image_url(e.StillPath),
                    airDate = string.IsNullOrWhiteSpace(e.AirDate) ? null : e.AirDate,
                    runtime = e.Runtime,
                    watched = watched.Contains(e.EpisodeNumber),
                })
                .ToList();

            return Ok(new { mediaKey, number = season, name = data.Name, episodes });
        }

        // GET /api/EnhancedFin/v1/media/{mediaKey}/playable?season=&episode=
        //
        // Le bouton « Lire » d'une fiche, native ou de découverte, suit cette réponse.
        // Sans saison ni épisode, la question porte sur l'œuvre entière ; avec, sur
        // cet épisode précis — un épisode manquant a une fiche mais pas de fichier.
        [HttpGet("media/{mediaKey}/playable")]
        public async Task<ActionResult> playable(
            string mediaKey,
            [FromQuery] int? season = null,
            [FromQuery] int? episode = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (MediaCatalog.split(mediaKey) is not { } parts) return invalid_media_key(mediaKey);
            if ((season is null) != (episode is null))
                return problem(400, "Épisode incomplet", "season et episode vont ensemble.");
            if (season is not null && parts.Type != "tv")
                return problem(400, "Épisode sans série", "season et episode ne s'appliquent qu'à une série.");
            if (season < 0 || episode < 1)
                return problem(400, "Épisode invalide", "season >= 0 et episode >= 1.");

            return Ok(new PlayableResponse(await is_playable(user.Value, mediaKey, season, episode)));
        }

        /// <summary>
        /// Dit si un média est lisible par cet utilisateur.
        ///
        /// Seul point de variation de la route `playable` : tout le reste (validation,
        /// réponse) est commun. `Task` dès maintenant, pour qu'une source asynchrone
        /// puisse s'y ajouter sans changer la signature.
        ///
        /// Parametres :
        /// - user (Guid) : utilisateur dont on applique les droits
        /// - media_key (string) : clé du film ou de la série
        /// - season (int?) : saison de l'épisode visé, null pour l'œuvre entière
        /// - episode (int?) : numéro de l'épisode visé, null pour l'œuvre entière
        ///
        /// Output :
        /// - playable (bool) : vrai si un fichier lisible existe sur le serveur
        /// </summary>
        private Task<bool> is_playable(Guid user, string media_key, int? season, int? episode)
            => Task.FromResult(_library.is_in_library(user, media_key, season, episode));

        /// <summary>
        /// Nombre d'épisodes vus, par saison.
        ///
        /// Parametres :
        /// - user_id (string) : utilisateur
        /// - media_key (string) : clé de la série
        ///
        /// Output :
        /// - counts (Dictionary&lt;int, int&gt;) : saison → épisodes vus
        /// </summary>
        private Dictionary<int, int> watched_count_by_season(string user_id, string media_key)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT p.season, COUNT(*) FROM playback p
                WHERE p.user_id = $u AND p.media_key = $k AND p.season > 0
                  AND " + SqlIsWatched + @"
                GROUP BY p.season";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", media_key);

            var counts = new Dictionary<int, int>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) counts[rd.GetInt32(0)] = rd.GetInt32(1);
            return counts;
        }

        /// <summary>
        /// Numéros des épisodes vus d'une saison.
        ///
        /// Parametres :
        /// - user_id (string) : utilisateur
        /// - media_key (string) : clé de la série
        /// - season (int) : numéro de saison
        ///
        /// Output :
        /// - episodes (HashSet&lt;int&gt;) : épisodes vus
        /// </summary>
        private HashSet<int> watched_episodes(string user_id, string media_key, int season)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT p.episode FROM playback p
                WHERE p.user_id = $u AND p.media_key = $k AND p.season = $s
                  AND " + SqlIsWatched;
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", media_key);
            cmd.Parameters.AddWithValue("$s", season);

            var episodes = new HashSet<int>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) episodes.Add(rd.GetInt32(0));
            return episodes;
        }

        /// <summary>
        /// Dernière position de lecture connue pour ce média.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - user_id (string) : identifiant normalisé de l'appelant
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - progress (object | null) : saison, épisode, position, durée ; null si jamais lu
        /// </summary>
        private static object? read_last_progress(
            Microsoft.Data.Sqlite.SqliteConnection con, string user_id, string media_key)
        {
            using var cmd = con.CreateCommand();
            // `watched` calculé ici avec `SqlIsWatched` : un client qui le déduirait
            // des ticks recopierait la règle, et divergerait au premier changement.
            cmd.CommandText = @"
                SELECT p.season, p.episode, p.position_ticks, p.duration_ticks, p.lang,
                       p.watched_at, p.updated_at, " + SqlIsWatched + @"
                FROM playback p
                WHERE p.user_id = $u AND p.media_key = $k
                ORDER BY p.updated_at DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", media_key);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;

            return new
            {
                season = rd.GetInt32(0),
                episode = rd.GetInt32(1),
                positionTicks = rd.GetInt64(2),
                durationTicks = rd.GetInt64(3),
                lang = rd.IsDBNull(4) ? null : rd.GetString(4),
                watchedAt = rd.IsDBNull(5) ? null : rd.GetString(5),
                updatedAt = rd.GetString(6),
                watched = rd.GetBoolean(7),
            };
        }

        /// <summary>
        /// Identifiants de genres TMDB du média.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - genres (List&lt;int&gt;) : identifiants de genres, liste vide si aucun
        /// </summary>
        private static List<int> read_genres(
            Microsoft.Data.Sqlite.SqliteConnection con, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT genre_id FROM media_genre WHERE media_key = $k ORDER BY genre_id";
            cmd.Parameters.AddWithValue("$k", media_key);

            var genres = new List<int>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) genres.Add(rd.GetInt32(0));

            return genres;
        }

        /// <summary>
        /// Noms des genres du média, dans le **même ordre** que `genres`.
        ///
        /// Le tri est celui de `read_genres`, sur `genre_id` : les deux listes
        /// partent côte à côte dans la même réponse, et un client qui les associe
        /// par position doit tomber juste. Trier celle-ci par nom les désalignait.
        ///
        /// Un genre dont le nom n'a jamais été vu est simplement absent : la
        /// jointure interne l'écarte plutôt que de renvoyer un trou dans la liste.
        /// Les deux listes peuvent donc différer en longueur — elles ne peuvent
        /// pas différer en ordre.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - names (List&lt;string&gt;) : noms, liste vide si aucun n'est connu
        /// </summary>
        private static List<string> read_genre_names(
            Microsoft.Data.Sqlite.SqliteConnection con, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT g.name
                FROM media_genre mg JOIN genre g ON g.genre_id = mg.genre_id
                WHERE mg.media_key = $k
                ORDER BY mg.genre_id";
            cmd.Parameters.AddWithValue("$k", media_key);

            var names = new List<string>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) names.Add(rd.GetString(0));

            return names;
        }

        /// <summary>
        /// Métadonnées lourdes (synopsis, casting), chargées uniquement sur demande.
        ///
        /// Elles vivent dans une table séparée : cast_json pèse 2 à 8 Ko et SQLite lit
        /// la ligne entière. Afficher une liste de 50 posters ne doit pas charger
        /// 50 castings.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - detail (object | null) : synopsis, casting, studios, scénaristes, date de
        ///   sortie (`AAAA-MM-JJ`), réalisation
        /// </summary>
        private static object? read_detail(
            Microsoft.Data.Sqlite.SqliteConnection con, string media_key)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT overview, cast_json, screenwriters, studios, release_date, directors
                FROM media_detail WHERE media_key = $k";
            cmd.Parameters.AddWithValue("$k", media_key);

            using var rd = cmd.ExecuteReader();
            if (!rd.Read()) return null;

            // cast_json est ré-exposé en JSON plutôt qu'en chaîne échappée, pour que
            // le client n'ait pas à le désérialiser une seconde fois.
            //
            // ⚠️ Toutes les lignes ne viennent pas de `serialize_cast`. Celles de la
            // migration recopient verbatim ce que **le client** postait à l'ancien
            // plugin : un `List<Dictionary<string,string>>`, donc des `id` en chaîne
            // — quand il y en a un. Un casting de cette forme faisait échouer le
            // décodage de toute la réponse côté Swift, pas seulement du casting :
            // la fiche perdait logo, image de fond, genres, note ET synopsis, en
            // silence. On préfère un casting absent à une fiche vide.
            object? cast = null;
            if (!rd.IsDBNull(1))
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonElement>(rd.GetString(1));
                    if (is_usable_cast(parsed)) cast = parsed;
                }
                catch (JsonException)
                {
                    // `null` et non la chaîne brute : le champ reste monomorphe.
                    // Un client typé n'a pas à décoder tantôt un tableau, tantôt
                    // du texte.
                    cast = null;
                }
            }

            return new
            {
                overview = rd.IsDBNull(0) ? null : rd.GetString(0),
                cast,
                screenwriters = rd.IsDBNull(2) ? null : rd.GetString(2),
                studios = rd.IsDBNull(3) ? null : rd.GetString(3),
                releaseDate = rd.IsDBNull(4) ? null : rd.GetString(4),
                directors = rd.IsDBNull(5) ? null : rd.GetString(5),
            };
        }

        /// <summary>
        /// Vrai si le casting stocké a la forme que le client sait lire.
        ///
        /// Un tableau d'objets portant chacun un `id` numérique. Tout le reste —
        /// `id` en chaîne, `id` absent, objet isolé, texte — vient de l'ancien
        /// schéma et n'est pas décodable.
        ///
        /// Parametres :
        /// - cast (JsonElement) : contenu désérialisé de cast_json
        ///
        /// Output :
        /// - usable (bool) : vrai si le casting peut être renvoyé tel quel
        /// </summary>
        private static bool is_usable_cast(JsonElement cast)
        {
            if (cast.ValueKind != JsonValueKind.Array) return false;

            foreach (var member in cast.EnumerateArray())
            {
                if (member.ValueKind != JsonValueKind.Object) return false;
                if (!member.TryGetProperty("id", out var id)) return false;
                if (id.ValueKind != JsonValueKind.Number) return false;
            }

            return true;
        }
    }
}
