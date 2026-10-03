using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.EnhancedFin.Api
{
    public record SyncPlayInviteRequest(Guid groupId, Guid userId);

    /// <summary>
    /// Invitations SyncPlay : pousse « X t'invite à regarder ensemble » aux sessions d'un utilisateur.
    ///
    /// Transport = GeneralCommand **DisplayMessage** standard, et non un nom maison : le SDK Swift
    /// jette tout message dont le nom lui est inconnu. Un client qui ne connaît pas nos arguments
    /// affiche au moins le message ; SweetFin y lit le groupe et propose Rejoindre / Refuser.
    /// </summary>
    public class SyncPlayController : EnhancedFinController
    {
        /// <summary>
        /// Délai minimal entre deux invitations d'un même expéditeur à une même personne :
        /// l'invitation s'affiche aussi en message sur la webapp et les TV (faille n°8 de
        /// l'ancien plugin : spam).
        /// </summary>
        private static readonly TimeSpan InviteCooldown = TimeSpan.FromSeconds(10);

        /// <summary>Dernier envoi par (expéditeur, cible). Statique : un controller vit une requête.</summary>
        private static readonly ConcurrentDictionary<(Guid, Guid), DateTime> LastInvites = new();

        private readonly ISessionManager _sessions;
        private readonly ISyncPlayManager _syncplay;
        private readonly IUserManager _users;

        public SyncPlayController(ISessionManager sessions, ISyncPlayManager syncplay, IUserManager users)
        {
            _sessions = sessions;
            _syncplay = syncplay;
            _users = users;
        }

        // POST /api/EnhancedFin/v1/syncplay/invite — corps { groupId, userId }
        [HttpPost("syncplay/invite")]
        // Au plus 5 invitations par minute et par expéditeur, toutes cibles confondues :
        // le délai par cible ne suffit pas à empêcher d'inviter tout le serveur en boucle.
        [RateLimit("syncplay-invite", 5)]
        public async Task<ActionResult> invite([FromBody] SyncPlayInviteRequest body, CancellationToken ct)
        {
            var me = current_user();
            if (me is null) return not_authenticated();

            // Un champ absent devient Guid.Empty, qui désigne aussi les sessions sans utilisateur
            // (clés API) pour `SendMessageToUserSessions`.
            if (body.groupId == Guid.Empty || body.userId == Guid.Empty)
                return problem(400, "Requête invalide", "groupId et userId sont obligatoires.");
            if (body.userId == me.Value)
                return problem(400, "Requête invalide", "On ne s'invite pas soi-même.");
            if (_users.GetUserById(body.userId) is null)
                return problem(404, "Utilisateur introuvable", "Aucun utilisateur ne porte cet identifiant.");

            // L'expéditeur doit être dans le groupe : on ne peut pas inviter chez les autres,
            // ni au nom d'un autre (faille n°8 de l'ancien plugin, qui lisait l'expéditeur
            // dans le corps).
            var my_session = _sessions.Sessions.FirstOrDefault(s => s.UserId == me);
            var group = my_session is null ? null : _syncplay.GetGroup(my_session, body.groupId);
            var sender = _users.GetUserById(me.Value);
            if (group is null || sender is null || !group.Participants.Contains(sender.Username))
                return problem(403, "Hors du groupe", "On ne peut inviter que dans un groupe dont on fait partie.");

            var key = (me.Value, body.userId);
            var now = DateTime.UtcNow;
            if (LastInvites.TryGetValue(key, out var last) && now - last < InviteCooldown)
                return problem(429, "Trop d'invitations", "Attendre quelques secondes avant de réinviter.");
            LastInvites[key] = now;

            var command = new GeneralCommand
            {
                Name = GeneralCommandType.DisplayMessage,
                ControllingUserId = me.Value,
            };
            command.Arguments["Header"] = "SyncPlay";
            command.Arguments["Text"] = $"{sender.Username} t'invite à regarder ensemble";
            command.Arguments["TimeoutMs"] = "15000";
            command.Arguments["SyncPlayGroupId"] = body.groupId.ToString("N");
            command.Arguments["SyncPlayFrom"] = sender.Username;

            await _sessions.SendMessageToUserSessions(
                new List<Guid> { body.userId }, SessionMessageType.GeneralCommand, command, ct)
                .ConfigureAwait(false);

            // Un booléen, pas un nombre d'appareils : on ne révèle que ce qui est utile à
            // l'expéditeur. Seules comptent les sessions dont le socket est réellement ouvert.
            var delivered = _sessions.Sessions.Any(s =>
                s.ContainsUser(body.userId) && s.SessionControllers.Any(c => c.IsSessionActive));
            return Ok(new { delivered });
        }
    }
}
