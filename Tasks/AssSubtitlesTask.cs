using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.EnhancedFin.Tasks
{
    /// <summary>
    /// Remplace les sous-titres ASS intégrés par des `.srt` posés à côté du film.
    ///
    /// Le lecteur de SweetFin (libass de SwiftVLC, compilé sans police iOS) décode
    /// l'ASS mais n'en dessine rien. Le srt, lui, passe par freetype et s'affiche.
    /// Seul le style ASS est perdu (position, couleur), jamais le texte.
    ///
    /// Pour chaque piste ASS d'un mkv :
    /// 1. Jellyfin la convertit en srt (`ISubtitleEncoder`, l'outil qui sert les clients) ;
    /// 2. le srt est écrit en `&lt;film&gt;.&lt;lang&gt;[.sdh][.forced].srt`, nom que Jellyfin
    ///    reconnaît comme sous-titre externe ;
    /// 3. le mkv est remuxé **sans** ses pistes ASS (copie, aucun réencodage), vérifié,
    ///    puis substitué à l'original par un renommage atomique.
    ///
    /// Idempotente : un mkv sans ASS n'est pas touché, un srt existant n'est pas réécrit.
    /// Sur un dossier en lecture seule (serveur de test), rien n'est écrit : la tâche
    /// journalise ce qu'elle aurait fait.
    /// </summary>
    public class AssSubtitlesTask : IScheduledTask, ILibraryPostScanTask
    {
        /// <summary>
        /// Garde l'original, renommé en fichier caché à côté du film (Jellyfin ignore les
        /// fichiers qui commencent par un point). Sécurité du premier passage en prod :
        /// à repasser à false une fois le résultat vérifié, puis supprimer les sauvegardes.
        /// </summary>
        private const bool KeepBackup = true;

        /// <summary>Écart de durée toléré entre l'original et le remux, en secondes.</summary>
        private const double DurationTolerance = 1.0;

        private static readonly string[] AssCodecs = { "ass", "ssa" };

        private readonly ILibraryManager _library;
        private readonly IMediaSourceManager _media_sources;
        private readonly ISubtitleEncoder _subtitles;
        private readonly IMediaEncoder _encoder;
        private readonly ILocalizationManager _localization;
        private readonly IProviderManager _providers;
        private readonly IFileSystem _file_system;
        private readonly ILogger<AssSubtitlesTask> _logger;

        public AssSubtitlesTask(
            ILibraryManager library,
            IMediaSourceManager media_sources,
            ISubtitleEncoder subtitles,
            IMediaEncoder encoder,
            ILocalizationManager localization,
            IProviderManager providers,
            IFileSystem file_system,
            ILogger<AssSubtitlesTask> logger)
        {
            _library = library;
            _media_sources = media_sources;
            _subtitles = subtitles;
            _encoder = encoder;
            _localization = localization;
            _providers = providers;
            _file_system = file_system;
            _logger = logger;
        }

        public string Name => "EnhancedFin — sous-titres ASS en srt";
        public string Key => "EnhancedFinAssSubtitles";
        public string Description => "Convertit les sous-titres ASS intégrés en .srt externes et les retire du fichier.";
        public string Category => "EnhancedFin";

        /// <summary>Aucun déclencheur planifié : la tâche suit chaque scan de médiathèque.</summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellation)
            => Run(progress, cancellation);

        public async Task Run(IProgress<double> progress, CancellationToken cancellation)
        {
            var videos = find_videos_with_ass();
            if (videos.Count == 0)
            {
                progress.Report(100);
                return;
            }

            _logger.LogInformation("[EnhancedFin] Sous-titres ASS : {Count} fichiers concernés", videos.Count);

            var done = 0;
            foreach (var (video, streams) in videos)
            {
                cancellation.ThrowIfCancellationRequested();

                try
                {
                    await convert_video(video, streams, cancellation);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Un film en échec ne bloque pas les suivants : l'original est intact
                    // tant que le renommage final n'a pas eu lieu.
                    _logger.LogError(ex, "[EnhancedFin] Sous-titres ASS : échec sur {Path}", video.Path);
                }

                progress.Report(100.0 * ++done / videos.Count);
            }
        }

        /// <summary>
        /// Vidéos locales en mkv qui portent au moins une piste ASS intégrée.
        ///
        /// Output :
        /// - videos (List) : chaque vidéo avec ses pistes ASS
        /// </summary>
        private List<(Video Video, List<MediaStream> Streams)> find_videos_with_ass()
        {
            var query = new InternalItemsQuery
            {
                MediaTypes = new[] { MediaType.Video },
                IsVirtualItem = false,
                Recursive = true,
            };

            var videos = new List<(Video, List<MediaStream>)>();
            foreach (var item in _library.GetItemList(query).OfType<Video>())
            {
                // Seul le mkv porte de l'ASS ; un `.strm` n'a pas de pistes connues.
                if (!string.Equals(Path.GetExtension(item.Path), ".mkv", StringComparison.OrdinalIgnoreCase))
                    continue;

                var streams = _media_sources.GetMediaStreams(item.Id)
                    .Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal
                                && AssCodecs.Contains(s.Codec?.ToLowerInvariant()))
                    .ToList();

                if (streams.Count > 0) videos.Add((item, streams));
            }

            return videos;
        }

        /// <summary>
        /// Convertit les pistes ASS d'une vidéo en srt, puis les retire du mkv.
        ///
        /// Parametres :
        /// - video (Video) : la vidéo
        /// - streams (List&lt;MediaStream&gt;) : ses pistes ASS
        /// - cancellation (CancellationToken) : annulation de la tâche
        /// </summary>
        private async Task convert_video(Video video, List<MediaStream> streams, CancellationToken cancellation)
        {
            var srts = new Dictionary<string, byte[]>();
            foreach (var stream in streams)
            {
                var srt_path = srt_path_for(video.Path, stream);
                if (srts.ContainsKey(srt_path))
                {
                    // Deux pistes de même langue et même type : on garde la première.
                    _logger.LogWarning("[EnhancedFin] {Path} : piste {Index} ignorée, {Srt} déjà prévu", video.Path, stream.Index, srt_path);
                    continue;
                }

                srts[srt_path] = await extract_srt(video, stream, cancellation);
            }

            var folder = Path.GetDirectoryName(video.Path)!;
            if (!is_writable(folder))
            {
                foreach (var (srt_path, content) in srts)
                    _logger.LogInformation("[EnhancedFin] Lecture seule, aurait écrit {Srt} ({Bytes} octets)", srt_path, content.Length);
                _logger.LogInformation(
                    "[EnhancedFin] Lecture seule, aurait retiré les pistes {Indexes} de {Path}",
                    string.Join(", ", streams.Select(s => s.Index)), video.Path);
                return;
            }

            foreach (var (srt_path, content) in srts)
            {
                // Un srt déjà là (passage précédent interrompu, ou fourni à la main) fait foi.
                if (!File.Exists(srt_path)) await File.WriteAllBytesAsync(srt_path, content, cancellation);
            }

            await remove_streams(video.Path, streams, cancellation);

            _providers.QueueRefresh(
                video.Id,
                new MetadataRefreshOptions(new DirectoryService(_file_system)),
                RefreshPriority.High);

            _logger.LogInformation("[EnhancedFin] {Path} : {Count} pistes ASS converties en srt", video.Path, srts.Count);
        }

        /// <summary>
        /// Nom du srt externe d'une piste, selon la convention de Jellyfin :
        /// `Coco.fr.srt`, `Kiki.fr.forced.srt`, `Film.en.sdh.srt`.
        ///
        /// Le forcé se lit aussi dans le titre : certaines pistes ne le disent que là
        /// (« FR Forced ASS » chez Kiki), sans le drapeau.
        ///
        /// Parametres :
        /// - video_path (string) : chemin de la vidéo
        /// - stream (MediaStream) : la piste ASS
        ///
        /// Output :
        /// - path (string) : chemin du srt à côté de la vidéo
        /// </summary>
        private string srt_path_for(string video_path, MediaStream stream)
        {
            var language = _localization.FindLanguageInfo(stream.Language ?? string.Empty)?.TwoLetterISOLanguageName
                           ?? stream.Language
                           ?? "und";
            var title = stream.Title ?? string.Empty;
            var is_forced = stream.IsForced || title.Contains("forced", StringComparison.OrdinalIgnoreCase);

            var suffix = "." + language;
            if (stream.IsHearingImpaired) suffix += ".sdh";
            if (is_forced) suffix += ".forced";

            return Path.ChangeExtension(video_path, null) + suffix + ".srt";
        }

        /// <summary>
        /// Demande à Jellyfin la piste convertie en srt.
        ///
        /// Parametres :
        /// - video (Video) : la vidéo
        /// - stream (MediaStream) : la piste ASS
        /// - cancellation (CancellationToken) : annulation de la tâche
        ///
        /// Output :
        /// - content (byte[]) : le srt, jamais vide
        /// </summary>
        private async Task<byte[]> extract_srt(Video video, MediaStream stream, CancellationToken cancellation)
        {
            await using var source = await _subtitles.GetSubtitles(
                video, video.Id.ToString("N", CultureInfo.InvariantCulture), stream.Index, "srt", 0, 0, false, cancellation);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellation);

            if (buffer.Length == 0)
                throw new InvalidOperationException($"srt vide pour la piste {stream.Index}");

            return buffer.ToArray();
        }

        /// <summary>
        /// Réécrit le mkv sans les pistes données, vérifie le résultat, puis le substitue
        /// à l'original. Tant que le renommage final n'a pas eu lieu, l'original est intact.
        ///
        /// Parametres :
        /// - path (string) : chemin du mkv
        /// - streams (List&lt;MediaStream&gt;) : pistes à retirer
        /// - cancellation (CancellationToken) : annulation de la tâche
        /// </summary>
        private async Task remove_streams(string path, List<MediaStream> streams, CancellationToken cancellation)
        {
            var folder = Path.GetDirectoryName(path)!;
            var name = Path.GetFileName(path);
            var temp = Path.Combine(folder, "." + name + ".remux.mkv");

            // `-map 0` garde tout (vidéo, audio, chapitres, pièces jointes), puis chaque
            // `-map -0:i` retire une piste ASS. `-c copy` : aucun réencodage.
            var args = new List<string> { "-nostdin", "-v", "error", "-y", "-i", path, "-map", "0" };
            foreach (var stream in streams) args.AddRange(new[] { "-map", "-0:" + stream.Index.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-c", "copy", "-f", "matroska", temp });

            try
            {
                await run(_encoder.EncoderPath, args, cancellation);

                var (original_streams, original_duration) = await probe(path, cancellation);
                var (remux_streams, remux_duration) = await probe(temp, cancellation);
                if (remux_streams != original_streams - streams.Count
                    || Math.Abs(remux_duration - original_duration) > DurationTolerance)
                {
                    throw new InvalidOperationException(
                        $"remux incohérent : {remux_streams} pistes / {remux_duration:F1} s, "
                        + $"attendu {original_streams - streams.Count} / {original_duration:F1} s");
                }

                if (KeepBackup) File.Move(path, Path.Combine(folder, "." + name + ".ass-backup"), overwrite: true);
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        /// <summary>
        /// Nombre de pistes et durée d'un fichier, lus par ffprobe.
        ///
        /// Parametres :
        /// - path (string) : le fichier
        /// - cancellation (CancellationToken) : annulation de la tâche
        ///
        /// Output :
        /// - probe ((int, double)) : nombre de pistes, durée en secondes
        /// </summary>
        private async Task<(int Streams, double Duration)> probe(string path, CancellationToken cancellation)
        {
            // Une ligne par piste (« stream »), puis la durée du conteneur.
            var output = await run(
                _encoder.ProbePath,
                new[] { "-v", "error", "-show_entries", "stream=index:format=duration", "-of", "csv=p=0", path },
                cancellation);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return (lines.Length - 1, double.Parse(lines[^1], CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Lance un exécutable et attend sa fin.
        ///
        /// Parametres :
        /// - executable (string) : chemin de l'exécutable
        /// - args (IEnumerable&lt;string&gt;) : arguments, passés sans shell
        /// - cancellation (CancellationToken) : annulation de la tâche
        ///
        /// Output :
        /// - output (string) : la sortie standard
        /// </summary>
        private static async Task<string> run(string executable, IEnumerable<string> args, CancellationToken cancellation)
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellation);
            var error = process.StandardError.ReadToEndAsync(cancellation);
            await process.WaitForExitAsync(cancellation);

            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{Path.GetFileName(executable)} a échoué ({process.ExitCode}) : {await error}");

            return await output;
        }

        /// <summary>
        /// Vrai si l'on peut écrire dans le dossier (faux sur un montage en lecture seule).
        ///
        /// Parametres :
        /// - folder (string) : le dossier
        ///
        /// Output :
        /// - writable (bool) : écriture possible
        /// </summary>
        private static bool is_writable(string folder)
        {
            var probe_file = Path.Combine(folder, ".enhancedfin-write-test");
            try
            {
                File.WriteAllBytes(probe_file, Array.Empty<byte>());
                File.Delete(probe_file);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
