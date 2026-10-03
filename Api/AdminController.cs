using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Services;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>Corps d'un test de clé : les valeurs **saisies**, pas celles enregistrées.</summary>
    public record ServiceCheckRequest(string? key, string? url);

    /// <summary>
    /// Routes de la page de réglages du plugin, réservées aux administrateurs.
    ///
    /// Le test porte sur ce qui vient d'être tapé, avant tout enregistrement : on sait
    /// tout de suite si une clé est bonne, au lieu de le découvrir à la première fiche
    /// vide. La réponse ne contient jamais la clé, seulement le verdict.
    /// </summary>
    public class AdminController : EnhancedFinController
    {
        private readonly TmdbClient _tmdb;
        private readonly MdblistClient _mdblist;
        private readonly SeerrClient _seerr;

        public AdminController(TmdbClient tmdb, MdblistClient mdblist, SeerrClient seerr)
        {
            _tmdb = tmdb;
            _mdblist = mdblist;
            _seerr = seerr;
        }

        // POST /api/EnhancedFin/v1/admin/check/{tmdb|mdblist|seerr} — corps { key, url? }
        [HttpPost("admin/check/{service}")]
        [RateLimit("outbound", 60)]
        public async Task<ActionResult<ServiceCheck>> check(string service, [FromBody] ServiceCheckRequest body)
        {
            if (current_user() is null) return not_authenticated();

            // Rôle posé par Jellyfin sur le jeton d'un administrateur. Le test fait appeler
            // au serveur une URL saisie (Seerr) : il ne doit pas être ouvert à tous.
            if (!User.IsInRole("Administrator"))
                return problem(403, "Réservé aux administrateurs", "Seul un administrateur peut tester les clés du plugin.");

            var key = body.key ?? string.Empty;
            return service switch
            {
                "tmdb" => await _tmdb.check(key),
                "mdblist" => await _mdblist.check(key),
                "seerr" => await _seerr.check(body.url ?? string.Empty, key),
                _ => problem(404, "Service inconnu", "service vaut 'tmdb', 'mdblist' ou 'seerr'."),
            };
        }
    }
}
