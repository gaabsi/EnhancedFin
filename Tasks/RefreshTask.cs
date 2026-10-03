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

        /// <summary>
        /// Délai avant de redemander à TMDB un champ qu'il n'avait pas.
        ///
        /// Logo, note et casting manquent définitivement pour une part du
        /// catalogue. Les retenter chaque jour ne les fait pas apparaître, mais
        /// suffit à saturer le lot quotidien avec les mêmes clés.
        /// </summary>
        private const int OptionalFieldRetryDays = 30;

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
            var followed = find_followed_media();
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
                await _catalog.ensure_exists(media_key, force: true, cancellation);
                progress.Report(100.0 * ++done / total);
            }

            foreach (var media_key in followed)
            {
                cancellation.ThrowIfCancellationRequested();

                var count = await _catalog.refresh_releases(media_key, cancellation);
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
            // Le logo, la note et le casting s'y ajoutent : ils n'ont été demandés à
            // TMDB qu'à partir du moment où `append_to_response` a été câblé, donc
            // tout ce qui a été peuplé avant en est dépourvu. `ensure_exists` sort
            // dès que le média existe, ils ne se rattraperaient jamais sans ça.
            //
            // Deux régimes, parce que ces critères n'ont pas le même statut :
            //
            // - `backdrop_url` et `year` sont attendus sur toute fiche — on les
            //   redemande sans délai ;
            // - logo, note et casting **n'existent pas** pour une part du catalogue.
            //   Un média que TMDB ignore ne les obtiendra jamais, et sans borne il
            //   revient dans le lot à chaque exécution, indéfiniment, en occupant
            //   50 places qui reviennent aux mêmes clés. La borne temporelle le
            //   laisse repasser, mais une fois par mois.
            cmd.CommandText = @"
                SELECT m.media_key FROM media m
                WHERE m.backdrop_url IS NULL
                   OR m.year IS NULL
                   OR (m.refreshed_at < $stale
                       AND (m.logo_url IS NULL
                            OR m.vote_average IS NULL
                            OR NOT EXISTS (SELECT 1 FROM media_detail d
                                           WHERE d.media_key = m.media_key
                                             AND d.cast_json IS NOT NULL)))
                ORDER BY m.refreshed_at
                LIMIT $limit";
            cmd.Parameters.AddWithValue(
                "$stale",
                DateTime.UtcNow.AddDays(-OptionalFieldRetryDays).ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$limit", BatchSize);

            var keys = new List<string>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) keys.Add(rd.GetString(0));

            return keys;
        }

        /// <summary>
        /// Médias suivis par au moins un utilisateur, dont les sorties n'ont pas été
        /// rafraîchies depuis plus d'une journée.
        ///
        /// Films compris : suivre un film enregistre sa date de sortie au calendrier,
        /// de la même façon qu'une série y enregistre ses épisodes.
        ///
        /// Output :
        /// - keys (List&lt;string&gt;) : clés média, séries **et films**, au plus BatchSize
        /// </summary>
        private List<string> find_followed_media()
        {
            using var con = _db.open();
            using var cmd = con.CreateCommand();

            // DISTINCT : deux utilisateurs suivant la même série ne la font rafraîchir
            // qu'une fois — `release` est un référentiel partagé.
            //
            // Un film déjà sorti est écarté : sa date ne bougera plus, et la relire
            // coûte un `get_item` complet par jour et par film suivi. Une série, elle,
            // annonce de nouveaux épisodes indéfiniment.
            cmd.CommandText = @"
                SELECT DISTINCT f.media_key
                FROM follow f JOIN media m ON m.media_key = f.media_key
                WHERE COALESCE((SELECT MAX(refreshed_at) FROM release r
                                WHERE r.media_key = f.media_key), '') < $cutoff
                  AND NOT (m.media_type = 'movie'
                           AND EXISTS (SELECT 1 FROM release r
                                       WHERE r.media_key = f.media_key
                                         AND r.air_date IS NOT NULL
                                         AND r.air_date <= $today))
                LIMIT $limit";
            cmd.Parameters.AddWithValue(
                "$cutoff", DateTime.UtcNow.AddDays(-1).ToString("o", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$today", DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$limit", BatchSize);

            var keys = new List<string>();
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) keys.Add(rd.GetString(0));

            return keys;
        }
    }
}
