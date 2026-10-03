using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>Corps de `POST me/requests/{mediaKey}`.</summary>
    public class SeerrRequestBody
    {
        /// <summary>Saisons demandées, pour une série. Ignoré pour un film.</summary>
        public int[]? Seasons { get; set; }
    }

    /// <summary>
    /// Demandes Seerr : le statut d'un média (et de ses saisons), et la création d'une
    /// demande.
    ///
    /// L'utilisateur vient **du jeton**, jamais du corps : lu dans le JSON, n'importe quel
    /// compte pourrait demander au nom d'un autre, et consommer ses quotas Seerr.
    /// </summary>
    public class SeerrController : EnhancedFinController
    {
        /// <summary>Plus de saisons qu'aucune série n'en a : borne le corps de la requête.</summary>
        private const int MaxSeasons = 100;

        private readonly SeerrClient _seerr;

        public SeerrController(SeerrClient seerr)
        {
            _seerr = seerr;
        }

        // GET /api/EnhancedFin/v1/seerr/{mediaKey}
        // Le statut Seerr et, pour une série, celui de chaque saison : de quoi griser dans
        // la fenêtre de demande ce qui est déjà disponible ou en attente.
        [HttpGet("seerr/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> status(string mediaKey)
        {
            if (current_user() is null) return not_authenticated();
            if (MediaCatalog.split(mediaKey) is not { } key) return invalid_media_key(mediaKey);

            if (await _seerr.details(key.Type, key.TmdbId) is not { } details)
                return seerr_unavailable();

            return Ok(new
            {
                status = details.Status,
                seasons = details.Seasons.Select(s => new
                {
                    number = s.Number,
                    name = s.Name,
                    episodeCount = s.EpisodeCount,
                    airDate = s.AirDate,
                    status = s.Status,
                }),
            });
        }

        // POST /api/EnhancedFin/v1/me/requests/{mediaKey}
        // Crée la demande **au nom de l'utilisateur du jeton**, avec ses droits Seerr.
        [HttpPost("me/requests/{mediaKey}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult> request(string mediaKey, [FromBody] SeerrRequestBody? body)
        {
            if (current_user() is not { } user) return not_authenticated();
            if (MediaCatalog.split(mediaKey) is not { } key) return invalid_media_key(mediaKey);

            var seasons = (body?.Seasons ?? System.Array.Empty<int>())
                .Where(s => s > 0)
                .Distinct()
                .ToArray();

            if (key.Type == "tv" && seasons.Length == 0)
                return problem(400, "Saisons manquantes", "Choisis au moins une saison à demander.");
            if (seasons.Length > MaxSeasons)
                return problem(400, "Trop de saisons", $"Au plus {MaxSeasons} saisons par demande.");

            if (await _seerr.find_user_id(user) is not { } seerr_user)
                return problem(403, "Compte Seerr introuvable",
                    "Aucun compte Seerr n'est lié à ton compte Jellyfin. Contacte l'admin.");

            if (await _seerr.request(seerr_user, key.Type, key.TmdbId, seasons) is { } error)
                return problem(502, "Demande refusée", error);

            return NoContent();
        }

        private ObjectResult seerr_unavailable() =>
            problem(503, "Seerr indisponible", "Seerr n'est pas configuré ou ne répond pas.");
    }
}
