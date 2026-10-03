using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

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
        /// Noter une série sur un seul épisode n'a pas de sens.
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


        /// <summary>
        /// Un média vu, avant filtrage et pagination — quelle que soit la source.
        ///
        /// Les champs de présentation sont nullables parce qu'un média vu sur le
        /// serveur mais jamais noté **n'est pas au référentiel** : Jellyfin en donne
        /// alors le titre et l'année, mais pas d'affiche TMDB. Le client retombe dans
        /// ce cas sur l'image du serveur, qu'il atteint par `jellyfinId`.
        /// </summary>
        private record PendingCandidate(
            string MediaKey,
            string MediaType,
            string Title,
            int? Year,
            string? PosterUrl,
            string? BackdropUrl,
            int WatchedEpisodes,
            /// Plus rien à voir : tous les épisodes d'une série, ou le film lui-même.
            /// Toujours faux pour la source `playback`, qui ne connaît pas le nombre
            /// total d'épisodes d'une œuvre.
            bool IsComplete,
            /// Telle qu'elle est **stockée**, et non reformatée : les dates migrées
            /// viennent verbatim de l'ancienne base, dont le format n'est pas garanti
            /// identique à celui que produit le plugin. Les reparser pour les réémettre
            /// changerait la valeur sur le fil sans rien apporter — c'est
            /// <see cref="sort_key"/> qui porte le tri.
            string LastWatchedAt);

        // GET /api/EnhancedFin/v1/me/ratings/pending
        // Vu mais pas encore noté. Remplace l'ancien `ToRate`.
        [HttpGet("me/ratings/pending")]
        [RateLimit("outbound", 60)]
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

            using var con = _db.open();

            // Deux sources, et il en faut deux : la table `playback` ne connaît que ce
            // qu'un client lui a explicitement envoyé — c'est-à-dire, aujourd'hui, les
            // seules données migrées. Jellyfin, lui, sait ce qui a réellement été lu
            // sur le serveur, mais ignore tout des médias sans fichier. C'est
            // exactement le découpage qu'opère le front web.
            var candidates = read_playback_candidates(con, user_id);
            merge_library_candidates(con, user.Value, candidates);

            var rated = read_rated_keys(con, user_id);

            // Le tri porte sur la fusion, donc il ne peut plus être fait en SQL : la
            // pagination devient une découpe en mémoire. Le volume le permet — quelques
            // centaines de médias vus, contre 2 678 lignes de progression brutes que le
            // regroupement réduit d'autant.
            var retained = candidates.Values
                .Where(c => !rated.Contains(c.MediaKey))
                .Where(is_rateable)
                .OrderByDescending(c => sort_key(c.LastWatchedAt))
                .ToList();

            var in_library = _library.index(user.Value);

            var items = retained
                .Skip(page.Offset)
                .Take(page.Limit)
                .Select(c =>
                {
                    in_library.TryGetValue(c.MediaKey, out var match);

                    return new PendingRatingItem(
                        mediaKey: c.MediaKey,
                        mediaType: c.MediaType,
                        title: c.Title,
                        year: c.Year,
                        posterUrl: c.PosterUrl,
                        watchedEpisodes: c.WatchedEpisodes,
                        lastWatchedAt: c.LastWatchedAt,
                        backdropUrl: c.BackdropUrl,
                        inLibrary: match is not null,
                        jellyfinId: match?.JellyfinId.ToString("D"));
                })
                .ToList();

            return Ok(new ListResponse<PendingRatingItem>(items, retained.Count));
        }

        /// <summary>
        /// Assez vu pour être noté.
        ///
        /// Un film suffit. Une série demande cinq épisodes — seuil repris de l'ancien
        /// plugin, parce que noter une série sur un épisode n'a pas de sens —
        /// **ou bien d'être terminée**.
        /// </summary>
        /// <remarks>
        /// ⚠️ La clause « terminée » n'est pas un confort : sans elle, une œuvre plus
        /// courte que le seuil ne pouvait **jamais** être proposée, même vue de bout
        /// en bout. Toutes les mini-séries de moins de cinq épisodes étaient invisibles
        /// pour cette section, à vie.
        ///
        /// Le seuil garde son rôle — écarter une série longue à peine commencée — mais
        /// ne décide plus seul.
        /// </remarks>
        private static bool is_rateable(PendingCandidate candidate) =>
            candidate.MediaType == "movie"
            || candidate.IsComplete
            || candidate.WatchedEpisodes >= MinEpisodesForTv;

        /// <summary>
        /// Médias vus d'après la table `playback` du plugin.
        ///
        /// C'est la seule source qui connaisse les médias **sans fichier** sur le
        /// serveur.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - user_id (string) : identité de l'appelant
        ///
        /// Output :
        /// - candidates (Dictionary) : media_key -&gt; candidat
        /// </summary>
        private static Dictionary<string, PendingCandidate> read_playback_candidates(
            SqliteConnection con, string user_id)
        {
            var candidates = new Dictionary<string, PendingCandidate>(StringComparer.Ordinal);

            using var cmd = con.CreateCommand();
            cmd.CommandText = @"
                SELECT p.media_key, m.media_type, m.title, m.year, m.poster_url,
                       m.backdrop_url, COUNT(*) AS vus, MAX(p.updated_at) AS dernier
                FROM playback p
                JOIN media m ON m.media_key = p.media_key
                WHERE p.user_id = $u
                  AND " + SqlIsWatched + @"
                GROUP BY p.media_key";
            cmd.Parameters.AddWithValue("$u", user_id);

            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var media_key = rd.GetString(0);
                candidates[media_key] = new PendingCandidate(
                    MediaKey: media_key,
                    MediaType: rd.GetString(1),
                    Title: rd.GetString(2),
                    Year: rd.IsDBNull(3) ? (int?)null : rd.GetInt32(3),
                    PosterUrl: rd.IsDBNull(4) ? null : rd.GetString(4),
                    BackdropUrl: rd.IsDBNull(5) ? null : rd.GetString(5),
                    WatchedEpisodes: rd.GetInt32(6),
                    // La table ne stocke pas le nombre d'épisodes d'une série : on ne
                    // peut pas savoir si celle-ci est terminée. Le seuil décide seul
                    // pour les médias que seule cette source connaît.
                    IsComplete: false,
                    LastWatchedAt: rd.GetString(7));
            }

            return candidates;
        }

        /// <summary>
        /// Complète les candidats avec ce que Jellyfin sait avoir été vu.
        ///
        /// Une clé déjà connue de `playback` est **fusionnée** et non remplacée : on
        /// retient le plus grand nombre d'épisodes et la date la plus récente, parce
        /// qu'aucune des deux sources n'est exhaustive. Le référentiel garde la main
        /// sur la présentation quand il porte le média, ses affiches TMDB étant plus
        /// riches que ce que la bibliothèque expose ici.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - user (Guid) : utilisateur dont on applique les droits
        /// - into (Dictionary) : candidats à compléter, modifié sur place
        /// </summary>
        private void merge_library_candidates(
            SqliteConnection con, Guid user, Dictionary<string, PendingCandidate> into)
        {
            var watched = _library.watched(user);
            if (watched.Count == 0) return;

            // Les médias vus que le référentiel connaît déjà sans les avoir en
            // `playback` : notés autrefois, mis en watchlist… Leurs affiches valent
            // mieux que rien, d'où cette relecture en une requête.
            var known = read_media_rows(con, watched.Keys.Where(k => !into.ContainsKey(k)));

            foreach (var (media_key, seen) in watched)
            {
                if (into.TryGetValue(media_key, out var existing))
                {
                    into[media_key] = existing with
                    {
                        WatchedEpisodes = Math.Max(existing.WatchedEpisodes, seen.WatchedEpisodes),
                        // `playback` ne peut que l'ignorer, Jellyfin le sait : la
                        // fusion ne doit donc jamais faire retomber ce drapeau.
                        IsComplete = existing.IsComplete || seen.IsComplete,
                        LastWatchedAt = sort_key(existing.LastWatchedAt) >= seen.LastPlayedAt
                            ? existing.LastWatchedAt
                            : iso(seen.LastPlayedAt),
                    };
                    continue;
                }

                if (MediaCatalog.split(media_key) is not { } parts) continue;

                known.TryGetValue(media_key, out var row);

                into[media_key] = new PendingCandidate(
                    MediaKey: media_key,
                    MediaType: parts.Type,
                    // Jellyfin est le repli, pas l'inverse : un titre du référentiel
                    // vient de TMDB et reste cohérent avec le reste de l'Explorer.
                    Title: row?.Title ?? seen.Name,
                    Year: row?.Year ?? seen.Year,
                    PosterUrl: row?.PosterUrl,
                    BackdropUrl: row?.BackdropUrl,
                    WatchedEpisodes: seen.WatchedEpisodes,
                    IsComplete: seen.IsComplete,
                    LastWatchedAt: iso(seen.LastPlayedAt));
            }
        }

        /// <summary>Ce que le référentiel porte sur un média, pour la présentation.</summary>
        private record MediaRow(string? Title, int? Year, string? PosterUrl, string? BackdropUrl);

        /// <summary>
        /// Lit les fiches du référentiel pour un lot de clés, en une requête.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - media_keys (IEnumerable&lt;string&gt;) : clés à lire
        ///
        /// Output :
        /// - rows (Dictionary) : clé -&gt; fiche ; une clé absente du référentiel
        ///   n'y figure simplement pas
        /// </summary>
        private static Dictionary<string, MediaRow> read_media_rows(
            SqliteConnection con, IEnumerable<string> media_keys)
        {
            var keys = media_keys.ToList();
            var rows = new Dictionary<string, MediaRow>(StringComparer.Ordinal);
            if (keys.Count == 0) return rows;

            using var cmd = con.CreateCommand();
            cmd.CommandText =
                $"SELECT media_key, title, year, poster_url, backdrop_url FROM media WHERE media_key IN ({bind_keys(cmd, keys)})";

            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                rows[rd.GetString(0)] = new MediaRow(
                    Title: rd.IsDBNull(1) ? null : rd.GetString(1),
                    Year: rd.IsDBNull(2) ? (int?)null : rd.GetInt32(2),
                    PosterUrl: rd.IsDBNull(3) ? null : rd.GetString(3),
                    BackdropUrl: rd.IsDBNull(4) ? null : rd.GetString(4));

            return rows;
        }

        /// <summary>Clés des médias que l'appelant a déjà notés.</summary>
        private static HashSet<string> read_rated_keys(SqliteConnection con, string user_id)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);

            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT media_key FROM rating WHERE user_id = $u";
            cmd.Parameters.AddWithValue("$u", user_id);

            using var rd = cmd.ExecuteReader();
            while (rd.Read()) keys.Add(rd.GetString(0));

            return keys;
        }

        /// <summary>Date d'une source Jellyfin, au format que le plugin émet partout.</summary>
        private static string iso(DateTime value) =>
            value.ToString("o", CultureInfo.InvariantCulture);

        /// <summary>
        /// Clé de tri d'une date stockée, sans jamais lever.
        ///
        /// Sert **uniquement** à ordonner : la chaîne émise reste celle de la base. Un
        /// tri ordinal ne suffirait pas ici, les deux sources n'écrivant pas forcément
        /// le même format — les dates migrées viennent verbatim de l'ancienne base.
        /// Une valeur illisible relègue l'item en fin de liste plutôt que de faire
        /// tomber la route.
        /// </summary>
        private static DateTime sort_key(string value) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture,
                              DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                              out var parsed)
                ? parsed
                : DateTime.MinValue;

        // PUT /api/EnhancedFin/v1/me/ratings/{mediaKey}
        // Idempotent : rejouer l'appel ne crée pas de doublon (upsert sur la PK).
        [HttpPut("me/ratings/{mediaKey}")]
        [RateLimit("outbound", 60)]
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
            return delete_for_media(_db, "rating", mediaKey, "Note introuvable", $"Aucune note sur '{mediaKey}'.");
        }

    }
}
