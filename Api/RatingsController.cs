using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    public record RateRequest(int Score);

    /// <summary>
    /// Une note, telle que renvoyée par `GET /me/ratings`.
    ///
    /// Décrit le contrat au format exact du fil — voir l'avertissement sur la casse
    /// dans <see cref="ListResponse{T}"/>. `year` et `posterUrl` sont nullables et
    /// **absents** du JSON quand ils sont nuls (`DefaultIgnoreCondition =
    /// WhenWritingNull` côté Jellyfin), pas présents à `null`.
    /// </summary>
    public record RatingItem(
        string mediaKey,
        int score,
        string ratedAt,
        string title,
        int? year,
        string? posterUrl,
        string? backdropUrl,
        bool inLibrary,
        string? jellyfinId);

    /// <summary>
    /// Un média vu mais pas encore noté, renvoyé par `GET /me/ratings/pending`.
    ///
    /// Forme volontairement distincte de <see cref="RatingItem"/> : les quatre champs
    /// communs ne justifient pas un type partagé, qui imposerait soit de l'imbrication
    /// dans le JSON, soit un héritage invisible à la lecture.
    /// </summary>
    public record PendingRatingItem(
        string mediaKey,
        string mediaType,
        string title,
        int? year,
        string? posterUrl,
        string? backdropUrl,
        int watchedEpisodes,
        string lastWatchedAt,
        bool inLibrary,
        string? jellyfinId);

    /// <summary>
    /// Notes utilisateur. Sert de patron aux autres controllers :
    /// - [Authorize] systématique
    /// - aucun paramètre userId : l'identité vient du token
    /// - erreurs en problem+json (RFC 7807) avec extensions de compat JS
    /// </summary>
    public class RatingsController : EnhancedFinController
    {

        /// <summary>
        /// Nombre d'épisodes d'une série à avoir vus avant de proposer de la noter.
        /// Reprend le seuil de l'ancien plugin : noter une série sur un seul épisode
        /// n'a pas de sens.
        /// </summary>
        private const int MinEpisodesForTv = 5;

        private readonly Db _db;
        private readonly MediaCatalog _catalog;
        private readonly JellyfinLibrary _library;

        public RatingsController(Db db, MediaCatalog catalog, JellyfinLibrary library)
        {
            _db = db;
            _catalog = catalog;
            _library = library;
        }

        // GET /api/EnhancedFin/v1/me/ratings
        [HttpGet("me/ratings")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<ListResponse<RatingItem>> list(
            [FromQuery] string? type = null,
            [FromQuery] int? score = null,
            [FromQuery] int? limit = null,
            [FromQuery] int? offset = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (!is_valid_media_type(type)) return invalid_media_type();
            if (page_of(limit, offset) is not { } page) return invalid_page();

            var user_id = user.Value.ToString("D");

            const string where = @"
                FROM rating r JOIN media m ON m.media_key = r.media_key
                WHERE r.user_id = $u
                  AND ($t IS NULL OR m.media_type = $t)
                  AND ($s IS NULL OR r.score = $s)";

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT r.media_key, r.score, r.rated_at, m.title, m.year, m.poster_url,
                       m.backdrop_url"
                + where + @"
                ORDER BY r.rated_at DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$t", (object?)type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$s", (object?)score ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            // Index résolu une fois pour toute la liste, pas une requête par ligne.
            var in_library = _library.index(user.Value);

            var items = new List<RatingItem>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var media_key = rd.GetString(0);
                in_library.TryGetValue(media_key, out var match);

                items.Add(new RatingItem(
                    mediaKey: media_key,
                    score: rd.GetInt32(1),
                    ratedAt: rd.GetString(2),
                    title: rd.GetString(3),
                    year: rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                    posterUrl: rd.IsDBNull(5) ? null : rd.GetString(5),
                    backdropUrl: rd.IsDBNull(6) ? null : rd.GetString(6),
                    inLibrary: match is not null,
                    jellyfinId: match?.JellyfinId.ToString("D")));
            }
            rd.Close();

            // `total` compte **toutes** les notes, pas la page : c'est ce qui permet au
            // client de savoir qu'il en reste. L'enveloppe porte déjà le champ.
            return Ok(new ListResponse<RatingItem>(items, count(con, "SELECT COUNT(*)" + where, cmd)));
        }


        // GET /api/EnhancedFin/v1/me/ratings/pending
        // Vu mais pas encore noté. Remplace l'ancien `ToRate`.
        [HttpGet("me/ratings/pending")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<ListResponse<PendingRatingItem>> pending(
            [FromQuery] int? limit = null,
            [FromQuery] int? offset = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (page_of(limit, offset) is not { } page) return invalid_page();

            var user_id = user.Value.ToString("D");

            // Une requête là où l'ancien `ToRate` agrégeait deux tables puis faisait
            // un appel TMDB par item pour retrouver titre et affiche. Le référentiel
            // les porte déjà.
            var body = @"
                FROM playback p
                JOIN media m ON m.media_key = p.media_key
                WHERE p.user_id = $u
                  AND " + SqlIsWatched + @"
                  AND NOT EXISTS (SELECT 1 FROM rating r
                                  WHERE r.user_id = $u AND r.media_key = p.media_key)
                GROUP BY p.media_key
                HAVING m.media_type = 'movie' OR COUNT(*) >= $min_eps";

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT p.media_key, m.media_type, m.title, m.year, m.poster_url,
                       COUNT(*) AS vus, MAX(p.updated_at) AS dernier, m.backdrop_url"
                + body + @"
                ORDER BY dernier DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$min_eps", MinEpisodesForTv);
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            var in_library = _library.index(user.Value);

            var items = new List<PendingRatingItem>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var media_key = rd.GetString(0);
                in_library.TryGetValue(media_key, out var match);

                items.Add(new PendingRatingItem(
                    mediaKey: media_key,
                    mediaType: rd.GetString(1),
                    title: rd.GetString(2),
                    year: rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3),
                    posterUrl: rd.IsDBNull(4) ? null : rd.GetString(4),
                    watchedEpisodes: rd.GetInt32(5),
                    lastWatchedAt: rd.GetString(6),
                    backdropUrl: rd.IsDBNull(7) ? null : rd.GetString(7),
                    inLibrary: match is not null,
                    jellyfinId: match?.JellyfinId.ToString("D")));
            }
            rd.Close();

            // GROUP BY : le total est le nombre de **groupes**, d'où la sous-requête.
            return Ok(new ListResponse<PendingRatingItem>(
                items,
                count(con, "SELECT COUNT(*) FROM (SELECT p.media_key" + body + ")", cmd)));
        }

        // PUT /api/EnhancedFin/v1/me/ratings/{mediaKey}
        // Idempotent : rejouer l'appel ne crée pas de doublon (upsert sur la PK).
        [HttpPut("me/ratings/{mediaKey}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<ActionResult> upsert(string mediaKey, [FromBody] RateRequest body)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            // Le corps est validé **avant** `ensure_media`, qui fait un appel réseau et
            // une écriture : un corps invalide ne doit rien coûter au serveur.
            if (body.Score is not (-1 or 1 or 2))
                return problem(400, "Score invalide", "score doit valoir -1 (pas aimé), 1 (bien) ou 2 (adoré).");

            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            return write_for_media(_db, @"
                INSERT INTO rating (user_id, media_key, score, rated_at)
                VALUES ($u, $k, $s, $now)
                ON CONFLICT(user_id, media_key) DO UPDATE SET score = $s, rated_at = $now",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", mediaKey);
                    cmd.Parameters.AddWithValue("$s", body.Score);
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                },
                mediaKey);
        }

        // DELETE /api/EnhancedFin/v1/me/ratings/{mediaKey}
        [HttpDelete("me/ratings/{mediaKey}")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult remove(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM rating WHERE user_id = $u AND media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);

            return cmd.ExecuteNonQuery() == 0
                ? problem(404, "Note introuvable", $"Aucune note sur '{mediaKey}'.")
                : NoContent();
        }

    }
}
