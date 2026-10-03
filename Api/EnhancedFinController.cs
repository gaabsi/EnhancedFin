using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Enveloppe commune des réponses de liste : `{ items, total }`.
    ///
    /// Toutes les collections du plugin sortent sous cette forme ; un générique
    /// évite d'en redéclarer une par domaine.
    ///
    /// ⚠️ Les propriétés sont en camelCase À DESSEIN, ici comme dans tous les DTOs
    /// de ce plugin. Jellyfin sérialise avec `JsonDefaults.PascalCaseOptions`, dont
    /// `PropertyNamingPolicy` vaut `null` : les noms partent **tels qu'écrits**.
    /// Les renommer en PascalCase changerait le JSON et casserait le front JS
    /// existant ainsi que le client Swift, sans aucune erreur de compilation.
    /// </summary>
    public record ListResponse<T>(IReadOnlyList<T> items, int total);

    /// <summary>
    /// Budget de requêtes par utilisateur et par minute, sur un compartiment nommé.
    ///
    /// Posé sur le controller de base (`all`, tout le plugin) et sur les routes qui
    /// appellent TMDB, MDBList ou Seerr, ou qui parcourent la bibliothèque (`outbound`).
    /// Sans lui, un compte qui boucle sur des identifiants épuise les quotas des clés
    /// d'API, **partagées** par tout le serveur, ou occupe le processeur du serveur.
    ///
    /// Fenêtre fixe d'une minute, en mémoire : simple, suffisant pour un serveur
    /// familial, remis à zéro au redémarrage.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RateLimitAttribute : ActionFilterAttribute
    {
        private static readonly ConcurrentDictionary<string, (long Minute, int Count)> Windows = new();

        private readonly string _bucket;
        private readonly int _per_minute;

        public RateLimitAttribute(string bucket, int per_minute)
        {
            _bucket = bucket;
            _per_minute = per_minute;
        }

        public override void OnActionExecuting(ActionExecutingContext context)
        {
            var user = context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value;
            if (user is null || context.Controller is not EnhancedFinController controller) return;

            var minute = Environment.TickCount64 / 60_000;
            var window = Windows.AddOrUpdate(
                _bucket + "|" + user,
                _ => (minute, 1),
                (_, current) => current.Minute == minute ? (minute, current.Count + 1) : (minute, 1));

            if (window.Count > _per_minute) context.Result = controller.too_many_requests();
        }
    }

    /// <summary>
    /// Base commune à tous les controllers du plugin : authentification, identité
    /// de l'appelant, validation des clés média et format d'erreur.
    ///
    /// Tout controller qui en hérite est authentifié par défaut — on ne peut pas
    /// oublier l'attribut et exposer un endpoint par inadvertance, ce qui est
    /// précisément ce qui est arrivé à l'ancien plugin (cf. SECURITY_AUDIT.md).
    /// </summary>
    [ApiController]
    // ⚠️ [Authorize] **sans politique nommée**, et ce n'est pas un oubli.
    //
    // Testé sur Jellyfin 10.11.6 : `[Authorize(Policy = "DefaultAuthorizationPolicy")]`
    // renvoie HTTP 500 sur toutes les routes —
    // « The AuthorizationPolicy named: 'DefaultAuthorizationPolicy' was not found. »
    // Ce nom désigne l'objet de politique par défaut, pas une politique **enregistrée** ;
    // `DefaultAuthorization`, son équivalent de 10.9, n'existe plus non plus.
    //
    // [Authorize] nu s'appuie sur `AuthorizationOptions.DefaultPolicy`, que Jellyfin
    // configure lui-même. La compilation ne dit rien de tout ça : seul un appel sur un
    // vrai serveur révèle l'erreur.
    [Authorize]
    [RateLimit("all", 300)]
    [Route("api/EnhancedFin/v1")]
    [Produces(MediaTypeNames.Application.Json)]
    public abstract class EnhancedFinController : ControllerBase
    {
        /// <summary>
        /// Nombre maximum d'items qu'une route de liste accepte de renvoyer.
        ///
        /// 500 et non 200 : les 234 notes de la production étaient tronquées, ce qui
        /// ne se voyait pas tant que « Mes notes » n'était qu'un aperçu. La section
        /// étant désormais dépliable, la troncature deviendrait visible — et
        /// inexplicable pour l'utilisateur. La borne reste là pour ce qu'elle fait
        /// vraiment : empêcher qu'une requête rende une table entière.
        /// </summary>
        protected const int MaxPageSize = 500;

        /// <summary>Longueur maximale d'une valeur du client renvoyée en écho dans une erreur.</summary>
        private const int MaxEchoLength = 64;

        /// <summary>
        /// Part de la durée au-delà de laquelle un média est considéré terminé.
        ///
        /// Seuil unique, volontairement partagé : il décide à la fois de la sortie du
        /// Continue Watching et de l'entrée dans « à noter ». Deux valeurs différentes
        /// rendraient un épisode simultanément « en cours » et « fini ».
        ///
        /// 0,9 reprend le `FINISHED_RATIO` de l'ancien plugin.
        /// </summary>
        /// <remarks>
        /// `static readonly` et non `const` : le seuil est interpolé dans
        /// <see cref="SqlIsWatched"/>, ce qu'une constante de compilation ne permet pas.
        /// </remarks>
        protected static readonly double FinishedRatio = 0.9;

        /// <summary>
        /// Condition SQL « cet épisode est vu », à injecter dans un WHERE.
        ///
        /// Une durée nulle signifie durée inconnue : on s'en remet alors au marquage,
        /// faute de pouvoir mesurer l'avancement. Attention, `watched_at` seul ne prouve
        /// rien — dans les données migrées il vaut la date de dernière activité, y compris
        /// pour un épisode lancé puis abandonné au bout de quelques secondes.
        /// </summary>
        /// <remarks>
        /// Le seuil est **interpolé** depuis <see cref="FinishedRatio"/> : le réécrire
        /// en dur laisserait deux valeurs vivre côte à côte, et le jour où l'une
        /// bougerait un épisode serait à la fois « en cours » et « fini ».
        ///
        /// `InvariantCulture` est indispensable : sous une culture française, 0.9 se
        /// sérialiserait « 0,9 » et produirait du SQL invalide.
        /// </remarks>
        protected static readonly string SqlIsWatched =
            "(p.duration_ticks <= 0 OR p.position_ticks >= p.duration_ticks * "
            + FinishedRatio.ToString(CultureInfo.InvariantCulture) + ")";

        /// <summary>
        /// Vrai si l'appelant (`$u`) a un lien avec le média `m` : noté, en watchlist,
        /// suivi, masqué ou lu.
        ///
        /// ⚠️ La table `media` est **partagée** : elle se remplit des actions de tous les
        /// utilisateurs. Tout ce qui la parcourt ou en dévoile la présence (recherche
        /// locale, `known`, `GET media`) doit passer par ce filtre, sinon un compte
        /// découvre ce que les autres ont noté ou masqué.
        /// </summary>
        protected const string SqlIsMine = @"(
            EXISTS (SELECT 1 FROM rating      WHERE user_id = $u AND media_key = m.media_key)
            OR EXISTS (SELECT 1 FROM watchlist   WHERE user_id = $u AND media_key = m.media_key)
            OR EXISTS (SELECT 1 FROM follow      WHERE user_id = $u AND media_key = m.media_key)
            OR EXISTS (SELECT 1 FROM hidden_item WHERE user_id = $u AND media_key = m.media_key)
            OR EXISTS (SELECT 1 FROM playback    WHERE user_id = $u AND media_key = m.media_key))";

        /// <summary>
        /// Identité de l'appelant, dérivée du token — jamais d'un paramètre de requête.
        ///
        /// Le GUID est normalisé en forme canonique ('D', avec tirets) : l'ancienne base
        /// mélangeait les deux formats selon les tables, ce qui rendait toute jointure
        /// impossible. Toute la base est désormais alignée sur cette forme.
        /// </summary>
        /// <remarks>
        /// Trois refus volontaires :
        ///
        /// - **pas de repli sur `ClaimTypes.NameIdentifier`** : rien ne garantit qu'il
        ///   porte un identifiant d'utilisateur Jellyfin ;
        /// - **une valeur non analysable en GUID est rejetée** au lieu d'être renvoyée
        ///   telle quelle : elle servait sinon de clé de partitionnement en base, et
        ///   deux appelants portant la même chaîne partageaient leurs données ;
        /// - **`Guid.Empty` est rejeté** : un jeton de clé API porte `Jellyfin-UserId`
        ///   à zéro, ce qui créait un « utilisateur » fantôme partagé par toutes les
        ///   clés API du serveur.
        /// </remarks>
        protected Guid? current_user()
        {
            var claim = User.FindFirst("Jellyfin-UserId");

            if (!Guid.TryParse(claim?.Value, out var guid) || guid == Guid.Empty)
                return null;

            return guid;
        }

        /// <summary>
        /// Identité de l'appelant en forme canonique 'D', telle qu'elle est stockée.
        ///
        /// L'ancienne base mélangeait les deux formats selon les tables, ce qui rendait
        /// toute jointure impossible. Toute la base est désormais alignée sur cette forme.
        /// </summary>
        protected string? current_user_id() => current_user()?.ToString("D");

        /// <summary>
        /// Valide une clé média au format '{movie|tv}:{tmdb_id}'.
        ///
        /// Le préfixe de type n'est pas décoratif : TMDB a des espaces d'ID séparés,
        /// donc movie:550 et tv:550 désignent deux œuvres sans rapport.
        /// </summary>
        protected static bool is_valid_media_key(string key) =>
            MediaCatalog.split(key) is not null;

        /// <summary>
        /// Valide un filtre de type de média.
        ///
        /// `null` est valide : le paramètre est facultatif sur toutes les routes qui
        /// l'acceptent, et l'omettre signifie « les deux types ».
        /// </summary>
        protected static bool is_valid_media_type(string? type) =>
            type is null or "movie" or "tv";

        /// <summary>
        /// Borne une pagination de liste.
        ///
        /// Parametres :
        /// - limit (int?) : taille demandée, `null` pour le maximum
        /// - offset (int?) : décalage demandé
        ///
        /// Output :
        /// - page ((int Limit, int Offset)?) : valeurs bornées, null si hors domaine
        /// </summary>
        protected static (int Limit, int Offset)? page_of(int? limit, int? offset)
        {
            var taken = limit ?? MaxPageSize;
            var skipped = offset ?? 0;

            if (taken is < 1 || taken > MaxPageSize) return null;
            if (skipped < 0) return null;

            return (taken, skipped);
        }

        /// <summary>
        /// Erreur RFC 7807, enrichie de `success` et `message` pour que le front JS
        /// existant fonctionne sans modification. Ces deux champs seront retirables
        /// une fois le JS migré, sans impact sur les clients Swift.
        /// </summary>
        /// <summary>
        /// Vrai si l'écriture a échoué parce que le média manque au référentiel : violation
        /// de clé étrangère (code étendu 787). Le code 19 seul couvre toutes les contraintes
        /// (`CHECK`, `NOT NULL`, `UNIQUE`), qu'il ne faut pas présenter comme « média inconnu ».
        /// </summary>
        protected static bool is_unknown_media(SqliteException ex) => ex.SqliteExtendedErrorCode == 787;

        /// <summary>Réponse d'un budget `RateLimit` dépassé.</summary>
        internal ObjectResult too_many_requests() =>
            problem(429, "Trop de requêtes", "Réessayez dans une minute.");

        protected ObjectResult problem(int status, string title, string detail)
        {
            var pd = new ProblemDetails
            {
                Type = $"https://enhancedfin/errors/{title.ToLowerInvariant().Replace(' ', '-')}",
                Title = title,
                Status = status,
                Detail = detail,
            };
            pd.Extensions["success"] = false;
            pd.Extensions["message"] = detail;

            return StatusCode(status, pd);
        }

        /// <summary>Raccourci pour l'erreur d'authentification, identique partout.</summary>
        protected ObjectResult not_authenticated() =>
            problem(401, "Non authentifié", "Aucun utilisateur associé au token.");

        /// <summary>Raccourci pour une clé média mal formée.</summary>
        /// <remarks>
        /// L'entrée est **tronquée** avant d'être renvoyée en écho : une clé de
        /// plusieurs kilo-octets se retrouverait sinon recopiée dans la réponse et
        /// dans les journaux, aux frais du serveur.
        /// </remarks>
        protected ObjectResult invalid_media_key(string key) =>
            problem(400, "Clé média invalide",
                    $"'{truncate(key)}' n'est pas au format 'movie:123' ou 'tv:123'.");

        /// <summary>Raccourci pour un filtre de type non reconnu.</summary>
        protected ObjectResult invalid_media_type() =>
            problem(400, "Type invalide", "type doit valoir 'movie', 'tv', ou être omis.");

        /// <summary>Raccourci pour une pagination hors domaine.</summary>
        protected ObjectResult invalid_page() =>
            problem(400, "Pagination invalide",
                    $"limit doit être compris entre 1 et {MaxPageSize}, offset positif.");

        /// <summary>
        /// Prépare un média pour une écriture : clé valide, puis présent au référentiel.
        ///
        /// Le référentiel est peuplé **paresseusement** : un média inconnu est récupéré
        /// depuis TMDB plutôt que de faire échouer l'écriture sur la clé étrangère.
        /// Sans ça, on ne pourrait noter que ce que la migration a importé.
        ///
        /// Parametres :
        /// - catalog (MediaCatalog) : service de peuplement
        /// - media_key (string) : clé du média
        ///
        /// Output :
        /// - error (ActionResult | null) : la réponse d'erreur, ou null si tout va bien
        /// </summary>
        protected async Task<ActionResult?> ensure_media(MediaCatalog catalog, string media_key)
        {
            if (!is_valid_media_key(media_key)) return invalid_media_key(media_key);

            return await catalog.ensure_exists(media_key)
                ? null
                : problem(404, "Média inconnu", $"'{truncate(media_key)}' est introuvable sur TMDB.");
        }

        /// <summary>
        /// Retire la ligne de l'appelant pour un média, dans une de ses tables
        /// (note, watchlist, suivi, masquage) : 204, ou 404 s'il n'y avait rien.
        ///
        /// Parametres :
        /// - db (Db) : base du plugin
        /// - table (string) : table visée — une constante du code, jamais une valeur reçue
        /// - media_key (string) : clé du média
        /// - not_found_title (string) : titre du 404
        /// - not_found_detail (string) : détail du 404
        ///
        /// Output :
        /// - result (ActionResult) : 204, 400, 401 ou 404
        /// </summary>
        protected ActionResult delete_for_media(
            Db db, string table, string media_key, string not_found_title, string not_found_detail)
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();
            if (!is_valid_media_key(media_key)) return invalid_media_key(media_key);

            using var con = db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = $"DELETE FROM {table} WHERE user_id = $u AND media_key = $k";
            cmd.Parameters.AddWithValue("$u", user_id);
            cmd.Parameters.AddWithValue("$k", media_key);

            return cmd.ExecuteNonQuery() == 0 ? problem(404, not_found_title, not_found_detail) : NoContent();
        }

        /// <summary>
        /// Exécute une écriture rattachée à un média, et traduit la violation de clé
        /// étrangère en 404.
        ///
        /// SQLite renvoie le code **19** quand le média n'est pas dans `media`. C'est
        /// une donnée manquante, pas une panne : elle mérite un 404, pas un 500.
        ///
        /// Parametres :
        /// - db (Db) : source de connexion
        /// - sql (string) : requête d'écriture
        /// - bind (Action&lt;SqliteCommand&gt;) : liaison des paramètres
        /// - media_key (string) : clé du média, pour le message d'erreur
        ///
        /// Output :
        /// - result (ActionResult) : 204, ou 404 si le média manque au référentiel
        /// </summary>
        protected ActionResult write_for_media(
            Db db, string sql, Action<SqliteCommand> bind, string media_key)
        {
            using var con = db.open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;
            bind(cmd);

            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (is_unknown_media(ex))
            {
                return problem(404, "Média inconnu",
                               $"'{truncate(media_key)}' doit d'abord être enregistré dans `media`.");
            }

            return NoContent();
        }

        /// <summary>
        /// Compte les lignes que renverrait une liste **sans sa pagination**.
        ///
        /// `total` ne peut plus valoir `items.Count` depuis que les listes sont
        /// bornées : c'est précisément ce nombre qui dit au client qu'il en reste.
        /// Les paramètres de la commande de liste sont repris tels quels, pour que le
        /// comptage porte exactement sur les mêmes filtres.
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - sql (string) : requête de comptage
        /// - source (SqliteCommand) : commande de la liste
        ///
        /// Output :
        /// - total (int) : nombre total de lignes
        /// </summary>
        protected static int count(SqliteConnection con, string sql, SqliteCommand source)
        {
            using var cmd = con.CreateCommand();
            cmd.CommandText = sql;

            foreach (SqliteParameter p in source.Parameters)
                cmd.Parameters.AddWithValue(p.ParameterName, p.Value ?? DBNull.Value);

            return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Ce que l'appelant a déjà fait d'un média.
        ///
        /// `Known` dit que le média est au **référentiel** (noté, vu, en watchlist…),
        /// ce qui est indépendant de sa présence en bibliothèque : un film noté peut
        /// avoir quitté le serveur, un film présent peut n'avoir jamais été touché.
        /// </summary>
        protected record MyState(int? Rating, bool InWatchlist, bool Following, bool Known);

        /// <summary>
        /// État de l'appelant sur un lot de médias, en une requête.
        ///
        /// Les listes de découverte rendent une vingtaine de candidats dont il faut
        /// savoir, pour chacun, s'il est noté ou en watchlist. Une sous-requête par
        /// ligne ferait autant d'allers-retours SQLite que d'items.
        ///
        /// Une clé absente du référentiel, ou présente sans lien avec l'appelant
        /// (`SqlIsMine`), ne figure pas au résultat : `known` ne dit que « à moi ».
        ///
        /// Parametres :
        /// - con (SqliteConnection) : connexion ouverte
        /// - user_id (string) : identité de l'appelant, en forme canonique
        /// - media_keys (IEnumerable&lt;string&gt;) : clés à interroger
        ///
        /// Output :
        /// - states (Dictionary) : clé -&gt; état ; une clé inconnue est absente
        /// </summary>
        /// <remarks>
        /// `IN (...)` à paramètres liés, et non un `UNION ALL` fabriqué : la seule
        /// borne est celle des paramètres liés (32 766), là où un SELECT composé
        /// plafonne à 500 branches. Les clés viennent de TMDB, pas de l'appelant,
        /// mais restent liées — une requête paramétrée ne se contourne pas.
        /// </remarks>
        protected static Dictionary<string, MyState> read_my_state(
            SqliteConnection con, string user_id, IEnumerable<string> media_keys)
        {
            var keys = media_keys.ToList();
            var states = new Dictionary<string, MyState>(StringComparer.Ordinal);
            if (keys.Count == 0) return states;

            var placeholders = string.Join(",", keys.Select((_, i) => $"$k{i}"));

            using var cmd = con.CreateCommand();
            cmd.CommandText = $@"
                SELECT m.media_key,
                       (SELECT score FROM rating    WHERE user_id = $u AND media_key = m.media_key),
                       (SELECT 1     FROM watchlist WHERE user_id = $u AND media_key = m.media_key),
                       (SELECT 1     FROM follow    WHERE user_id = $u AND media_key = m.media_key)
                FROM media m
                WHERE m.media_key IN ({placeholders}) AND {SqlIsMine}";
            cmd.Parameters.AddWithValue("$u", user_id);
            for (var i = 0; i < keys.Count; i++)
                cmd.Parameters.AddWithValue($"$k{i}", keys[i]);

            using var rd = cmd.ExecuteReader();
            while (rd.Read())
                states[rd.GetString(0)] = new MyState(
                    Rating: rd.IsDBNull(1) ? (int?)null : rd.GetInt32(1),
                    InWatchlist: !rd.IsDBNull(2),
                    Following: !rd.IsDBNull(3),
                    Known: true);

            return states;
        }

        /// <summary>Tronque une valeur venue du client avant de la renvoyer en écho.</summary>
        private static string truncate(string? value)
        {
            value ??= "";
            return value.Length <= MaxEchoLength ? value : value[..MaxEchoLength] + "…";
        }
    }
}
