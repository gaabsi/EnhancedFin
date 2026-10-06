using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using MediaBrowser.Controller.Configuration;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    public record ProgressRequest(
        int Season,
        int Episode,
        long PositionTicks,
        long DurationTicks,
        string? Lang,
        bool? Watched);

    /// <summary>Des épisodes d'une même saison. Un film : saison 0, épisode 0.</summary>
    public record WatchedRequest(int Season, List<int>? Episodes);

    /// <summary>
    /// Progression de lecture, Continue Watching et masquage.
    ///
    /// Ces trois sujets partagent une table (`playback`) et une règle commune de
    /// visibilité, d'où leur regroupement ici. `playback` remplace à elle seule
    /// `external_watched` et `anime_watched_v2`, qui avaient la même structure à
    /// l'identifiant près et devaient être fusionnées à chaque lecture par
    /// `WatchedProjector`.
    /// </summary>
    public class PlaybackController : EnhancedFinController
    {
        /// <summary>
        /// Bornes larges (les plus longues séries dépassent à peine 50 saisons, un anime
        /// quelques milliers d'épisodes) : elles empêchent seulement de remplir la base
        /// de lignes absurdes.
        /// </summary>
        private const int MaxSeason = 1000;
        private const int MaxEpisode = 10000;

        private static readonly Regex LanguageCode = new("^[a-z]{2,3}(-[a-z0-9]{2,8})?$", RegexOptions.Compiled);

        private readonly Db _db;
        private readonly MediaCatalog _catalog;
        private readonly TmdbClient _tmdb;
        private readonly IServerConfigurationManager _config;

        public PlaybackController(Db db, MediaCatalog catalog, TmdbClient tmdb, IServerConfigurationManager config)
        {
            _db = db;
            _catalog = catalog;
            _tmdb = tmdb;
            _config = config;
        }

        // GET /api/EnhancedFin/v1/me/continue-watching?limit=20
        //
        // Une entrée par média : le dernier épisode touché s'il est en cours ; pour une
        // série dont il est vu, le prochain à voir (`next_up`, position 0), comme le
        // « À suivre » de Jellyfin. Un film vu, une série à jour : absents.
        //
        // Comme Jellyfin, une ouverture sous `MinResumePct` du serveur (5 % par défaut)
        // ne compte pas : un film ouvert puis fermé n'est pas une reprise, une série
        // seulement entrouverte n'apparaît pas (voir `last_touched`).
        [HttpGet("me/continue-watching")]
        public async Task<ActionResult> continue_watching([FromQuery] int limit = 20)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (limit is < 1 or > 200)
                return problem(400, "Limite invalide", "limit doit être compris entre 1 et 200.");

            var items = new List<object>();
            foreach (var row in last_touched(user.Value, _config.Configuration.MinResumePct / 100.0))
            {
                if (items.Count == limit) break;

                var (season, episode, position, duration) = (row.Season, row.Episode, row.PositionTicks, row.DurationTicks);
                if (row.Watched)
                {
                    if (row.MediaType != "tv"
                        || await _catalog.next_up(user.Value, row.MediaKey, HttpContext.RequestAborted) is not { } next)
                        continue;
                    (season, episode, position, duration) = (next.Season, next.Episode, 0, 0);
                }

                var details = row.MediaType == "tv" && MediaCatalog.split(row.MediaKey) is { } parts
                    ? (await _tmdb.get_season(parts.TmdbId, season, HttpContext.RequestAborted))?.Episodes?
                        .FirstOrDefault(e => e.EpisodeNumber == episode)
                    : null;

                items.Add(new
                {
                    mediaKey = row.MediaKey,
                    season,
                    episode,
                    positionTicks = position,
                    durationTicks = duration,
                    // Calculé côté serveur : tous les clients en ont besoin pour la
                    // barre de progression, autant ne pas le refaire trois fois.
                    progress = duration > 0 ? Math.Round((double)position / duration, 4) : 0,
                    lang = row.Lang,
                    updatedAt = row.UpdatedAt,
                    mediaType = row.MediaType,
                    title = row.Title,
                    year = row.Year,
                    posterUrl = row.PosterUrl,
                    backdropUrl = row.BackdropUrl,
                    // Épisode seulement (TMDB).
                    episodeName = details?.Name,
                });
            }

            return Ok(new { items, total = items.Count });
        }

        private sealed record LastTouched(
            string MediaKey, int Season, int Episode, long PositionTicks, long DurationTicks, bool Watched,
            string? Lang, string UpdatedAt, string MediaType, string Title, int? Year, string? PosterUrl, string? BackdropUrl);

        /// <summary>
        /// Le dernier épisode touché de chaque média (un film : sa ligne), du plus récent
        /// au plus ancien, sans les médias masqués. Une ouverture sous le seuil de
        /// reprise (ni vue, ni au-delà de `min_resume`) est ignorée : TLOU E8 entrouvert
        /// ne cache pas E1 en cours.
        ///
        /// Règle de masquage : l'item est caché tant que hidden_at >= updated_at.
        /// Reprendre la lecture rafraîchit updated_at et le fait donc réapparaître
        /// automatiquement — comportement voulu, pas un effet de bord.
        ///
        /// Parametres :
        /// - user (Guid) : utilisateur
        /// - min_resume (double) : seuil de reprise du serveur, en fraction (0,05)
        ///
        /// Output :
        /// - rows (List&lt;LastTouched&gt;) : une ligne par média, `Watched` selon `SqlIsWatched`
        /// </summary>
        private List<LastTouched> last_touched(Guid user, double min_resume)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // Une seule ligne par média (le dernier épisode touché), d'où ROW_NUMBER.
            cmd.CommandText = $@"
                WITH dernier AS (
                    SELECT p.*, ROW_NUMBER() OVER (
                               PARTITION BY p.media_key ORDER BY p.updated_at DESC
                           ) AS rang
                    FROM playback p
                    WHERE p.user_id = $u
                      AND ({SqlIsWatched} OR p.position_ticks >= p.duration_ticks * $min)
                )
                SELECT p.media_key, p.season, p.episode, p.position_ticks,
                       p.duration_ticks, {SqlIsWatched}, p.lang, p.updated_at,
                       m.media_type, m.title, m.year, m.poster_url, m.backdrop_url
                FROM dernier p
                JOIN media m ON m.media_key = p.media_key
                LEFT JOIN hidden_item h
                       ON h.user_id = $u AND h.media_key = p.media_key
                WHERE p.rang = 1
                  AND (h.hidden_at IS NULL OR h.hidden_at < p.updated_at)
                ORDER BY p.updated_at DESC";
            cmd.Parameters.AddWithValue("$u", user.ToString("D"));
            cmd.Parameters.AddWithValue("$min", min_resume);

            var rows = new List<LastTouched>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                rows.Add(new LastTouched(
                    rd.GetString(0), rd.GetInt32(1), rd.GetInt32(2), rd.GetInt64(3), rd.GetInt64(4), rd.GetInt64(5) == 1,
                    rd.IsDBNull(6) ? null : rd.GetString(6), rd.GetString(7), rd.GetString(8), rd.GetString(9),
                    rd.IsDBNull(10) ? null : rd.GetInt32(10),
                    rd.IsDBNull(11) ? null : rd.GetString(11),
                    rd.IsDBNull(12) ? null : rd.GetString(12)));
            }
            return rows;
        }

        // GET /api/EnhancedFin/v1/me/next-up/{mediaKey}
        //
        // Le prochain épisode à voir d'une série (« Reprendre S2E3 ») : règle et sources
        // dans `MediaCatalog.next_up`. 404 quand la série est à jour.
        [HttpGet("me/next-up/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> next_up(string mediaKey)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (MediaCatalog.split(mediaKey) is not { } parts) return invalid_media_key(mediaKey);
            if (parts.Type != "tv")
                return problem(400, "Pas une série", "seule une série a un prochain épisode.");

            return await _catalog.next_up(user.Value, mediaKey, HttpContext.RequestAborted) is { } next
                ? Ok(new { mediaKey, season = next.Season, episode = next.Episode })
                : problem(404, "Rien à voir", $"'{mediaKey}' est à jour ou inconnue de TMDB.");
        }

        // GET /api/EnhancedFin/v1/me/progress/{mediaKey}
        // Toute la progression d'un média : utile pour cocher les épisodes vus d'une série.
        [HttpGet("me/progress/{mediaKey}")]
        public ActionResult progress(
            string mediaKey,
            [FromQuery] int? limit = null,
            [FromQuery] int? offset = null)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);
            if (page_of(limit, offset) is not { } page) return invalid_page();

            // Une série longue dépasse les 200 épisodes : la pagination est utile ici,
            // pas seulement défensive.
            const string where = @"
                FROM playback
                WHERE user_id = $u AND media_key = $k";

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT season, episode, position_ticks, duration_ticks, lang,
                       watched_at, updated_at"
                + where + @"
                ORDER BY season, episode
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            var items = new List<object>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                items.Add(new
                {
                    season = rd.GetInt32(0),
                    episode = rd.GetInt32(1),
                    positionTicks = rd.GetInt64(2),
                    durationTicks = rd.GetInt64(3),
                    lang = rd.IsDBNull(4) ? null : rd.GetString(4),
                    watchedAt = rd.IsDBNull(5) ? null : rd.GetString(5),
                    updatedAt = rd.GetString(6),
                });
            }
            rd.Close();

            return Ok(new { mediaKey, items, total = count(con, "SELECT COUNT(*)" + where, cmd) });
        }

        // PUT /api/EnhancedFin/v1/me/progress/{mediaKey}
        [HttpPut("me/progress/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> upsert_progress(string mediaKey, [FromBody] ProgressRequest body)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);

            // Le corps est validé **avant** `ensure_media`, qui fait un appel réseau
            // vers TMDB puis une écriture : un corps invalide ne doit rien coûter au
            // serveur. C'est l'ordre que suit déjà RatingsController.
            if (body.Season is < 0 or > MaxSeason || body.Episode is < 0 or > MaxEpisode)
                return problem(400, "Position invalide", $"season entre 0 et {MaxSeason}, episode entre 0 et {MaxEpisode}.");
            if (body.PositionTicks < 0 || body.DurationTicks < 0)
                return problem(400, "Durée invalide", "les ticks doivent être positifs.");
            if (body.Lang is { } lang && !LanguageCode.IsMatch(lang))
                return problem(400, "Langue invalide", "lang doit être un code de langue (« fr », « pt-br »).");

            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // watched_at n'est posé qu'une fois : il marque la première complétion et
            // ne doit pas être écrasé par un visionnage ultérieur.
            var watched = body.Watched == true
                || (body.DurationTicks > 0 && body.PositionTicks >= body.DurationTicks * FinishedRatio);

            return write_for_media(_db, @"
                INSERT INTO playback (user_id, media_key, season, episode,
                                      position_ticks, duration_ticks, lang,
                                      watched_at, updated_at)
                VALUES ($u, $k, $se, $ep, $pos, $dur, $lang, $watched, $now)
                ON CONFLICT(user_id, media_key, season, episode) DO UPDATE SET
                    position_ticks = excluded.position_ticks,
                    duration_ticks = excluded.duration_ticks,
                    lang           = COALESCE(excluded.lang, playback.lang),
                    watched_at     = COALESCE(playback.watched_at, excluded.watched_at),
                    updated_at     = excluded.updated_at",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", mediaKey);
                    cmd.Parameters.AddWithValue("$se", body.Season);
                    cmd.Parameters.AddWithValue("$ep", body.Episode);
                    cmd.Parameters.AddWithValue("$pos", body.PositionTicks);
                    cmd.Parameters.AddWithValue("$dur", body.DurationTicks);
                    cmd.Parameters.AddWithValue("$lang", (object?)body.Lang ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$watched", watched ? now : (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("$now", now);
                },
                mediaKey);
        }

        // PUT /api/EnhancedFin/v1/me/watched/{mediaKey}  — corps { season, episodes }
        //
        // Marque des épisodes vus, en une transaction : une saison entière est un seul
        // appel. `PUT /me/progress` ne convient pas ici — un épisode à la fois, et il ne
        // sait pas démarquer.
        [HttpPut("me/watched/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> mark_watched(string mediaKey, [FromBody] WatchedRequest body)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);
            if (invalid_watched(body) is { } invalid) return invalid;

            // Après la validation : `ensure_media` peut partir vers TMDB.
            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // Nouvelle ligne : durée 0, donc vue au sens de `SqlIsWatched`. Ligne
            // existante (lecture entamée) : position poussée à la fin, pour la même
            // raison. `watched_at` garde la première complétion.
            return write_episodes(user_id, mediaKey, body, @"
                INSERT INTO playback (user_id, media_key, season, episode,
                                      position_ticks, duration_ticks, watched_at, updated_at)
                VALUES ($u, $k, $se, $ep, 0, 0, $now, $now)
                ON CONFLICT(user_id, media_key, season, episode) DO UPDATE SET
                    position_ticks = playback.duration_ticks,
                    watched_at     = COALESCE(playback.watched_at, excluded.watched_at),
                    updated_at     = excluded.updated_at",
                now);
        }

        // DELETE /api/EnhancedFin/v1/me/watched/{mediaKey}  — corps { season, episodes }
        //
        // Démarque : la ligne disparaît. Une ligne restante compterait encore comme vue
        // dès que sa durée est inconnue (`SqlIsWatched`).
        [HttpDelete("me/watched/{mediaKey}")]
        public ActionResult unmark_watched(string mediaKey, [FromBody] WatchedRequest body)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);
            if (invalid_watched(body) is { } invalid) return invalid;

            return write_episodes(user_id, mediaKey, body, @"
                DELETE FROM playback
                WHERE user_id = $u AND media_key = $k AND season = $se AND episode = $ep",
                now: null);
        }

        /// <summary>
        /// Valide un corps `WatchedRequest`.
        ///
        /// Parametres :
        /// - body (WatchedRequest) : corps reçu
        ///
        /// Output :
        /// - error (ObjectResult | null) : 400, ou null si le corps est valide
        /// </summary>
        private ObjectResult? invalid_watched(WatchedRequest body)
        {
            if (body.Season is < 0 or > MaxSeason || body.Episodes is not { Count: > 0 })
                return problem(400, "Corps invalide", $"season entre 0 et {MaxSeason}, et au moins un épisode attendus.");
            // Borne large (une saison dépasse rarement 30 épisodes, un long anime 200) :
            // elle empêche seulement une transaction démesurée.
            if (body.Episodes.Count > 500 || body.Episodes.Exists(e => e is < 0 or > MaxEpisode))
                return problem(400, "Épisodes invalides", $"entre 1 et 500 numéros, de 0 à {MaxEpisode}.");
            return null;
        }

        /// <summary>
        /// Applique une requête à chaque épisode du corps, en une transaction.
        ///
        /// Parametres :
        /// - user_id (string) : utilisateur
        /// - media_key (string) : clé du média
        /// - body (WatchedRequest) : saison et épisodes
        /// - sql (string) : requête, paramètres `$u $k $se $ep` et `$now` si fourni
        /// - now (string | null) : horodatage, null si la requête n'en a pas besoin
        ///
        /// Output :
        /// - result (ActionResult) : 204, ou 404 si le média manque au référentiel
        /// </summary>
        private ActionResult write_episodes(
            string user_id, string media_key, WatchedRequest body, string sql, string? now)
        {
            using var con = _db.open();
            using var transaction = con.BeginTransaction();
            try
            {
                foreach (var episode in new HashSet<int>(body.Episodes!))
                {
                    using var cmd = con.CreateCommand();
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", media_key);
                    cmd.Parameters.AddWithValue("$se", body.Season);
                    cmd.Parameters.AddWithValue("$ep", episode);
                    if (now is not null) cmd.Parameters.AddWithValue("$now", now);
                    cmd.ExecuteNonQuery();
                }
                transaction.Commit();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex) when (is_unknown_media(ex))
            {
                return problem(404, "Média inconnu", "le média doit d'abord être enregistré dans `media`.");
            }

            return NoContent();
        }

        // GET /api/EnhancedFin/v1/me/hidden
        // Les items masqués, pour pouvoir les restaurer depuis un écran dédié.
        [HttpGet("me/hidden")]
        public ActionResult hidden([FromQuery] int? limit = null, [FromQuery] int? offset = null)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (page_of(limit, offset) is not { } page) return invalid_page();

            const string where = @"
                FROM hidden_item h JOIN media m ON m.media_key = h.media_key
                WHERE h.user_id = $u";

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT h.media_key, h.hidden_at, m.media_type, m.title, m.year, m.poster_url"
                + where + @"
                ORDER BY h.hidden_at DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            var items = new List<object>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                items.Add(new
                {
                    mediaKey = rd.GetString(0),
                    hiddenAt = rd.GetString(1),
                    mediaType = rd.GetString(2),
                    title = rd.GetString(3),
                    year = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                    posterUrl = rd.IsDBNull(5) ? null : rd.GetString(5),
                });
            }
            rd.Close();

            return Ok(new { items, total = count(con, "SELECT COUNT(*)" + where, cmd) });
        }

        // PUT /api/EnhancedFin/v1/me/hidden/{mediaKey}
        [HttpPut("me/hidden/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> hide(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            return write_for_media(_db, @"
                INSERT INTO hidden_item (user_id, media_key, hidden_at) VALUES ($u, $k, $now)
                ON CONFLICT(user_id, media_key) DO UPDATE SET hidden_at = excluded.hidden_at",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", mediaKey);
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                },
                mediaKey);
        }

        // DELETE /api/EnhancedFin/v1/me/hidden/{mediaKey}
        [HttpDelete("me/hidden/{mediaKey}")]
        public ActionResult unhide(string mediaKey)
        {
            return delete_for_media(_db, "hidden_item", mediaKey, "Non masqué", $"'{mediaKey}' n'était pas masqué.");
        }
    }
}
