using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Une entrée de watchlist, telle que renvoyée par `GET /me/watchlist`.
    ///
    /// Propriétés en camelCase à dessein — voir <see cref="ListResponse{T}"/>.
    ///
    /// `inLibrary` et `jellyfinId` disent si le média est lisible sur ce serveur :
    /// c'est ce qui permet au client de proposer « Lire » plutôt que d'ouvrir une
    /// fiche vide. Ils valent respectivement `false` et `null` sinon.
    /// </summary>
    public record WatchlistItem(
        string mediaKey,
        string addedAt,
        string mediaType,
        string title,
        int? year,
        string? posterUrl,
        string? backdropUrl,
        int? rating,
        bool inLibrary,
        string? jellyfinId);

    /// <summary>
    /// Watchlist : ce que l'utilisateur veut regarder.
    ///
    /// L'ancien schéma stockait les métadonnées (titre, poster, synopsis, genres en CSV)
    /// sur chaque ligne de watchlist. Elles vivent désormais dans `media`, et la table
    /// ne porte plus que l'appartenance et sa date.
    /// </summary>
    public class WatchlistController : EnhancedFinController
    {
        private readonly Db _db;
        private readonly MediaCatalog _catalog;
        private readonly JellyfinLibrary _library;

        public WatchlistController(Db db, MediaCatalog catalog, JellyfinLibrary library)
        {
            _db = db;
            _catalog = catalog;
            _library = library;
        }

        // GET /api/EnhancedFin/v1/me/watchlist?type=movie|tv&genre=16
        [HttpGet("me/watchlist")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult<ListResponse<WatchlistItem>> list(
            [FromQuery] string? type = null,
            [FromQuery] int? genre = null,
            [FromQuery] int? limit = null,
            [FromQuery] int? offset = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (!is_valid_media_type(type)) return invalid_media_type();
            if (page_of(limit, offset) is not { } page) return invalid_page();

            var user_id = user.Value.ToString("D");

            // Le filtre par genre n'était pas possible avant : genre_ids était un CSV.
            const string where = @"
                FROM watchlist w JOIN media m ON m.media_key = w.media_key
                WHERE w.user_id = $u
                  AND ($t IS NULL OR m.media_type = $t)
                  AND ($g IS NULL OR EXISTS (SELECT 1 FROM media_genre
                                             WHERE media_key = w.media_key AND genre_id = $g))";

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT w.media_key, w.added_at, m.media_type, m.title, m.year,
                       m.poster_url, m.backdrop_url,
                       (SELECT score FROM rating WHERE user_id = $u AND media_key = w.media_key)"
                + where + @"
                ORDER BY w.added_at DESC
                LIMIT $limit OFFSET $offset";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$t", (object?)type ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$g", (object?)genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            // Index résolu une fois pour toute la liste : une résolution par ligne
            // ferait une requête bibliothèque par entrée de watchlist.
            var in_library = _library.index(user.Value);

            var items = new List<WatchlistItem>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var media_key = rd.GetString(0);
                in_library.TryGetValue(media_key, out var match);

                items.Add(new WatchlistItem(
                    mediaKey: media_key,
                    addedAt: rd.GetString(1),
                    mediaType: rd.GetString(2),
                    title: rd.GetString(3),
                    year: rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                    posterUrl: rd.IsDBNull(5) ? null : rd.GetString(5),
                    backdropUrl: rd.IsDBNull(6) ? null : rd.GetString(6),
                    rating: rd.IsDBNull(7) ? (int?)null : rd.GetInt32(7),
                    inLibrary: match is not null,
                    jellyfinId: match?.JellyfinId.ToString("D")));
            }
            rd.Close();

            return Ok(new ListResponse<WatchlistItem>(items, count(con, "SELECT COUNT(*)" + where, cmd)));
        }

        // PUT /api/EnhancedFin/v1/me/watchlist/{mediaKey}
        [HttpPut("me/watchlist/{mediaKey}")]
        public async Task<ActionResult> add(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            return write_for_media(_db, @"
                INSERT INTO watchlist (user_id, media_key, added_at) VALUES ($u, $k, $now)
                ON CONFLICT(user_id, media_key) DO NOTHING",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", mediaKey);
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                },
                mediaKey);
        }

        // DELETE /api/EnhancedFin/v1/me/watchlist/{mediaKey}
        [HttpDelete("me/watchlist/{mediaKey}")]
        public ActionResult remove(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "DELETE FROM watchlist WHERE user_id = $u AND media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", mediaKey);

            return cmd.ExecuteNonQuery() == 0
                ? problem(404, "Absent de la watchlist", $"'{mediaKey}' n'est pas dans la watchlist.")
                : NoContent();
        }
    }
}
