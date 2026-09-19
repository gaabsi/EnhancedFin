using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
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
        private readonly Db _db;
        private readonly MediaCatalog _catalog;

        public PlaybackController(Db db, MediaCatalog catalog)
        {
            _db = db;
            _catalog = catalog;
        }

        // GET /api/EnhancedFin/v1/me/continue-watching?limit=20
        [HttpGet("me/continue-watching")]
        public ActionResult continue_watching([FromQuery] int limit = 20)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (limit is < 1 or > 200)
                return problem(400, "Limite invalide", "limit doit être compris entre 1 et 200.");

            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // Une seule ligne par média (le dernier épisode touché), d'où ROW_NUMBER.
            //
            // Règle de masquage reprise de l'ancien plugin : l'item est caché tant que
            // hidden_at >= updated_at. Reprendre la lecture rafraîchit updated_at et le
            // fait donc réapparaître automatiquement — comportement voulu, pas un effet
            // de bord.
            cmd.CommandText = @"
                WITH dernier AS (
                    SELECT p.*, ROW_NUMBER() OVER (
                               PARTITION BY p.media_key ORDER BY p.updated_at DESC
                           ) AS rang
                    FROM playback p
                    WHERE p.user_id = $u
                )
                SELECT d.media_key, d.season, d.episode, d.position_ticks,
                       d.duration_ticks, d.lang, d.updated_at,
                       m.media_type, m.title, m.year, m.poster_url, m.backdrop_url
                FROM dernier d
                JOIN media m ON m.media_key = d.media_key
                LEFT JOIN hidden_item h
                       ON h.user_id = $u AND h.media_key = d.media_key
                WHERE d.rang = 1
                  AND d.position_ticks > 0
                  AND (h.hidden_at IS NULL OR h.hidden_at < d.updated_at)
                  AND (d.duration_ticks <= 0
                       OR d.position_ticks < d.duration_ticks * $ratio)
                ORDER BY d.updated_at DESC
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$ratio", FinishedRatio);
            cmd.Parameters.AddWithValue("$limit", limit);

            var items = new List<object>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var position = rd.GetInt64(3);
                var duration = rd.GetInt64(4);

                items.Add(new
                {
                    mediaKey = rd.GetString(0),
                    season = rd.GetInt32(1),
                    episode = rd.GetInt32(2),
                    positionTicks = position,
                    durationTicks = duration,
                    // Calculé côté serveur : tous les clients en ont besoin pour la
                    // barre de progression, autant ne pas le refaire trois fois.
                    progress = duration > 0 ? Math.Round((double)position / duration, 4) : 0,
                    lang = rd.IsDBNull(5) ? null : rd.GetString(5),
                    updatedAt = rd.GetString(6),
                    mediaType = rd.GetString(7),
                    title = rd.GetString(8),
                    year = rd.IsDBNull(9) ? (int?)null : rd.GetInt32(9),
                    posterUrl = rd.IsDBNull(10) ? null : rd.GetString(10),
                    backdropUrl = rd.IsDBNull(11) ? null : rd.GetString(11),
                });
            }

            return Ok(new { items, total = items.Count });
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
        public async Task<ActionResult> upsert_progress(string mediaKey, [FromBody] ProgressRequest body)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(mediaKey)) return invalid_media_key(mediaKey);

            // Le corps est validé **avant** `ensure_media`, qui fait un appel réseau
            // vers TMDB puis une écriture : un corps invalide ne doit rien coûter au
            // serveur. C'est l'ordre que suit déjà RatingsController.
            if (body.Season < 0 || body.Episode < 0)
                return problem(400, "Position invalide", "season et episode doivent être positifs.");
            if (body.PositionTicks < 0 || body.DurationTicks < 0)
                return problem(400, "Durée invalide", "les ticks doivent être positifs.");

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
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM hidden_item WHERE user_id = $u AND media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);

            return cmd.ExecuteNonQuery() == 0
                ? problem(404, "Non masqué", $"'{mediaKey}' n'était pas masqué.")
                : NoContent();
        }
    }
}
