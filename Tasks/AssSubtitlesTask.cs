using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// 3. le mkv est remuxé **sans** les pistes converties (copie, aucun réencodage),
    ///    vérifié, puis substitué à l'original par un renommage atomique.
    ///
    /// **Désactivée par défaut** (`ConvertAssSubtitles`) : elle réécrit les fichiers de la
    /// bibliothèque. Idempotente : un mkv sans ASS n'est pas touché, un srt existant n'est
    /// pas réécrit. Sur un dossier en lecture seule, rien n'est écrit : la tâche journalise
    /// ce qu'elle aurait fait.
    /// </summary>
    public class AssSubtitlesTask : IScheduledTask, ILibraryPostScanTask
    {
        /// <summary>Écart de durée toléré entre l'original et le remux, en secondes.</summary>
        private const double DurationTolerance = 1.0;

        private static readonly string[] AssCodecs = { "ass", "ssa" };

        /// <summary>
        /// Une seule exécution à la fois : le passage après un scan et le bouton du tableau
        /// de bord écriraient sinon le même fichier temporaire.
        /// </summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static readonly Regex LanguageCode = new("^[a-z]{2,3}$", RegexOptions.Compiled);

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
            if (Plugin.Instance?.Configuration.ConvertAssSubtitles != true)
            {
                progress.Report(100);
                return;
            }

            if (!await Gate.WaitAsync(0, cancellation))
            {
                _logger.LogInformation("[EnhancedFin] Sous-titres ASS : une conversion est déjà en cours");
                return;
            }

            try
            {
                await convert_all(progress, cancellation);
            }
            finally
            {
                Gate.Release();
            }
        }

        private async Task convert_all(IProgress<double> progress, CancellationToken cancellation)
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
            var folder = Path.GetDirectoryName(video.Path)!;
            var writable = is_writable(folder);

            // ⚠️ Une piste n'est retirée du mkv que si **son** srt vient d'être écrit. Deux
            // pistes de même langue et même type (« Full » et « Signs »), ou un srt déjà
            // présent, laissent la piste ASS en place : son texte n'existe nulle part
            // ailleurs, la retirer le perdrait.
            var converted = new List<MediaStream>();
            var planned = new HashSet<string>();
            foreach (var stream in streams)
            {
                var srt_path = srt_path_for(video.Path, stream);
                if (!planned.Add(srt_path) || File.Exists(srt_path))
                {
                    _logger.LogWarning("[EnhancedFin] {Path} : piste {Index} gardée, {Srt} existe déjà", video.Path, stream.Index, srt_path);
                    continue;
                }

                if (!writable)
                {
                    _logger.LogInformation("[EnhancedFin] Lecture seule, aurait converti la piste {Index} en {Srt}", stream.Index, srt_path);
                    continue;
                }

                await File.WriteAllBytesAsync(srt_path, await extract_srt(video, stream, cancellation), cancellation);
                converted.Add(stream);
            }

            if (converted.Count == 0) return;

            await remove_streams(video.Path, converted, cancellation);

            _providers.QueueRefresh(
                video.Id,
                new MetadataRefreshOptions(new DirectoryService(_file_system)),
                RefreshPriority.High);

            _logger.LogInformation("[EnhancedFin] {Path} : {Count} pistes ASS converties en srt", video.Path, converted.Count);
        }

        /// <summary>
        /// Nom du srt externe d'une piste, selon la convention de Jellyfin :
        /// `Film.fr.srt`, `Film.fr.forced.srt`, `Film.en.sdh.srt`.
        ///
        /// Le forcé se lit aussi dans le titre : certaines pistes ne le disent que là
        /// (« FR Forced ASS »), sans le drapeau.
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
            // La langue vient des métadonnées du fichier : un `/` ou un `..` y écrirait le srt
            // hors du dossier du film. Seul un code de langue passe.
            var language = _localization.FindLanguageInfo(stream.Language ?? string.Empty)?.TwoLetterISOLanguageName
                           ?? stream.Language
                           ?? string.Empty;
            if (!LanguageCode.IsMatch(language)) language = "und";
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
        /// à l'original. Tant que la substitution n'a pas eu lieu, l'original est intact.
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
            var removed = streams.Select(s => s.Index).ToHashSet();

            // Les index viennent de la base de Jellyfin, qui peut être en retard sur le
            // fichier (remplacé depuis le dernier scan) : chaque piste retirée doit être,
            // **dans le fichier**, un sous-titre ASS. Sinon `-map -0:i` retirerait une
            // piste audio ou vidéo.
            var (original, original_duration) = await probe(path, cancellation);
            foreach (var index in removed)
            {
                if (index >= original.Count || original[index].Type != "subtitle" || !AssCodecs.Contains(original[index].Codec))
                    throw new InvalidOperationException($"piste {index} absente ou pas en ASS dans le fichier");
            }

            // `-map 0` garde tout (vidéo, audio, chapitres, pièces jointes), puis chaque
            // `-map -0:i` retire une piste ASS. `-c copy` : aucun réencodage.
            var args = new List<string> { "-nostdin", "-v", "error", "-y", "-i", path, "-map", "0" };
            foreach (var index in removed) args.AddRange(new[] { "-map", "-0:" + index.ToString(CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-c", "copy", "-f", "matroska", temp });

            try
            {
                await run(_encoder.EncoderPath, args, cancellation);

                // Le remux doit garder exactement les autres pistes, dans le même ordre.
                var expected = original.Where((_, index) => !removed.Contains(index)).ToList();
                var (remux, remux_duration) = await probe(temp, cancellation);
                if (!remux.SequenceEqual(expected) || Math.Abs(remux_duration - original_duration) > DurationTolerance)
                {
                    throw new InvalidOperationException(
                        $"remux incohérent : {remux.Count} pistes / {remux_duration:F1} s, "
                        + $"attendu {expected.Count} / {original_duration:F1} s");
                }

                // Substitution en un seul renommage : à aucun moment le film n'est absent
                // du dossier (le moniteur de Jellyfin le retirerait de la bibliothèque).
                if (Plugin.Instance?.Configuration.KeepAssBackup != false) File.Replace(temp, path, Path.Combine(folder, "." + name + ".ass-backup"));
                else File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        /// <summary>
        /// Pistes et durée d'un fichier, lues par ffprobe.
        ///
        /// Parametres :
        /// - path (string) : le fichier
        /// - cancellation (CancellationToken) : annulation de la tâche
        ///
        /// Output :
        /// - probe ((List, double)) : type et codec de chaque piste, par index ; durée en secondes
        /// </summary>
        private async Task<(List<(string Type, string Codec)> Streams, double Duration)> probe(string path, CancellationToken cancellation)
        {
            // Une ligne par piste (« index,codec_name,codec_type »), puis la durée du conteneur.
            var output = await run(
                _encoder.ProbePath,
                new[] { "-v", "error", "-show_entries", "stream=index,codec_name,codec_type:format=duration", "-of", "csv=p=0", path },
                cancellation);
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var streams = lines[..^1]
                .Select(line => line.Split(','))
                .Select(parts => (Type: parts[^1].ToLowerInvariant(), Codec: parts.Length > 2 ? parts[1].ToLowerInvariant() : string.Empty))
                .ToList();

            return (streams, double.Parse(lines[^1], CultureInfo.InvariantCulture));
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
            try
            {
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                // Sans ça, ffmpeg continue seul : il remplit le disque d'un fichier déjà
                // supprimé et occupe le processeur jusqu'au bout du film.
                process.Kill(entireProcessTree: true);
                throw;
            }

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
