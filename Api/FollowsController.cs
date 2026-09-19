using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Séries suivies et calendrier des sorties.
    ///
    /// Le calendrier est bâti côté serveur. Le front JS actuel récupère les suivis bruts
    /// puis fait `_flatten_releases()`, `_group_by_day()` et le découpage en quinzaines
    /// en JavaScript — logique qu'il faudrait réécrire en Swift, puis une troisième fois
    /// pour tvOS. Renvoyer des jours prêts à afficher supprime cette duplication.
    ///
    /// Les dates de sortie viennent de TMDB. La disponibilité sur les sources externes
    /// est un tout autre sujet, qui relève de un autre plugin.
    /// </summary>
    public class FollowsController : EnhancedFinController
    {
        private readonly Db _db;
        private readonly MediaCatalog _catalog;

        public FollowsController(Db db, MediaCatalog catalog)
        {
            _db = db;
            _catalog = catalog;
        }

        // GET /api/EnhancedFin/v1/me/follows
        [HttpGet("me/follows")]
        public ActionResult list([FromQuery] int? limit = null, [FromQuery] int? offset = null)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (page_of(limit, offset) is not { } page) return invalid_page();

            const string where = @"
                FROM follow f JOIN media m ON m.media_key = f.media_key
                WHERE f.user_id = $u";

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT f.media_key, f.added_at, m.media_type, m.title, m.year, m.poster_url,
                       (SELECT MIN(air_date) FROM release r
                        WHERE r.media_key = f.media_key AND r.air_date >= $today)"
                + where + @"
                ORDER BY f.added_at DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$today", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            var items = new List<object>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                items.Add(new
                {
                    mediaKey = rd.GetString(0),
                    addedAt = rd.GetString(1),
                    mediaType = rd.GetString(2),
                    title = rd.GetString(3),
                    year = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                    posterUrl = rd.IsDBNull(5) ? null : rd.GetString(5),
                    // Prochaine sortie à venir : ce que l'UI affiche sous le titre.
                    nextAirDate = rd.IsDBNull(6) ? null : rd.GetString(6),
                });
            }
            rd.Close();

            return Ok(new { items, total = count(con, "SELECT COUNT(*)" + where, cmd) });
        }

        // PUT /api/EnhancedFin/v1/me/follows/{mediaKey}
        [HttpPut("me/follows/{mediaKey}")]
        public async Task<ActionResult> follow(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            return write_for_media(_db, @"
                INSERT INTO follow (user_id, media_key, added_at) VALUES ($u, $k, $now)
                ON CONFLICT(user_id, media_key) DO NOTHING",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", mediaKey);
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                },
                mediaKey);
        }

        // DELETE /api/EnhancedFin/v1/me/follows/{mediaKey}
        [HttpDelete("me/follows/{mediaKey}")]
        public ActionResult unfollow(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM follow WHERE user_id = $u AND media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);

            return cmd.ExecuteNonQuery() == 0
                ? problem(404, "Non suivi", $"'{mediaKey}' n'était pas suivi.")
                : NoContent();
        }

        // GET /api/EnhancedFin/v1/me/calendar?from=2026-09-01&to=2026-09-30
        [HttpGet("me/calendar")]
        public ActionResult calendar([FromQuery] string? from = null, [FromQuery] string? to = null)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            // Par défaut : la quinzaine en cours, la fenêtre qu'affiche le front actuel.
            var start = parse_date(from) ?? DateTime.UtcNow.Date;
            var end = parse_date(to) ?? start.AddDays(14);

            if (end < start)
                return problem(400, "Plage invalide", "'to' doit être postérieur à 'from'.");
            if ((end - start).TotalDays > 366)
                return problem(400, "Plage trop large", "la plage ne peut excéder 366 jours.");

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            // `m.media_type` est projeté : un film et un épisode ne se formatent pas
            // pareil. Une sortie de film est stockée en saison 0 / épisode 0, forme
            // qu'un client rendrait en « S0E00 » faute de savoir la distinguer.
            cmd.CommandText = @"
                SELECT r.air_date, r.media_key, r.season, r.episode, r.episode_name,
                       m.title, m.poster_url, m.media_type
                FROM release r
                JOIN follow f ON f.media_key = r.media_key AND f.user_id = $u
                JOIN media  m ON m.media_key = r.media_key
                WHERE r.air_date IS NOT NULL
                  AND r.air_date >= $from AND r.air_date <= $to
                ORDER BY r.air_date, m.title, r.season, r.episode";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$from", start.ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$to", end.ToString("yyyy-MM-dd"));

            // Regroupement par jour fait ici : le client n'a plus qu'à afficher.
            var by_day = new Dictionary<string, List<object>>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var day = rd.GetString(0)[..10];
                if (!by_day.TryGetValue(day, out var releases))
                    by_day[day] = releases = new List<object>();

                var media_type = rd.GetString(7);

                releases.Add(new
                {
                    mediaKey = rd.GetString(1),
                    mediaType = media_type,
                    season = rd.GetInt32(2),
                    episode = rd.GetInt32(3),
                    // Nul pour un film : `refresh_movie_release` y recopie le titre
                    // faute de mieux, mais `title` le porte déjà juste en dessous.
                    // Le répéter ici ferait afficher deux fois la même chose au
                    // client qui traite la ligne comme un épisode.
                    episodeName = media_type == "movie" || rd.IsDBNull(4) ? null : rd.GetString(4),
                    title = rd.GetString(5),
                    posterUrl = rd.IsDBNull(6) ? null : rd.GetString(6),
                });
            }

            // Les dates sont au format ISO, donc l'ordre lexicographique est l'ordre
            // chronologique : un tri ordinal suffit.
            var days = by_day.Keys
                .OrderBy(day => day, StringComparer.Ordinal)
                .Select(day => (object)new { date = day, releases = by_day[day] })
                .ToList();

            return Ok(new
            {
                from = start.ToString("yyyy-MM-dd"),
                to = end.ToString("yyyy-MM-dd"),
                days,
            });
        }

        /// <summary>
        /// Analyse une date 'yyyy-MM-dd' fournie en paramètre de requête.
        ///
        /// Parametres :
        /// - raw (string | null) : date brute
        ///
        /// Output :
        /// - date (DateTime | null) : date analysée, null si absente ou invalide
        /// </summary>
        private static DateTime? parse_date(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            return DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out var parsed)
                ? parsed
                : null;
        }
    }
}
