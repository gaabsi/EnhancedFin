using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    public record SeerrMediaInfo(
        [property: JsonPropertyName("status")] int? Status);

    public record SeerrTv(
        [property: JsonPropertyName("mediaInfo")] SeerrMediaInfo? MediaInfo);

    /// <summary>Réponse de Seerr sur une série. `Status` nul : Seerr ne la suit pas.</summary>
    public record SeerrAvailability(int? Status);

    /// <summary>
    /// Disponibilité d'une série selon Seerr, pour la carte « saisons manquantes ».
    ///
    /// Méthode reprise du plugin plugin précédent (`detail_page.js`, carte « see-more ») :
    /// `GET /api/v1/tv/{tmdbId}` → `mediaInfo.status`, et **5 = entièrement
    /// disponible**. Seerr fait lui-même la comparaison bibliothèque Jellyfin / épisodes
    /// sortis ; la refaire ici serait une seconde vérité.
    /// </summary>
    public class SeerrClient
    {
        /// <summary>
        /// Court : le statut change dès qu'un téléchargement se termine, et une carte
        /// « + » restée affichée sur une série désormais complète induirait en erreur.
        /// </summary>
        private static readonly TimeSpan _ttl = TimeSpan.FromHours(1);

        /// <summary>Pendant l'ouverture d'une fiche : mieux vaut pas de carte qu'une attente.</summary>
        private static readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(3) };

        private static readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 2000 });

        private readonly ILogger<SeerrClient> _logger;

        public SeerrClient(ILogger<SeerrClient> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Statut d'une série dans Seerr.
        ///
        /// Parametres :
        /// - tmdb_id (int) : identifiant TMDB de la série
        ///
        /// Output :
        /// - availability (SeerrAvailability | null) : réponse de Seerr ; null s'il n'est
        ///   pas configuré ou n'a pas répondu — l'appelant n'affiche alors rien
        /// </summary>
        public async Task<SeerrAvailability?> tv_availability(int tmdb_id)
        {
            var cache_key = $"tv|{tmdb_id}";
            if (_cache.TryGetValue(cache_key, out SeerrAvailability? hit)) return hit;

            var config = Plugin.Instance?.Configuration;
            if (string.IsNullOrWhiteSpace(config?.SeerrUrl) || string.IsNullOrWhiteSpace(config.SeerrApiKey)) return null;

            var path = $"/api/v1/tv/{tmdb_id}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, config.SeerrUrl.TrimEnd('/') + path);
                request.Headers.Add("X-Api-Key", config.SeerrApiKey);

                using var response = await _client.SendAsync(request);

                // Série que Seerr ne connaît pas : une réponse, pas une panne.
                var availability = response.StatusCode == HttpStatusCode.NotFound
                    ? new SeerrAvailability(null)
                    : response.IsSuccessStatusCode
                        ? new SeerrAvailability(
                            JsonSerializer.Deserialize<SeerrTv>(await response.Content.ReadAsStringAsync())?.MediaInfo?.Status)
                        : null;

                if (availability is null)
                {
                    _logger.LogWarning("[EnhancedFin] Seerr {Path} → HTTP {Code}", path, (int)response.StatusCode);
                    return null;
                }

                _cache.Set(cache_key, availability, new MemoryCacheEntryOptions
                {
                    Size = 1,
                    AbsoluteExpirationRelativeToNow = _ttl,
                });
                return availability;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // `ex.Message` seul, comme pour MDBList : ne rien laisser fuir de la requête.
                _logger.LogWarning("[EnhancedFin] Seerr {Path} a échoué : {Message}", path, ex.Message);
                return null;
            }
        }
    }
}
