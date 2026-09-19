using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.EnhancedFin.Data;
using Jellyfin.Plugin.EnhancedFin.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Tasks
{
    /// <summary>
    /// Entretien du référentiel : complète les fiches incomplètes et rafraîchit les
    /// dates de diffusion des séries suivies.
    ///
    /// Deux besoins distincts mais liés, traités ensemble pour ne pas multiplier les
    /// tâches sur un même sujet :
    ///
    /// 1. **Fiches incomplètes** — les médias issus de la migration n'ont ni image de
    ///    fond ni année : l'ancienne table `ratings` ne les stockait pas. Leurs tuiles
    ///    seraient plus pauvres que celles des médias découverts après coup.
    ///
    /// 2. **Dates de diffusion** — sans rafraîchissement, le calendrier reste figé sur
    ///    les données migrées et ignore les épisodes annoncés depuis.
    /// </summary>
    public class RefreshTask : IScheduledTask
    {
        /// <summary>
        /// Nombre maximum de médias traités par exécution. Borne la durée de la tâche
        /// et la charge sur TMDB ; le reliquat est repris au passage suivant.
        /// </summary>
        private const int BatchSize = 50;

        private readonly Db _db;
        private readonly MediaCatalog _catalog;
        private readonly ILogger<RefreshTask> _logger;

        public RefreshTask(Db db, MediaCatalog catalog, ILogger<RefreshTask> logger)
        {
            _db = db;
            _catalog = catalog;
            _logger = logger;
        }

        public string Name => "EnhancedFin — entretien du référentiel";
        public string Key => "EnhancedFinRefresh";
        public string Description => "Complète les fiches média incomplètes et met à jour les dates de diffusion des séries suivies.";
        public string Category => "EnhancedFin";

        /// <summary>Une fois par jour : les métadonnées TMDB bougent peu.</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
        };

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellation)
        {
            var incomplete = find_incomplete_media();
            var followed = find_followed_series();
            var total = incomplete.Count + followed.Count;

            if (total == 0)
            {
                progress.Report(100);
                return;
            }

            _logger.LogInformation(
                "[EnhancedFin] Entretien : {Incomplete} fiches à compléter, {Followed} séries à rafraîchir",
                incomplete.Count, followed.Count);

            var done = 0;

            foreach (var media_key in incomplete)
            {
                cancellation.ThrowIfCancellationRequested();

                // `force` est indispensable : sans lui, ensure_exists sort dès que le
                // média existe et cette boucle ne fait rien. C'est l'UPSERT qui
                // complète la fiche (année, images, synopsis).
                await _catalog.ensure_exists(media_key, force: true);
                progress.Report(100.0 * ++done / total);
            }

            foreach (var media_key in followed)
            {
                cancellation.ThrowIfCancellationRequested();

                var count = await _catalog.refresh_releases(media_key);
                if (count > 0)
                    _logger.LogDebug("[EnhancedFin] {Key} : {Count} épisodes", media_key, count);

                progress.Report(100.0 * ++done / total);
            }

            _logger.LogInformation("[EnhancedFin] Entretien terminé ({Done} médias)", done);
        }

        /// <summary>
        /// Médias dont la fiche est incomplète, les plus anciennement rafraîchis d'abord.
        ///
        /// Output :
        /// - keys (List&lt;string&gt;) : clés média à compléter, au plus BatchSize
        /// </summary>
        private List<string> find_incomplete_media()
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // L'absence d'image de fond ou d'année signale une fiche issue de la
            // migration : `ratings` et `watchlist` ne portaient pas ces champs.
            // Le logo s'y ajoute : il n'a été demandé à TMDB qu'à partir du moment
            // où `append_to_response=images` a été câblé, donc tout ce qui a été
            // peuplé avant en est dépourvu.
            //
            // Un média sans logo chez TMDB repassera ici à chaque cycle. C'est sans
            // conséquence : `ORDER BY refreshed_at` le renvoie en fin de file, et le
            // lot est de 50 par jour.
            cmd.CommandText = @"
                SELECT media_key FROM media
                WHERE backdrop_url IS NULL OR year IS NULL OR logo_url IS NULL
                ORDER BY refreshed_at
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$limit", BatchSize);

            var keys = new List<string>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) keys.Add(rd.GetString(0));

            return keys;
        }

        /// <summary>
        /// Séries suivies par au moins un utilisateur, dont les sorties n'ont pas été
        /// rafraîchies depuis plus d'une journée.
        ///
        /// Output :
        /// - keys (List&lt;string&gt;) : clés de séries, au plus BatchSize
        /// </summary>
        private List<string> find_followed_series()
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // DISTINCT : deux utilisateurs suivant la même série ne la font rafraîchir
            // qu'une fois — `release` est un référentiel partagé.
            cmd.CommandText = @"
                SELECT DISTINCT f.media_key
                FROM follow f JOIN media m ON m.media_key = f.media_key
                WHERE m.media_type = 'tv'
                  AND COALESCE((SELECT MAX(refreshed_at) FROM release r
                                WHERE r.media_key = f.media_key), '') < $cutoff
                LIMIT $limit";
            cmd.Parameters.AddWithValue(
                "$cutoff", DateTime.UtcNow.AddDays(-1).ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$limit", BatchSize);

            var keys = new List<string>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) keys.Add(rd.GetString(0));

            return keys;
        }
    }
}
