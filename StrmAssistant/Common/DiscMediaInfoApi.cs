using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Extracts complete media information (video/audio/subtitle streams and chapters) for disc
    /// containers. Emby can only probe the container path, so an ISO image or a BDMV/VIDEO_TS
    /// folder never gets any streams. The main video is first resolved to a probeable file
    /// (ISO images go through <see cref="IsoApi"/>), then Emby's own probe manager is used so the
    /// result passes through the same normalization as ordinary files.
    /// </summary>
    public class DiscMediaInfoApi
    {
        private const int MaxPlaylistCount = 200;
        private const long MaxPlaylistSize = 8 * 1024 * 1024;
        private const int MaxIfoCount = 32;
        private const long MaxIfoSize = 4 * 1024 * 1024;

        private readonly ILogger _logger;
        private readonly IMediaProbeManager _mediaProbeManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;

        public DiscMediaInfoApi(IMediaProbeManager mediaProbeManager, ILibraryManager libraryManager,
            IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _mediaProbeManager = mediaProbeManager;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
        }

        private static MediaInfoExtractOptions Options =>
            Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;

        /// <summary>
        /// True when the item is an ISO image or a Blu-ray/DVD folder that this component can probe.
        /// Kept cheap: no directory access for ordinary video files.
        /// </summary>
        public bool IsSupported(BaseItem item)
        {
            try
            {
                if (item is null || !Options.EnableDiscMediaInfoExtraction) return false;

                // Shortcuts (.strm) point at something we cannot read as a disc.
                if (item.IsShortcut || !item.IsFileProtocol) return false;

                var path = item.Path;

                if (string.IsNullOrEmpty(path)) return false;

                var container = item.MediaContainer;
                var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

                if (container == MediaContainers.Iso || container == MediaContainers.BlurayIso ||
                    container == MediaContainers.DvdIso ||
                    extension == "iso" || extension == "img")
                {
                    return Options.EnableIsoExtraction;
                }

                if (container == MediaContainers.Bluray || container == MediaContainers.Dvd ||
                    extension == "disc")
                {
                    return GetDiscRoot(item) != null && HasDiscStructure(item);
                }
            }
            catch (Exception e)
            {
                _logger?.Debug("DiscMediaInfo - Support check failed: " + e.Message);
            }

            return false;
        }

        public async Task<bool> ExtractAsync(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                var source = await ResolveProbeSourceAsync(item, cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrEmpty(source))
                {
                    _logger?.Info("DiscMediaInfo - No playable video found: " + item.Path);
                    return false;
                }

                var mediaSource = new MediaSourceInfo
                {
                    Path = source,
                    Protocol = MediaProtocol.File,
                    Container = Path.GetExtension(source).TrimStart('.'),
                    Name = item.Name
                };

                var mediaInfo = await _mediaProbeManager.GetMediaInfo(new MediaInfoRequest
                {
                    MediaSource = mediaSource,
                    ExtractChapters = true,
                    MediaType = DlnaProfileType.Video
                }, cancellationToken).ConfigureAwait(false);

                var streams = mediaInfo?.MediaStreams?
                    .Where(s => s.Type == MediaStreamType.Video || s.Type == MediaStreamType.Audio ||
                                s.Type == MediaStreamType.Subtitle)
                    .ToList();

                if (streams is null || streams.Count == 0 ||
                    streams.All(s => s.Type != MediaStreamType.Video))
                {
                    _logger?.Info("DiscMediaInfo - No video stream probed: " + item.Path);
                    return false;
                }

                _itemRepository.SaveMediaStreams(item.InternalId, streams, cancellationToken);
                item.MediaStreams = streams;

                item.RunTimeTicks = mediaInfo.RunTimeTicks ?? item.RunTimeTicks;
                item.Size = GetMediaSize(item, source);

                if (mediaInfo.Bitrate.HasValue && mediaInfo.Bitrate.Value > 0)
                {
                    item.TotalBitrate = mediaInfo.Bitrate.Value;
                }

                var videoStream = streams
                    .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                    .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                    .FirstOrDefault();

                if (videoStream != null)
                {
                    item.Width = videoStream.Width.GetValueOrDefault();
                    item.Height = videoStream.Height.GetValueOrDefault();
                }

                if (string.IsNullOrEmpty(item.Container))
                {
                    item.Container = ResolveContainer(item);
                }

                var chapters = mediaInfo.Chapters?.ToList() ?? new List<ChapterInfo>();

                if (chapters.Count == 0)
                {
                    // Blu-ray chapters live in the playlist and DVD chapters in the IFO; neither
                    // container exposes them to ffprobe.
                    chapters = ReadDiscChapters(item, source);
                }

                SaveChapters(item, chapters);

                _libraryManager.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.MetadataImport,
                    false, false, null, cancellationToken);

                _logger?.Info("DiscMediaInfo - Extracted " + streams.Count + " streams (" +
                              streams.Count(s => s.Type == MediaStreamType.Video) + " video, " +
                              streams.Count(s => s.Type == MediaStreamType.Audio) + " audio, " +
                              streams.Count(s => s.Type == MediaStreamType.Subtitle) + " subtitle), " +
                              chapters.Count + " chapters: " + item.Path);

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                _logger?.Info("DiscMediaInfo - Failed: " + item.Path + " - " + e.Message);
                _logger?.Debug(e.StackTrace);
                return false;
            }
        }

        private async Task<string> ResolveProbeSourceAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var path = item.Path;

            if (IsIsoItem(item))
            {
                var timeoutSeconds = Math.Max(10, Options.DiscMediaInfoExtractionTimeoutSeconds);

                // The extraction job is cached and keeps running in the background when it does
                // not finish in time, so a later scan picks up the finished file.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                var extractTask = IsoApi.ExtractAsync(path, null, cancellationToken);
                var completed = await Task.WhenAny(extractTask,
                        Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completed != extractTask)
                {
                    _logger?.Info("DiscMediaInfo - ISO extraction still running in background: " + path);
                    return null;
                }

                timeoutCts.Cancel();

                return await extractTask.ConfigureAwait(false);
            }

            var root = GetDiscRoot(item);

            return root is null ? null : FindMainVideo(root);
        }

        private static string GetDiscRoot(BaseItem item)
        {
            var path = item.Path;

            if (string.IsNullOrEmpty(path)) return null;

            if (Directory.Exists(path)) return path;

            var parent = item.ContainingFolderPath;

            return !string.IsNullOrEmpty(parent) && Directory.Exists(parent) ? parent : null;
        }

        private static bool HasDiscStructure(BaseItem item)
        {
            var root = GetDiscRoot(item);

            return root != null &&
                   (Directory.Exists(Path.Combine(root, "BDMV", "STREAM")) ||
                    Directory.Exists(Path.Combine(root, "VIDEO_TS")));
        }

        private static bool IsIsoItem(BaseItem item)
        {
            var container = item.MediaContainer;

            if (container == MediaContainers.Iso || container == MediaContainers.BlurayIso ||
                container == MediaContainers.DvdIso) return true;

            var path = item.Path;

            if (string.IsNullOrEmpty(path)) return false;

            var extension = Path.GetExtension(path);

            return extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".img", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveContainer(BaseItem item)
        {
            if (IsIsoItem(item)) return MediaContainers.Iso.GetFirstAliasOrName();

            var root = GetDiscRoot(item);

            if (root != null && Directory.Exists(Path.Combine(root, "BDMV")))
            {
                return MediaContainers.Bluray.GetFirstAliasOrName();
            }

            return MediaContainers.Dvd.GetFirstAliasOrName();
        }

        private static long GetMediaSize(BaseItem item, string source)
        {
            try
            {
                if (IsIsoItem(item))
                {
                    var length = new FileInfo(item.Path).Length;

                    if (length > 0) return length;
                }

                return new FileInfo(source).Length;
            }
            catch
            {
                return item.Size;
            }
        }

        private static string FindMainVideo(string root)
        {
            try
            {
                var streamFolder = Path.Combine(root, "BDMV", "STREAM");

                if (Directory.Exists(streamFolder))
                {
                    // Prefer the clip referenced by the longest playlist, which is the main
                    // feature far more reliably than "largest file".
                    var clip = BdPlaylistApi.GetMainClip(streamFolder, Path.Combine(root, "BDMV", "PLAYLIST"));

                    return clip ?? GetLargestFile(streamFolder, ".m2ts", ".mts");
                }

                var videoTsFolder = Path.Combine(root, "VIDEO_TS");

                if (Directory.Exists(videoTsFolder))
                {
                    return GetLargestFile(videoTsFolder, ".vob");
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("DiscMediaInfo - Find main video failed: " + e.Message);
            }

            return null;
        }

        private static string GetLargestFile(string folder, params string[] extensions)
        {
            return Directory.EnumerateFiles(folder)
                .Where(f => extensions.Any(e =>
                    f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.Length)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }

        private List<ChapterInfo> ReadDiscChapters(BaseItem item, string source)
        {
            // The extracted file keeps the disc type: DVD titles are extracted as VOB, Blu-ray
            // titles as m2ts, for ISO images and folders alike.
            return source.EndsWith(".vob", StringComparison.OrdinalIgnoreCase)
                ? ReadDvdChapters(item, source)
                : ReadBluRayChapters(item);
        }

        private List<ChapterInfo> ReadDvdChapters(BaseItem item, string source)
        {
            try
            {
                var ifos = ReadDvdIfos(item);

                if (ifos.Count == 0) return new List<ChapterInfo>();

                int? vtsNumber = null;

                // The extracted file name identifies the VTS for folder based discs. ISO images
                // keep their own file name, so the longest title across all IFOs is used there.
                if (DvdIfoApi.TryGetVtsNumber(Path.GetFileName(source), out var number))
                {
                    vtsNumber = number;
                }

                var chapters = DvdIfoApi.SelectChapters(ifos, vtsNumber, item.RunTimeTicks ?? 0);

                if (chapters.Count > 0)
                {
                    _logger?.Debug("DiscMediaInfo - DVD IFO chapters: " + chapters.Count + " - " + item.Path);
                }

                return chapters;
            }
            catch (Exception e)
            {
                _logger?.Debug("DiscMediaInfo - DVD chapter extraction failed: " + e.Message);

                return new List<ChapterInfo>();
            }
        }

        private static Dictionary<string, byte[]> ReadDvdIfos(BaseItem item)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            if (IsIsoItem(item)) return IsoApi.ReadDvdIfos(item.Path);

            var root = GetDiscRoot(item);

            if (root is null) return result;

            var folder = Path.Combine(root, "VIDEO_TS");

            if (!Directory.Exists(folder)) return result;

            foreach (var file in Directory.EnumerateFiles(folder)
                         .Where(f => f.EndsWith(".ifo", StringComparison.OrdinalIgnoreCase))
                         .Take(MaxIfoCount))
            {
                try
                {
                    var info = new FileInfo(file);

                    if (!info.Exists || info.Length <= 0 || info.Length > MaxIfoSize) continue;

                    result[info.Name] = File.ReadAllBytes(file);
                }
                catch
                {
                    // ignored: keep the IFOs that can be read
                }
            }

            return result;
        }

        private List<ChapterInfo> ReadBluRayChapters(BaseItem item)
        {
            try
            {
                if (IsIsoItem(item))
                {
                    return BdPlaylistApi.SelectChapters(IsoApi.ReadBdPlaylists(item.Path));
                }

                var root = GetDiscRoot(item);

                if (root is null) return new List<ChapterInfo>();

                var playlistFolder = Path.Combine(root, "BDMV", "PLAYLIST");

                if (!Directory.Exists(playlistFolder)) return new List<ChapterInfo>();

                var playlists = new List<byte[]>();

                foreach (var file in Directory.EnumerateFiles(playlistFolder, "*.mpls")
                             .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                             .Take(MaxPlaylistCount))
                {
                    var info = new FileInfo(file);

                    if (!info.Exists || info.Length <= 0 || info.Length > MaxPlaylistSize) continue;

                    playlists.Add(File.ReadAllBytes(file));
                }

                return BdPlaylistApi.SelectChapters(playlists);
            }
            catch (Exception e)
            {
                _logger?.Debug("DiscMediaInfo - Chapter extraction failed: " + e.Message);

                return new List<ChapterInfo>();
            }
        }

        private void SaveChapters(BaseItem item, List<ChapterInfo> chapters)
        {
            if (chapters is null || chapters.Count == 0) return;

            // Intro/credits markers written by the intro skip feature must survive a re-probe.
            var markers = _itemRepository.GetChapters(item)?
                .Where(c => c.MarkerType != MarkerType.Chapter)
                .ToList() ?? new List<ChapterInfo>();

            var merged = markers.Concat(chapters)
                .GroupBy(c => c.StartPositionTicks)
                .Select(g => g.First())
                .OrderBy(c => c.StartPositionTicks)
                .ToList();

            for (var i = 0; i < merged.Count; i++)
            {
                merged[i].ChapterIndex = i;
                merged[i].ImageTag = null;
            }

            _itemRepository.SaveChapters(item.InternalId, true, merged);
        }
    }
}
