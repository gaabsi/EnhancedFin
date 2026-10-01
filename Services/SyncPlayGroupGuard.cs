using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Controller.SyncPlay.PlaybackRequests;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Services
{
    /// <summary>
    /// Arrête un groupe SyncPlay que plus personne ne regarde.
    ///
    /// Quitter le lecteur sans quitter le groupe (jellyfin-web le fait) le laisse « en
    /// lecture » côté serveur. Qui le rejoint plus tard part de « dernière position + temps
    /// écoulé » (`WaitingGroupState.SessionJoined`, sans borne) : 10 min plus loin, ou 6 h
    /// dans un film de 2 h.
    ///
    /// Arrêt plutôt que pause : rejoindre un groupe arrêté n'ouvre rien
    /// (`IdleGroupState.SessionJoined` n'envoie qu'un `stop`), on attend que quelqu'un relance
    /// un film, depuis **sa** reprise Jellyfin. ❌ Pause : le film s'ouvrait chez qui
    /// rejoignait alors que l'hôte était parti.
    ///
    /// Le serveur est le seul à voir tous les clients : SweetFin ne peut pas corriger le web.
    /// </summary>
    public class SyncPlayGroupGuard : IHostedService
    {
        /// <summary>
        /// Attente avant de juger le groupe abandonné. À la fin d'un épisode, le client
        /// arrête la lecture **avant** que le groupe passe au suivant : sans délai, on
        /// l'arrêterait au milieu de l'enchaînement.
        /// </summary>
        private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(5);

        private readonly ISessionManager _sessions;
        private readonly ISyncPlayManager _syncplay;
        private readonly ILogger<SyncPlayGroupGuard> _logger;

        public SyncPlayGroupGuard(ISessionManager sessions, ISyncPlayManager syncplay, ILogger<SyncPlayGroupGuard> logger)
        {
            _sessions = sessions;
            _syncplay = syncplay;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStopped += on_playback_stopped;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessions.PlaybackStopped -= on_playback_stopped;
            return Task.CompletedTask;
        }

        private void on_playback_stopped(object? sender, PlaybackStopEventArgs args)
        {
            var session = args.Session;
            if (session is null || !_syncplay.IsUserActive(session.UserId)) return;

            _ = stop_if_abandoned(session);
        }

        /// <summary>
        /// Après le délai de grâce, arrête le groupe de la session si plus aucun de ses
        /// membres ne regarde quoi que ce soit.
        ///
        /// Parametres :
        /// - session (SessionInfo) : la session qui vient d'arrêter la lecture
        /// </summary>
        private async Task stop_if_abandoned(SessionInfo session)
        {
            try
            {
                await Task.Delay(GracePeriod).ConfigureAwait(false);

                // Relu après le délai : un membre parti entre-temps n'est plus dans la liste,
                // et un `HandleRequest` hors groupe enverrait une erreur au client.
                // En pause aussi : l'hôte qui met en pause puis s'en va laisse un groupe qui
                // ouvrirait le film chez qui le rejoint. Pas en attente (chargement en cours).
                var group = _syncplay.ListGroups(session, new ListGroupsRequest())
                    .FirstOrDefault(g => g.State is GroupStateType.Playing or GroupStateType.Paused
                                         && g.Participants.Contains(session.UserName));
                if (group is null) return;

                // La session qui s'est arrêtée compte aussi : rejoindre un groupe ou enchaîner
                // l'épisode suivant arrête un média puis en relance un, pendant le délai.
                // ❌ L'exclure arrêtait le groupe qu'on venait de rejoindre.
                // Les membres ne sont connus que par leur nom : un même compte sur deux
                // appareils compte pour un. Cas rare, et l'erreur va dans le sens prudent.
                var someone_watching = _sessions.Sessions.Any(s =>
                    s.NowPlayingItem is not null && group.Participants.Contains(s.UserName));
                if (someone_watching) return;

                _syncplay.HandleRequest(session, new StopGroupRequest(), CancellationToken.None);
                _logger.LogInformation("[EnhancedFin] SyncPlay : groupe {Group} abandonné, arrêté", group.GroupId);
            }
            catch (Exception ex)
            {
                // Tâche détachée : une exception non attrapée serait perdue sans trace.
                _logger.LogError(ex, "[EnhancedFin] SyncPlay : échec de l'arrêt d'un groupe abandonné");
            }
        }
    }
}
