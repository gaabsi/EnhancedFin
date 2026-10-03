using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    // --- Réponses brutes de Seerr (`GET /api/v1/{movie|tv}/{tmdbId}`) ---

    public record SeerrSeasonInfo(
        [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
        [property: JsonPropertyName("status")] int? Status);

    public record SeerrMediaInfo(
        [property: JsonPropertyName("status")] int? Status,
        [property: JsonPropertyName("seasons")] List<SeerrSeasonInfo>? Seasons);

    public record SeerrSeason(
        [property: JsonPropertyName("seasonNumber")] int SeasonNumber,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("episodeCount")] int? EpisodeCount,
        [property: JsonPropertyName("airDate")] string? AirDate);

    public record SeerrMedia(
        [property: JsonPropertyName("mediaInfo")] SeerrMediaInfo? MediaInfo,
        [property: JsonPropertyName("seasons")] List<SeerrSeason>? Seasons);

    // --- Ce que le plugin en fait ---

    /// <summary>Une saison et son statut Seerr (1 inconnu … 5 disponible).</summary>
    public record SeerrSeasonStatus(int Number, string? Name, int EpisodeCount, string? AirDate, int Status);

    /// <summary>
    /// Un média selon Seerr. `Status` nul : Seerr ne le suit pas. `Seasons` est vide
    /// pour un film.
    /// </summary>
    public record SeerrDetails(int? Status, IReadOnlyList<SeerrSeasonStatus> Seasons);

    /// <summary>
    /// Client Seerr (Jellyseerr / Overseerr) : lecture des statuts et demandes.
    ///
    /// Repris du plugin plugin précédent (`SeerrClient`, `SeerrController`). Statuts Seerr :
    /// 1 inconnu, 2 en attente, 3 en cours, 4 partiellement disponible, **5 disponible**.
    /// Seerr fait lui-même la comparaison bibliothèque Jellyfin / épisodes sortis ; la
    /// refaire ici serait une seconde vérité.
    ///
    /// Une demande part **au nom de l'utilisateur** (`X-Api-User`) : ses quotas et ses
    /// droits Seerr s'appliquent, la clé API admin ne sert que de transport.
    /// </summary>
    public class SeerrClient
    {
        /// <summary>Statut d'une saison que Seerr ne mentionne pas : jamais demandée.</summary>
        private const int Unknown = 1;

        /// <summary>
        /// Court : le statut change dès qu'un téléchargement se termine, et une fiche qui
        /// propose encore une demande déjà servie induirait en erreur.
        /// </summary>
        private static readonly TimeSpan _ttl = TimeSpan.FromHours(1);

        /// <summary>Correspondance compte Jellyfin → compte Seerr : change rarement.</summary>
        private static readonly TimeSpan _users_ttl = TimeSpan.FromMinutes(10);

        /// <summary>Pendant l'ouverture d'une fiche : mieux vaut rien qu'une attente.</summary>
        private static readonly HttpClient _read_client = new() { Timeout = TimeSpan.FromSeconds(3) };

        /// <summary>Une demande, elle, est un geste explicite : on peut attendre Seerr.</summary>
        private static readonly HttpClient _write_client = new() { Timeout = TimeSpan.FromSeconds(15) };

        private static readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 2000 });

        private readonly ILogger<SeerrClient> _logger;

        public SeerrClient(ILogger<SeerrClient> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Statut d'un média dans Seerr, et de chacune de ses saisons pour une série.
        ///
        /// Parametres :
        /// - type (string) : 'movie' ou 'tv'
        /// - tmdb_id (int) : identifiant TMDB
        ///
        /// Output :
        /// - details (SeerrDetails | null) : réponse de Seerr ; null s'il n'est pas
        ///   configuré ou n'a pas répondu — l'appelant n'affiche alors rien
        /// </summary>
        public async Task<SeerrDetails?> details(string type, int tmdb_id)
        {
            var cache_key = cache_key_of(type, tmdb_id);
            if (_cache.TryGetValue(cache_key, out SeerrDetails? hit)) return hit;

            var (ok, status, body) = await send(_read_client, HttpMethod.Get, $"/api/v1/{type}/{tmdb_id}");

            // Média que Seerr ne connaît pas : une réponse, pas une panne.
            var details = status == (int)HttpStatusCode.NotFound
                ? new SeerrDetails(null, Array.Empty<SeerrSeasonStatus>())
                : ok ? parse_details(body) : null;

            if (details is null) return null;

            _cache.Set(cache_key, details, new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = _ttl,
            });
            return details;
        }

        /// <summary>
        /// Le compte Seerr lié à un compte Jellyfin.
        ///
        /// Parametres :
        /// - jellyfin_user (Guid) : l'utilisateur, **tiré du jeton** par l'appelant
        ///
        /// Output :
        /// - id (int | null) : identifiant Seerr ; null si aucun compte n'est lié, ou si
        ///   Seerr n'a pas répondu
        /// </summary>
        public async Task<int?> find_user_id(Guid jellyfin_user)
        {
            if (!_cache.TryGetValue("users", out Dictionary<Guid, int>? users) || users is null)
            {
                var (ok, _, body) = await send(_write_client, HttpMethod.Get, "/api/v1/user?take=500");
                if (!ok) return null;

                users = parse_users(body);
                if (users is null) return null;
                _cache.Set("users", users, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = _users_ttl,
                });
            }

            return users.TryGetValue(jellyfin_user, out var id) ? id : null;
        }

        /// <summary>
        /// Crée une demande dans Seerr, au nom d'un utilisateur. Le statut en cache du
        /// média est oublié : il vient de changer.
        ///
        /// Parametres :
        /// - seerr_user_id (int) : le compte Seerr qui demande
        /// - type (string) : 'movie' ou 'tv'
        /// - tmdb_id (int) : identifiant TMDB
        /// - seasons (int[]) : saisons demandées ; ignoré pour un film
        ///
        /// Output :
        /// - error (string | null) : le message de Seerr en cas de refus, null si la
        ///   demande est créée
        /// </summary>
        public async Task<string?> request(int seerr_user_id, string type, int tmdb_id, int[] seasons)
        {
            object payload = type == "tv"
                ? new { mediaType = type, mediaId = tmdb_id, seasons }
                : new { mediaType = type, mediaId = tmdb_id };

            var (ok, status, body) = await send(
                _write_client, HttpMethod.Post, "/api/v1/request", payload, seerr_user_id);

            if (ok)
            {
                _cache.Remove(cache_key_of(type, tmdb_id));
                return null;
            }

            return status == 0
                ? "Seerr ne répond pas."
                : seerr_message(body) ?? $"Seerr a refusé la demande (HTTP {status}).";
        }

        // --- Transport ---

        /// <summary>
        /// Envoie une requête à Seerr avec la clé API, et au nom d'un utilisateur si
        /// `as_user` est donné.
        ///
        /// Output :
        /// - result ((bool ok, int status, string body)) : `status` = 0 si Seerr n'est pas
        ///   configuré ou injoignable
        /// </summary>
        private async Task<(bool ok, int status, string body)> send(
            HttpClient client, HttpMethod method, string path, object? payload = null, int? as_user = null)
        {
            var config = Plugin.Instance?.Configuration;
            if (string.IsNullOrWhiteSpace(config?.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey))
                return (false, 0, "");

            try
            {
                using var request = new HttpRequestMessage(method, config.SeerrUrl.TrimEnd('/') + path);
                request.Headers.Add("X-Api-Key", config.SeerrApiKey);
                if (as_user is { } user) request.Headers.Add("X-Api-User", user.ToString());
                if (payload is not null)
                    request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                    _logger.LogWarning("[EnhancedFin] Seerr {Method} {Path} → HTTP {Code}", method, path, (int)response.StatusCode);

                return (response.IsSuccessStatusCode, (int)response.StatusCode, body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // `ex.Message` seul, comme pour MDBList : ne rien laisser fuir de la requête.
                _logger.LogWarning("[EnhancedFin] Seerr {Method} {Path} a échoué : {Message}", method, path, ex.Message);
                return (false, 0, "");
            }
        }

        // --- Lecture des réponses ---

        private static string cache_key_of(string type, int tmdb_id) => $"{type}|{tmdb_id}";

        /// <summary>
        /// Statut global et par saison. Les spéciaux (saison 0) sont écartés, comme dans
        /// plugin précédent : Seerr ne les demande pas.
        /// </summary>
        private SeerrDetails? parse_details(string body)
        {
            try
            {
                var media = JsonSerializer.Deserialize<SeerrMedia>(body);
                if (media is null) return null;

                var statuses = (media.MediaInfo?.Seasons ?? new())
                    .GroupBy(s => s.SeasonNumber)
                    .ToDictionary(g => g.Key, g => g.First().Status ?? Unknown);

                var seasons = (media.Seasons ?? new())
                    .Where(s => s.SeasonNumber > 0)
                    .OrderBy(s => s.SeasonNumber)
                    .Select(s => new SeerrSeasonStatus(
                        s.SeasonNumber,
                        s.Name,
                        s.EpisodeCount ?? 0,
                        s.AirDate,
                        statuses.TryGetValue(s.SeasonNumber, out var st) ? st : Unknown))
                    .ToList();

                return new SeerrDetails(media.MediaInfo?.Status, seasons);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning("[EnhancedFin] Réponse Seerr illisible : {Message}", ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Comptes Seerr indexés par compte Jellyfin, null si la réponse est illisible.
        /// Seerr stocke l'identifiant sans tirets ; `Guid.TryParse` accepte les deux formes.
        /// </summary>
        private static Dictionary<Guid, int>? parse_users(string body)
        {
            var users = new Dictionary<Guid, int>();
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("results", out var results)) return users;

                foreach (var u in results.EnumerateArray())
                {
                    if (u.TryGetProperty("jellyfinUserId", out var jid)
                        && jid.ValueKind == JsonValueKind.String
                        && Guid.TryParse(jid.GetString(), out var jellyfin_id)
                        && u.TryGetProperty("id", out var id)
                        && id.TryGetInt32(out var seerr_id))
                    {
                        users[jellyfin_id] = seerr_id;
                    }
                }
            }
            catch (JsonException)
            {
                // Illisible n'est pas « aucun compte lié » : ne rien mettre en cache.
                return null;
            }

            return users;
        }

        /// <summary>
        /// Le message d'erreur de Seerr (`{ "message": … }`), tronqué : il est renvoyé
        /// tel quel à l'utilisateur.
        /// </summary>
        private static string? seerr_message(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    var message = m.GetString() ?? "";
                    return message.Length > 200 ? message[..200] : message;
                }
            }
            catch (JsonException) { }

            return null;
        }
    }
}
