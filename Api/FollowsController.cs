using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
    /// <summary>
    /// Une sortie au calendrier : un épisode, ou un film le jour de sa sortie.
    ///
    /// Propriétés en camelCase à dessein — voir <see cref="ListResponse{T}"/>.
    /// </summary>
    public record CalendarRelease(
        string mediaKey,
        string mediaType,
        int season,
        int episode,
        /// Nul pour un film : une sortie de film est stockée en saison 0 / épisode 0,
        /// et `refresh_movie_release` y recopie le titre faute de mieux — que
        /// `title` porte déjà. Le répéter ferait afficher deux fois la même chose au
        /// client qui traite la ligne comme un épisode.
        string? episodeName,
        string title,
        string? posterUrl,
        /// Le fichier est sur ce serveur : le client peut ouvrir la fiche native
        /// plutôt que la fiche de découverte.
        bool inLibrary,
        string? jellyfinId);

    /// <summary>Les sorties d'un jour donné.</summary>
    public record CalendarDay(string date, IReadOnlyList<CalendarRelease> releases);

    /// <summary>
    /// Le calendrier sur une plage, regroupé par jour.
    ///
    /// La plage demandée est renvoyée en écho : le client qui pagine par quinzaines
    /// sait ainsi ce qu'il a réellement obtenu, sans recalculer ses bornes.
    /// </summary>
    public record CalendarResponse(string from, string to, IReadOnlyList<CalendarDay> days);

    public class FollowsController : EnhancedFinController
    {
        private readonly Db _db;
        private readonly MediaCatalog _catalog;
        private readonly JellyfinLibrary _library;
        private readonly ILogger<FollowsController> _logger;

        public FollowsController(
            Db db, MediaCatalog catalog, JellyfinLibrary library, ILogger<FollowsController> logger)
        {
            _db = db;
            _catalog = catalog;
            _library = library;
            _logger = logger;
        }

        // GET /api/EnhancedFin/v1/me/follows
        [HttpGet("me/follows")]
        public ActionResult list([FromQuery] int? limit = null, [FromQuery] int? offset = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();
            if (page_of(limit, offset) is not { } page) return invalid_page();

            var user_id = user.Value.ToString("D");

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
            // Date du serveur, pas UTC : les dates TMDB sont des jours sans fuseau, et le soir
            // en Europe UTC est déjà « demain ».
            cmd.Parameters.AddWithValue("$today", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$limit", page.Limit);
            cmd.Parameters.AddWithValue("$offset", page.Offset);

            // Index résolu une fois pour toute la réponse, comme pour le calendrier :
            // une résolution par ligne ferait une requête bibliothèque par suivi.
            var in_library = _library.index(user.Value);

            var items = new List<object>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var media_key = rd.GetString(0);
                in_library.TryGetValue(media_key, out var match);

                items.Add(new
                {
                    mediaKey = media_key,
                    addedAt = rd.GetString(1),
                    mediaType = rd.GetString(2),
                    title = rd.GetString(3),
                    year = rd.IsDBNull(4) ? (int?)null : rd.GetInt32(4),
                    posterUrl = rd.IsDBNull(5) ? null : rd.GetString(5),
                    // Prochaine sortie à venir : ce que l'UI affiche sous le titre.
                    nextAirDate = rd.IsDBNull(6) ? null : rd.GetString(6),
                    // Sans eux, le client ouvre une fiche de découverte pour une série
                    // que ce serveur possède — bouton Lire grisé, pas d'épisodes. Les
                    // autres routes de liste les rendent déjà.
                    inLibrary = match is not null,
                    jellyfinId = match?.JellyfinId.ToString("D"),
                });
            }
            rd.Close();

            return Ok(new { items, total = count(con, "SELECT COUNT(*)" + where, cmd) });
        }

        // PUT /api/EnhancedFin/v1/me/follows/{mediaKey}
        [HttpPut("me/follows/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> follow(string mediaKey)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            if (await ensure_media(_catalog, mediaKey) is { } error) return error;

            var is_new = !is_following(user_id, mediaKey);
            var written = write_for_media(_db, @"
                INSERT INTO follow (user_id, media_key, added_at) VALUES ($u, $k, $now)
                ON CONFLICT(user_id, media_key) DO NOTHING",
                cmd =>
                {
                    cmd.Parameters.AddWithValue("$u", user_id);
                    cmd.Parameters.AddWithValue("$k", mediaKey);
                    cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                },
                mediaKey);

            // Les sorties sont récupérées **maintenant**, et non à la prochaine
            // exécution de `RefreshTask`.
            //
            // Sans ça, suivre une série ne la fait apparaître au calendrier qu'après
            // l'entretien quotidien de 4 h : on suit un média et il ne se passe
            // visiblement rien, parfois pendant une journée entière.
            //
            // Après l'écriture du suivi, et sans conditionner la réponse : le suivi
            // est déjà commité, donc une panne TMDB ou une requête abandonnée en
            // cours de route ne le perd pas — `RefreshTask` rattrapera les sorties.
            //
            // Coût : un appel TMDB par saison, soit quelques secondes sur une longue
            // série. Le client pose son état de façon optimiste, l'attente ne se voit
            // donc pas à l'écran. Seulement pour un **nouveau** suivi : répéter le `PUT`
            // ne doit pas relancer ces appels.
            if (is_new && written is NoContentResult)
                await refresh_releases_quietly(mediaKey);

            return written;
        }

        /// <summary>Vrai si l'appelant suit déjà ce média.</summary>
        private bool is_following(string user_id, string media_key)
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM follow WHERE user_id = $u AND media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", media_key);
            return cmd.ExecuteScalar() is not null;
        }

        /// <summary>
        /// Récupère les dates de diffusion d'un média, sans jamais faire échouer
        /// l'appel qui l'a demandé.
        ///
        /// Parametres :
        /// - media_key (string) : média à rafraîchir
        /// </summary>
        private async Task refresh_releases_quietly(string media_key)
        {
            try
            {
                var count = await _catalog.refresh_releases(media_key);
                _logger.LogDebug("[EnhancedFin] suivi {Key} : {Count} sorties", media_key, count);
            }
            catch (Exception ex)
            {
                // Le suivi est enregistré, c'est ce qui compte. L'entretien quotidien
                // repassera sur ce média, sa dernière date de rafraîchissement étant
                // restée nulle.
                _logger.LogWarning(ex, "[EnhancedFin] sorties de {Key} non récupérées", media_key);
            }
        }

        // DELETE /api/EnhancedFin/v1/me/follows/{mediaKey}
        [HttpDelete("me/follows/{mediaKey}")]
        public ActionResult unfollow(string mediaKey)
        {
            return delete_for_media(_db, "follow", mediaKey, "Non suivi", $"'{mediaKey}' n'était pas suivi.");
        }

        // GET /api/EnhancedFin/v1/me/calendar?from=2026-09-01&to=2026-09-30
        [HttpGet("me/calendar")]
        public ActionResult<CalendarResponse> calendar(
            [FromQuery] string? from = null, [FromQuery] string? to = null)
        {
            var user = current_user();
            if (user is null) return not_authenticated();

            var user_id = user.Value.ToString("D");

            // Une date fournie mais illisible est une erreur : la remplacer en silence
            // renverrait une autre période que celle demandée.
            var parsed_from = parse_date(from);
            var parsed_to = parse_date(to);
            if ((from is not null && parsed_from is null) || (to is not null && parsed_to is null))
                return problem(400, "Date invalide", "from et to sont au format 'yyyy-MM-dd'.");

            // Par défaut : la quinzaine en cours, à partir d'aujourd'hui (date du serveur).
            var start = parsed_from ?? DateTime.Now.Date;
            var end = parsed_to ?? start.AddDays(14);

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
            cmd.Parameters.AddWithValue("$from", start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$to", end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            // Index résolu une fois pour toute la réponse : une résolution par ligne
            // ferait une requête bibliothèque par sortie. Il porte l'utilisateur, donc
            // ne révèle pas les bibliothèques auxquelles il n'a pas accès.
            var in_library = _library.index(user.Value);

            // Regroupement par jour fait ici : le client n'a plus qu'à afficher.
            var by_day = new Dictionary<string, List<CalendarRelease>>(StringComparer.Ordinal);
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var day = rd.GetString(0)[..10];
                if (!by_day.TryGetValue(day, out var releases))
                    by_day[day] = releases = new List<CalendarRelease>();

                var media_key = rd.GetString(1);
                var media_type = rd.GetString(7);
                in_library.TryGetValue(media_key, out var match);

                releases.Add(new CalendarRelease(
                    mediaKey: media_key,
                    mediaType: media_type,
                    season: rd.GetInt32(2),
                    episode: rd.GetInt32(3),
                    episodeName: media_type == "movie" || rd.IsDBNull(4) ? null : rd.GetString(4),
                    title: rd.GetString(5),
                    posterUrl: rd.IsDBNull(6) ? null : rd.GetString(6),
                    inLibrary: match is not null,
                    jellyfinId: match?.JellyfinId.ToString("D")));
            }

            // Les dates sont au format ISO, donc l'ordre lexicographique est l'ordre
            // chronologique : un tri ordinal suffit.
            var days = by_day.Keys
                .OrderBy(day => day, StringComparer.Ordinal)
                .Select(day => new CalendarDay(day, by_day[day]))
                .ToList();

            return Ok(new CalendarResponse(
                from: start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                to: end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                days: days));
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
