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

                // .strm items whose target is an http/smb disc image (or a file inside a remote
                // BDMV/VIDEO_TS structure) are probed through the plugin's own ffprobe.
                var remote = GetRemoteDiscSource(item);

                if (remote != null) return IsRemoteDisc(remote);

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

        /// <summary>
        /// True when the item resolves to an http/smb disc source. Used by the scan pipeline to
        /// fall back to the regular refresh instead of leaving a failed remote item without data.
        /// </summary>
        public bool IsRemoteSource(BaseItem item)
        {
            return GetRemoteDiscSource(item) != null;
        }

        private static string GetRemoteDiscSource(BaseItem item)
        {
            var path = item?.Path;

            if (string.IsNullOrEmpty(path)) return null;

            if (LibraryApi.IsFileShortcut(path))
            {
                path = ReadShortcutTarget(path);
            }

            return !string.IsNullOrEmpty(path) && CustomFfprobeApi.IsRemoteUrl(path) ? path : null;
        }

        /// <summary>
        /// Reads the first usable line of a .strm file, mirroring Emby's StrmMounter.
        /// </summary>
        private static string ReadShortcutTarget(string strmPath)
        {
            try
            {
                if (!File.Exists(strmPath)) return null;

                foreach (var line in File.ReadLines(strmPath))
                {
                    var value = line.Replace("\t", string.Empty).Replace("\r", string.Empty).Trim();

                    if (value.Length == 0 || value.StartsWith("#", StringComparison.Ordinal)) continue;

                    return value;
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger?.Debug("DiscMediaInfo - Shortcut read failed: " + e.Message);
            }

            return null;
        }

        private static string GetUrlPath(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return uri.AbsolutePath;
            }

            var end = url.IndexOfAny(new[] { '?', '#' });

            return end >= 0 ? url.Substring(0, end) : url;
        }

        private static bool IsRemoteDisc(string url)
        {
            if (!CustomFfprobeApi.IsRemoteUrl(url)) return false;

            if (IsoApi.IsIsoPath(url)) return true;

            var path = GetUrlPath(url);

            return path.IndexOf("/BDMV/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   path.IndexOf("/VIDEO_TS/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRemoteBdmvFile(string url)
        {
            var path = GetUrlPath(url);

            return path.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".mts", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".vob", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".bdmv", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<bool> ExtractAsync(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                var remote = GetRemoteDiscSource(item);
                string source;
                string discPath;
                MediaInfo mediaInfo;

                if (remote != null)
                {
                    (source, discPath, mediaInfo) =
                        await ResolveRemoteProbeAsync(item, remote, cancellationToken).ConfigureAwait(false);

                    if (mediaInfo is null)
                    {
                        _logger?.Info("DiscMediaInfo - Remote probe failed: " + remote);
                        return false;
                    }
                }
                else
                {
                    source = await ResolveProbeSourceAsync(item, cancellationToken).ConfigureAwait(false);

                    if (string.IsNullOrEmpty(source))
                    {
                        _logger?.Info("DiscMediaInfo - No playable video found: " + item.Path);
                        return false;
                    }

                    discPath = item.Path;
                    mediaInfo = await ProbeWithEmbyAsync(item, source, MediaProtocol.File,
                        cancellationToken).ConfigureAwait(false);
                }

                return await ApplyMediaInfoAsync(item, mediaInfo, source, discPath, cancellationToken)
                    .ConfigureAwait(false);
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

        private async Task<(string Source, string DiscPath, MediaInfo MediaInfo)> ResolveRemoteProbeAsync(
            BaseItem item, string url, CancellationToken cancellationToken)
        {
            if (IsoApi.IsIsoPath(url))
            {
                // A static ffprobe can open the image through its bluray protocol, including the
                // upstream http bridge. This gives streams and chapters in one step.
                var probed = await CustomFfprobeApi.ProbeAsync("bluray:" + url, false,
                    MediaProtocol.Http, cancellationToken).ConfigureAwait(false);

                if (HasVideo(probed)) return (url, url, probed);

                // Fallback that needs no custom binary: pull the main title out of the image
                // with http range requests, then let Emby probe the extracted file.
                if (!CustomFfprobeApi.IsHttpUrl(url)) return (null, null, null);

                var timeoutSeconds = Math.Max(10, Options.DiscMediaInfoExtractionTimeoutSeconds);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                var extractTask = IsoApi.ExtractAsync(url, null, cancellationToken);
                var completed = await Task.WhenAny(extractTask,
                        Task.Delay(Timeout.Infinite, timeoutCts.Token))
                    .ConfigureAwait(false);

                if (completed != extractTask)
                {
                    _logger?.Info("DiscMediaInfo - Remote ISO extraction still running in background: " + url);
                    return (null, null, null);
                }

                timeoutCts.Cancel();

                var extracted = await extractTask.ConfigureAwait(false);

                if (string.IsNullOrEmpty(extracted)) return (null, null, null);

                var mediaInfo = await ProbeWithEmbyAsync(item, extracted, MediaProtocol.File,
                    cancellationToken).ConfigureAwait(false);

                return (extracted, url, mediaInfo);
            }

            if (IsRemoteBdmvFile(url))
            {
                var probed = await CustomFfprobeApi.ProbeAsync(url, false, MediaProtocol.Http,
                    cancellationToken).ConfigureAwait(false);

                if (HasVideo(probed)) return (url, url, probed);

                var mediaInfo = await ProbeWithEmbyAsync(item, url, MediaProtocol.Http,
                    cancellationToken).ConfigureAwait(false);

                return (url, url, mediaInfo);
            }

            return (null, null, null);
        }

        private static bool HasVideo(MediaInfo mediaInfo)
        {
            return mediaInfo?.MediaStreams?.Any(s => s.Type == MediaStreamType.Video) is true;
        }

        private Task<MediaInfo> ProbeWithEmbyAsync(BaseItem item, string path, MediaProtocol protocol,
            CancellationToken cancellationToken)
        {
            var mediaSource = new MediaSourceInfo
            {
                Path = path,
                Protocol = protocol,
                Container = Path.GetExtension(GetUrlPath(path)).TrimStart('.'),
                Name = item.Name
            };

            return _mediaProbeManager.GetMediaInfo(new MediaInfoRequest
            {
                MediaSource = mediaSource,
                ExtractChapters = true,
                MediaType = DlnaProfileType.Video
            }, cancellationToken);
        }

        private async Task<bool> ApplyMediaInfoAsync(BaseItem item, MediaInfo mediaInfo, string source,
            string discPath, CancellationToken cancellationToken)
        {
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

            // Must run before SaveMediaStreams so the Dolby Vision flags are persisted with the
            // streams. A failure only means the item keeps Emby's unmarked result.
            await MarkDvP7Async(item, streams, source, cancellationToken).ConfigureAwait(false);

            _itemRepository.SaveMediaStreams(item.InternalId, streams, cancellationToken);
            item.MediaStreams = streams;

            item.RunTimeTicks = mediaInfo.RunTimeTicks ?? item.RunTimeTicks;
            item.Size = GetMediaSize(item, source, discPath, mediaInfo.Size);

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
                item.Container = ResolveContainer(discPath);
            }

            var chapters = mediaInfo.Chapters?.ToList() ?? new List<ChapterInfo>();

            if (chapters.Count == 0)
            {
                // Blu-ray chapters live in the playlist and DVD chapters in the IFO; neither
                // container exposes them to ffprobe.
                chapters = ReadDiscChapters(item, source, discPath);
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

        /// <summary>
        /// Detects a Dolby Vision Profile 7 dual-layer title and marks its base layer. Emby has
        /// no layer field, so the base layer is tagged as Dolby Vision profile 7 (DoviProfile76)
        /// exactly like a single-track P7 file; the enhancement layer stays a normal second
        /// video stream. The detection only runs for HEVC discs and is silently skipped when the
        /// static ffprobe is unavailable or the probe fails.
        /// </summary>
        private async Task MarkDvP7Async(BaseItem item, List<MediaStream> streams, string source,
            CancellationToken cancellationToken)
        {
            if (!HasHevcVideo(streams) || string.IsNullOrEmpty(source)) return;

            DvP7Info dvP7;

            try
            {
                // A retail image is probed through libbluray; an extracted/local title is read
                // directly. Both carry the PMT that holds the Dolby Vision stream group.
                var target = IsoApi.IsIsoPath(source) ? "bluray:" + source : source;

                dvP7 = await DvP7Api.DetectAsync(target, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Debug("DiscMediaInfo - DV P7 detection failed: " + e.Message);

                return;
            }

            if (dvP7?.IsDvP7 != true) return;

            var baseLayer = dvP7.BaseLayerIndex.HasValue
                ? streams.FirstOrDefault(s =>
                    s.Type == MediaStreamType.Video && s.Index == dvP7.BaseLayerIndex.Value)
                : null;

            baseLayer ??= streams
                .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                .FirstOrDefault();

            if (baseLayer is null) return;

            baseLayer.ExtendedVideoType = ExtendedVideoTypes.DolbyVision;
            baseLayer.ExtendedVideoSubType = ExtendedVideoSubTypes.DoviProfile76;

            _logger?.Info("DiscMediaInfo - Dolby Vision Profile 7 dual-layer (" + dvP7.Evidence +
                          "): BL " + DescribeLayer(dvP7.BaseLayerId, dvP7.BaseLayerIndex) +
                          ", EL " + DescribeLayer(dvP7.EnhancementLayerId, dvP7.EnhancementLayerIndex) +
                          " - " + item.Path);
        }

        private static bool HasHevcVideo(IEnumerable<MediaStream> streams)
        {
            return streams != null && streams.Any(s => s.Type == MediaStreamType.Video &&
                (string.Equals(s.Codec, "hevc", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.Codec, "h265", StringComparison.OrdinalIgnoreCase)));
        }

        private static string DescribeLayer(string id, int? index)
        {
            if (!string.IsNullOrEmpty(id)) return id;

            return index.HasValue ? "index " + index.Value : "unknown";
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

        /// <summary>
        /// Folder that holds BDMV/VIDEO_TS for a local path or an extracted file. Remote paths
        /// return null because their structure is read through the image, not the file system.
        /// </summary>
        private static string GetDiscRootFromPath(string path)
        {
            if (string.IsNullOrEmpty(path) || CustomFfprobeApi.IsRemoteUrl(path)) return null;

            if (Directory.Exists(path)) return path;

            var parent = Path.GetDirectoryName(path);

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

        private static string ResolveContainer(string discPath)
        {
            if (IsoApi.IsIsoPath(discPath)) return MediaContainers.Iso.GetFirstAliasOrName();

            var urlPath = GetUrlPath(discPath);

            if (urlPath.EndsWith(".vob", StringComparison.OrdinalIgnoreCase) ||
                urlPath.IndexOf("/VIDEO_TS/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return MediaContainers.Dvd.GetFirstAliasOrName();
            }

            if (urlPath.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) ||
                urlPath.EndsWith(".mts", StringComparison.OrdinalIgnoreCase) ||
                urlPath.EndsWith(".bdmv", StringComparison.OrdinalIgnoreCase) ||
                urlPath.IndexOf("/BDMV/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return MediaContainers.Bluray.GetFirstAliasOrName();
            }

            var root = GetDiscRootFromPath(discPath);

            if (root != null && Directory.Exists(Path.Combine(root, "BDMV")))
            {
                return MediaContainers.Bluray.GetFirstAliasOrName();
            }

            return MediaContainers.Dvd.GetFirstAliasOrName();
        }

        private static long GetMediaSize(BaseItem item, string source, string discPath, long? probedSize)
        {
            try
            {
                if (IsoApi.IsIsoPath(discPath))
                {
                    var length = IsoApi.GetSourceLength(discPath);

                    if (length > 0) return length;
                }

                var info = new FileInfo(source);

                if (info.Exists)
                {
                    return info.Length;
                }
            }
            catch
            {
                // ignored
            }

            return probedSize ?? item.Size;
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

        private List<ChapterInfo> ReadDiscChapters(BaseItem item, string source, string discPath)
        {
            // The extracted file keeps the disc type: DVD titles are extracted as VOB, Blu-ray
            // titles as m2ts, for ISO images and folders alike.
            return source.EndsWith(".vob", StringComparison.OrdinalIgnoreCase)
                ? ReadDvdChapters(item, source, discPath)
                : ReadBluRayChapters(item, discPath);
        }

        private List<ChapterInfo> ReadDvdChapters(BaseItem item, string source, string discPath)
        {
            try
            {
                var ifos = ReadDvdIfos(discPath);

                if (ifos.Count == 0) return new List<ChapterInfo>();

                int? vtsNumber = null;

                // The extracted file name identifies the VTS for folder based discs. ISO images
                // keep their own file name, so the longest title across all IFOs is used there.
                if (DvdIfoApi.TryGetVtsNumber(Path.GetFileName(GetUrlPath(source)), out var number))
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

        private static Dictionary<string, byte[]> ReadDvdIfos(string discPath)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            if (IsoApi.IsIsoPath(discPath)) return IsoApi.ReadDvdIfos(discPath);

            var root = GetDiscRootFromPath(discPath);

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

        private List<ChapterInfo> ReadBluRayChapters(BaseItem item, string discPath)
        {
            try
            {
                if (IsoApi.IsIsoPath(discPath))
                {
                    return BdPlaylistApi.SelectChapters(IsoApi.ReadBdPlaylists(discPath));
                }

                var root = GetDiscRootFromPath(discPath);

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
