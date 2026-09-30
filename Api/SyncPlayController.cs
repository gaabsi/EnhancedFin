using System;
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
        public async Task<ActionResult> invite([FromBody] SyncPlayInviteRequest body, CancellationToken ct)
        {
            var me = current_user();
            if (me is null) return not_authenticated();

            // L'expéditeur doit être dans le groupe : on ne peut pas inviter chez les autres,
            // ni au nom d'un autre (faille n°8 de l'ancien plugin, qui lisait l'expéditeur
            // dans le corps).
            var my_session = _sessions.Sessions.FirstOrDefault(s => s.UserId == me);
            var group = my_session is null ? null : _syncplay.GetGroup(my_session, body.groupId);
            var sender = _users.GetUserById(me.Value);
            if (group is null || sender is null || !group.Participants.Contains(sender.Username))
                return problem(403, "Hors du groupe", "On ne peut inviter que dans un groupe dont on fait partie.");

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

            // Combien d'appareils de la cible l'ont reçue : 0 = pas connecté, le client le dit.
            return Ok(new { delivered = _sessions.Sessions.Count(s => s.UserId == body.userId) });
        }
    }
}
