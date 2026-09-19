using System;
using Jellyfin.Plugin.EnhancedFin.Data;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    /// <summary>
    /// Identité de l'utilisateur courant et compteurs de ses collections.
    ///
    /// Premier appel que fait un client au démarrage : il lui donne qui il est, ce
    /// qu'il a le droit de faire, et de quoi afficher les badges de navigation sans
    /// interroger chaque collection séparément.
    /// </summary>
    public class UserController : EnhancedFinController
    {
        private readonly Db _db;
        private readonly IUserManager _users;

        public UserController(Db db, IUserManager users)
        {
            _db = db;
            _users = users;
        }

        // GET /api/EnhancedFin/v1/me
        [HttpGet("me")]
        public ActionResult me()
        {
            var user_id = current_user_id();
            if (user_id is null) return not_authenticated();

            // Le nom et les droits restent la propriété de Jellyfin : on les lit chez
            // lui plutôt que d'en garder une copie qui dériverait.
            var user = Guid.TryParse(user_id, out var guid) ? _users.GetUserById(guid) : null;

            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // Les compteurs en une requête : quatre sous-requêtes scalaires coûtent
            // moins qu'un aller-retour HTTP par collection.
            cmd.CommandText = @"
                SELECT (SELECT COUNT(*) FROM rating      WHERE user_id = $u),
                       (SELECT COUNT(*) FROM watchlist   WHERE user_id = $u),
                       (SELECT COUNT(*) FROM follow      WHERE user_id = $u),
                       (SELECT COUNT(*) FROM hidden_item WHERE user_id = $u)";
            cmd.Parameters.AddWithValue("$u", user_id);

            using var rd = cmd.ExecuteReader();
            rd.Read();

            return Ok(new
            {
                id = user_id,
                name = user?.Username,
                // Le rôle est porté par le token, comme l'identité : pas besoin de
                // référencer Jellyfin.Data pour un seul booléen.
                isAdmin = User.IsInRole("Administrator"),
                counts = new
                {
                    ratings = rd.GetInt32(0),
                    watchlist = rd.GetInt32(1),
                    follows = rd.GetInt32(2),
                    hidden = rd.GetInt32(3),
                },
            });
        }
    }
}
