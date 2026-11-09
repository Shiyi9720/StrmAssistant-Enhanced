using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class MediaInfoApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly IProviderManager _providerManager;

        private const string MediaInfoFileExtension = "-mediainfo.json";

        private readonly bool _fallbackApproach;
        private readonly MethodInfo _getStaticMediaSources;
        private readonly bool _getStaticMediaSourcesWithCancellationToken;
        private Dictionary<string, bool> _alwaysIgnoreFiles;

        internal class MediaSourceWithChapters
        {
            public MediaSourceInfo MediaSourceInfo { get; set; }
            public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
            public bool? ZeroFingerprintConfidence { get; set; }
            public string EmbeddedImage { get; set; }
            public string Album { get; set; }
            public string[] AlbumArtists { get; set; }
            public string[] Artists { get; set; }
        }

        public MediaInfoApi(ILibraryManager libraryManager, IFileSystem fileSystem, IProviderManager providerManager,
            IMediaSourceManager mediaSourceManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer,
            ILibraryMonitor libraryMonitor)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _providerManager = providerManager;
            _mediaSourceManager = mediaSourceManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;

            try
            {
                // Prefer the overload with CancellationToken, fall back to the one without it.
                _getStaticMediaSources = mediaSourceManager.GetType()
                                              .GetMethod("GetStaticMediaSources",
                                                  new[]
                                                  {
                                                      typeof(BaseItem), typeof(bool), typeof(bool), typeof(bool),
                                                      typeof(bool), typeof(BaseItem[]), typeof(LibraryOptions),
                                                      typeof(DeviceProfile), typeof(User), typeof(CancellationToken)
                                                  }) ??
                                          mediaSourceManager.GetType()
                                              .GetMethod("GetStaticMediaSources",
                                                  new[]
                                                  {
                                                      typeof(BaseItem), typeof(bool), typeof(bool), typeof(bool),
                                                      typeof(BaseItem[]), typeof(LibraryOptions),
                                                      typeof(DeviceProfile), typeof(User)
                                                  });
                _getStaticMediaSourcesWithCancellationToken =
                    _getStaticMediaSources?.GetParameters().Length == 10;
                _fallbackApproach = _getStaticMediaSources != null;
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            if (_getStaticMediaSources is null)
            {
                _logger?.Warn($"{nameof(MediaInfoApi)} Init Failed");
            }

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var libraryMonitorImpl =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.IO.LibraryMonitor");
                var alwaysIgnoreExtensions = libraryMonitorImpl?.GetField("_alwaysIgnoreExtensions",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (alwaysIgnoreExtensions?.GetValue(libraryMonitor) is string[] currentArray)
                {
                    var newArray = new string[currentArray.Length + 1];
                    Array.Copy(currentArray, newArray, currentArray.Length);
                    newArray[newArray.Length - 1] = ".json";
                    alwaysIgnoreExtensions.SetValue(libraryMonitor, newArray);
                }
                else
                {
                    // Emby 4.10 replaced the ignored extension list with an exact file name dictionary.
                    _alwaysIgnoreFiles = libraryMonitorImpl?
                        .GetField("AlwaysIgnoreFiles", BindingFlags.Static | BindingFlags.NonPublic)?
                        .GetValue(null) as Dictionary<string, bool>;
                }
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }

                _logger?.Warn($"{nameof(MediaInfoApi)} Init Failed");
            }
        }

        private List<MediaSourceInfo> GetStaticMediaSourcesByApi(BaseItem item, bool enableAlternateMediaSources,
            LibraryOptions libraryOptions)
        {
            return _mediaSourceManager.GetStaticMediaSources(item, enableAlternateMediaSources, false, false, false,
                null, libraryOptions, null, null, CancellationToken.None);
        }

        private List<MediaSourceInfo> GetStaticMediaSourcesByRef(BaseItem item, bool enableAlternateMediaSources,
            LibraryOptions libraryOptions)
        {
            var arguments = _getStaticMediaSourcesWithCancellationToken
                ? new object[]
                {
                    item, enableAlternateMediaSources, false, false, false, null, libraryOptions, null, null,
                    CancellationToken.None
                }
                : new object[]
                {
                    item, enableAlternateMediaSources, false, false, null, libraryOptions, null, null
                };

            return (List<MediaSourceInfo>)_getStaticMediaSources.Invoke(_mediaSourceManager, arguments);
        }

        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            var options = _libraryManager.GetLibraryOptions(item);
            var mediaSources = !_fallbackApproach
                ? GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, options)
                : GetStaticMediaSourcesByRef(item, enableAlternateMediaSources, options);

            if (mediaSources != null)
            {
                foreach (var mediaSource in mediaSources)
                {
                    mediaSource.Path = StrmUrlHelper.Normalize(mediaSource.Path);
                }
            }

            if (SubtitleBlacklist.IsConfigured && mediaSources != null)
            {
                foreach (var mediaSource in mediaSources)
                {
                    RemoveBlacklistedSubtitleStreams(mediaSource, "GetStaticMediaSources");
                }
            }

            return mediaSources;
        }

        public int RemoveBlacklistedSubtitleStreams(BaseItem item, string source)
        {
            if (!SubtitleBlacklist.IsConfigured || item is null) return 0;

            var mediaStreams = item.GetMediaStreams();

            if (mediaStreams is null || mediaStreams.Count == 0) return 0;

            var blacklist = SubtitleBlacklist.FromOptions();
            var removed = mediaStreams.Where(blacklist.IsBlacklisted).ToList();

            if (removed.Count == 0) return 0;

            var remaining = mediaStreams.Where(stream => !removed.Contains(stream)).ToList();

            _itemRepository.SaveMediaStreams(item.InternalId, remaining, CancellationToken.None);
            item.MediaStreams = remaining;

            foreach (var stream in removed)
            {
                LogRemovedSubtitleStream(stream, source);
            }

            return removed.Count;
        }

        private int RemoveBlacklistedSubtitleStreams(MediaSourceInfo mediaSource, string source)
        {
            if (!SubtitleBlacklist.IsConfigured || mediaSource?.MediaStreams is null) return 0;

            var blacklist = SubtitleBlacklist.FromOptions();
            var removed = new List<MediaStream>();

            mediaSource.MediaStreams.RemoveAll(stream =>
            {
                if (!blacklist.IsBlacklisted(stream)) return false;

                removed.Add(stream);

                return true;
            });

            foreach (var stream in removed)
            {
                LogRemovedSubtitleStream(stream, source);
            }

            return removed.Count;
        }

        private void LogRemovedSubtitleStream(MediaStream stream, string source)
        {
            _logger?.Info("SubtitleBlacklist - Removed subtitle stream (" + source + "): language=" +
                         (stream.Language ?? "EMPTY") + ", title=" + (stream.Title ?? "EMPTY") + ", codec=" +
                         (stream.Codec ?? "EMPTY") + ", external=" + stream.IsExternal);
        }

        private const string HdrVividTag = "HDR Vivid";

        /// <summary>
        /// Emby has no first class HDR Vivid (CUVA 2094-10) support, so the only reliable way to
        /// identify it is reading the dynamic metadata side data of the first video frame with
        /// ffprobe. Matching videos get an "HDR Vivid" tag so they can be filtered and searched.
        /// </summary>
        public async Task<bool> DetectHdrVividAsync(Video video, CancellationToken cancellationToken)
        {
            if (video is null) return false;

            var options = Plugin.Instance?.GetPluginOptions().MediaInfoExtractOptions;

            if (options?.DetectHdrVivid != true) return false;

            if (video.Tags != null &&
                video.Tags.Any(t => string.Equals(t, HdrVividTag, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var videoStream = video.GetMediaStreams().FirstOrDefault(s => s.Type == MediaStreamType.Video);

            // HDR Vivid always uses the PQ transfer function. Skip obvious SDR files, but still
            // probe when the transfer function is unknown.
            if (videoStream != null && !string.IsNullOrEmpty(videoStream.ColorTransfer) &&
                !string.Equals(videoStream.ColorTransfer, "smpte2084", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var inputPath = GetStaticMediaSources(video, false).FirstOrDefault()?.Path;

            if (string.IsNullOrEmpty(inputPath)) inputPath = video.Path;

            if (string.IsNullOrEmpty(inputPath)) return false;

            if (LibraryApi.IsFileShortcut(inputPath) && video.IsShortcut)
            {
                var mountedPath = await Plugin.LibraryApi.GetStrmMountPath(inputPath).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(mountedPath)) inputPath = mountedPath;
            }

            inputPath = StrmUrlHelper.Normalize(inputPath);

            if (string.IsNullOrEmpty(inputPath)) return false;

            var detected = await DetectHdrVividCoreAsync(inputPath, cancellationToken).ConfigureAwait(false);

            if (!detected) return false;

            _logger?.Info($"HdrVividDetect - HDR Vivid detected: {video.Name} - {video.Path}");

            video.SetTags((video.Tags ?? Array.Empty<string>())
                .Concat(new[] { HdrVividTag }).Distinct(StringComparer.OrdinalIgnoreCase));

            _libraryManager.UpdateItems(new List<BaseItem> { video }, null,
                ItemUpdateType.MetadataEdit, false, false, null, CancellationToken.None);

            return true;
        }

        internal static string BuildHdrVividProbeArguments(string inputPath)
        {
            inputPath = StrmUrlHelper.Normalize(inputPath);

            return "-hide_banner -loglevel error -select_streams v:0 -read_intervals %+#1 " +
                   "-show_entries frame=side_data_list -of default=noprint_wrappers=1 " +
                   $"\"{inputPath}\"";
        }

        internal static bool IsHdrVividProbeOutput(string output)
        {
            return !string.IsNullOrEmpty(output) &&
                   (output.IndexOf("CUVA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    output.IndexOf("HDR Vivid", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    output.IndexOf("2094-10", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private async Task<bool> DetectHdrVividCoreAsync(string inputPath, CancellationToken cancellationToken)
        {
            inputPath = StrmUrlHelper.Normalize(inputPath);

            // Optional distributed detection: the worker only answers whether the first video
            // frame carries HDR Vivid metadata. A null result means it could not handle the
            // request, so the local ffprobe below stays the fallback.
            if (Plugin.DistributedExtractApi != null &&
                DistributedExtractApi.IsHdrVividDistributionEnabled)
            {
                var distributed = await Plugin.DistributedExtractApi
                    .TryDetectHdrVividAsync(inputPath, cancellationToken).ConfigureAwait(false);

                if (distributed.HasValue) return distributed.Value;
            }

            var ffmpegManager = Plugin.Instance?.FfmpegManager;

            if (ffmpegManager?.FfmpegConfiguration?.ProbePath is null) return false;

            var output = await FfmpegApi.RunAndCaptureAsync(ffmpegManager,
                    ffmpegManager.FfmpegConfiguration.ProbePath,
                    BuildHdrVividProbeArguments(inputPath), "HdrVividDetect", 60000,
                    cancellationToken)
                .ConfigureAwait(false);

            return IsHdrVividProbeOutput(output);
        }

        public MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            var jsonRootFolder = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.MediaInfoJsonRootFolder;

            var relativePath = item.ContainingFolderPath;
            if (!string.IsNullOrEmpty(jsonRootFolder) && Path.IsPathRooted(item.ContainingFolderPath))
            {
                relativePath = Path.GetRelativePath(Path.GetPathRoot(item.ContainingFolderPath)!,
                    item.ContainingFolderPath);
            }

            var mediaInfoJsonPath = !string.IsNullOrEmpty(jsonRootFolder)
                ? Path.Combine(jsonRootFolder, relativePath, item.FileNameWithoutExtension + MediaInfoFileExtension)
                : Path.Combine(item.ContainingFolderPath!, item.FileNameWithoutExtension + MediaInfoFileExtension);

            return mediaInfoJsonPath;
        }

        private void IgnoreMediaInfoJsonFile(string mediaInfoJsonPath)
        {
            if (_alwaysIgnoreFiles is null || string.IsNullOrEmpty(mediaInfoJsonPath)) return;

            try
            {
                _alwaysIgnoreFiles[Path.GetFileName(mediaInfoJsonPath)] = true;
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }
        }

        private async Task<bool> SerializeMediaInfo(BaseItem item, IDirectoryService directoryService, bool overwrite,
            string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (overwrite || file?.Exists != true)
            {
                try
                {
                    var options = _libraryManager.GetLibraryOptions(item);
                    var mediaSources = item.GetMediaSources(false, false, options);
                    var chapters = _itemRepository.GetChapters(item);
                    var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                            new MediaSourceWithChapters
                                { MediaSourceInfo = mediaSource, Chapters = chapters })
                        .ToList();

                    foreach (var jsonItem in mediaSourcesWithChapters)
                    {
                        jsonItem.MediaSourceInfo.Id = null;
                        jsonItem.MediaSourceInfo.ItemId = null;
                        jsonItem.MediaSourceInfo.Path = null;

                        foreach (var subtitle in jsonItem.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = _fileSystem.GetFileInfo(subtitle.Path).Name;
                        }

                        foreach (var chapter in jsonItem.Chapters)
                        {
                            chapter.ImageTag = null;
                        }

                        if (item is Episode)
                        {
                            jsonItem.ZeroFingerprintConfidence =
                                !string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
                        }

                        if (item is Audio audio)
                        {
                            jsonItem.Album = audio.Album;
                            jsonItem.AlbumArtists = audio.AlbumArtists;
                            jsonItem.Artists = audio.Artists;

                            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
                            if (primaryImageInfo != null && _fileSystem.FileExists(primaryImageInfo.Path))
                            {
                                var imageBytes = await _fileSystem
                                    .ReadAllBytesAsync(primaryImageInfo.Path, CancellationToken.None)
                                    .ConfigureAwait(false);
                                var base64String = Convert.ToBase64String(imageBytes);
                                jsonItem.EmbeddedImage = base64String;
                            }
                        }
                    }

                    var parentDirectory = Path.GetDirectoryName(mediaInfoJsonPath);
                    if (!string.IsNullOrEmpty(parentDirectory))
                    {
                        Directory.CreateDirectory(parentDirectory);
                    }

                    IgnoreMediaInfoJsonFile(mediaInfoJsonPath);

                    _jsonSerializer.SerializeToFile(mediaSourcesWithChapters, mediaInfoJsonPath);

                    _logger?.Info("MediaInfoPersist - Serialization Success (" + source + "): " + mediaInfoJsonPath);

                    return true;
                }
                catch (Exception e)
                {
                    _logger?.Error("MediaInfoPersist - Serialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public async Task<bool> SerializeMediaInfo(long itemId, IDirectoryService directoryService, bool overwrite,
            string source)
        {
            var workItem = _libraryManager.GetItemById(itemId);

            if (MediaInfoBlacklist.FromOptions().IsExcluded(workItem))
            {
                _logger.Info("MediaInfoBlacklist - Serialization Skipped (" + source + "): " + workItem?.Name);
                return false;
            }

            if (!Plugin.LibraryApi.HasMediaInfo(workItem))
            {
                _logger?.Info("MediaInfoPersist - Serialization Skipped - No MediaInfo (" + source + ")");
                return false;
            }

            if (!Plugin.LibraryApi.IsLibraryInScope(workItem)) return false;

            var ds = directoryService ?? new DirectoryService(_logger, _fileSystem);

            return await SerializeMediaInfo(workItem, ds, overwrite, source).ConfigureAwait(false);
        }

        public async Task<bool> DeserializeMediaInfo(BaseItem item, IDirectoryService directoryService, string source,
            bool ignoreFileChange)
        {
            var workItem = _libraryManager.GetItemById(item.InternalId);

            if (MediaInfoBlacklist.FromOptions().IsExcluded(workItem))
            {
                _logger.Info("MediaInfoBlacklist - Deserialization Skipped (" + source + "): " + workItem?.Name);
                return false;
            }

            if (Plugin.LibraryApi.HasMediaInfo(workItem)) return true;

            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters =
                        (await _jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false)).ToArray()[0];

                    if (mediaSourceWithChapters?.MediaSourceInfo?.RunTimeTicks.HasValue is true &&
                        (ignoreFileChange || !Plugin.LibraryApi.HasFileChanged(item, directoryService)))
                    {
                        RemoveBlacklistedSubtitleStreams(mediaSourceWithChapters.MediaSourceInfo, "Deserialize");

                        foreach (var subtitle in mediaSourceWithChapters.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = Path.Combine(workItem.ContainingFolderPath,
                                _fileSystem.GetFileInfo(subtitle.Path).Name);
                        }

                        _itemRepository.SaveMediaStreams(item.InternalId,
                            mediaSourceWithChapters.MediaSourceInfo.MediaStreams, CancellationToken.None);

                        if (workItem is Audio audio)
                        {
                            if (!string.IsNullOrWhiteSpace(mediaSourceWithChapters.Album))
                            {
                                audio.Album = mediaSourceWithChapters.Album;
                            }

                            if (mediaSourceWithChapters.AlbumArtists?.Length > 0)
                            {
                                audio.AlbumArtists = mediaSourceWithChapters.AlbumArtists;
                            }

                            if (mediaSourceWithChapters.Artists?.Length > 0)
                            {
                                audio.Artists = mediaSourceWithChapters.Artists;
                            }

                            if (!string.IsNullOrEmpty(mediaSourceWithChapters.EmbeddedImage))
                            {
                                var imageBytes = Convert.FromBase64String(mediaSourceWithChapters.EmbeddedImage);
                                var tempPath = Path.Combine(Plugin.Instance.ApplicationPaths.TempDirectory,
                                    Guid.NewGuid() + ".jpg");
                                await _fileSystem.WriteAllBytesAsync(tempPath, imageBytes, CancellationToken.None)
                                    .ConfigureAwait(false);

                                var libraryOptions = _libraryManager.GetLibraryOptions(workItem);
                                await _providerManager.SaveImage(workItem, libraryOptions, tempPath, ImageType.Primary,
                                        null, Array.Empty<long>(), directoryService, false, CancellationToken.None)
                                    .ConfigureAwait(false);

                                try
                                {
                                    _fileSystem.DeleteFile(tempPath);
                                }
                                catch
                                {
                                    // ignored
                                }
                            }
                        }

                        workItem.Size = mediaSourceWithChapters.MediaSourceInfo.Size.GetValueOrDefault();
                        workItem.RunTimeTicks = mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks;
                        workItem.Container = mediaSourceWithChapters.MediaSourceInfo.Container;
                        workItem.TotalBitrate = mediaSourceWithChapters.MediaSourceInfo.Bitrate.GetValueOrDefault();

                        var videoStream = mediaSourceWithChapters.MediaSourceInfo.MediaStreams
                            .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                            .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                            .FirstOrDefault();

                        if (videoStream != null)
                        {
                            workItem.Width = videoStream.Width.GetValueOrDefault();
                            workItem.Height = videoStream.Height.GetValueOrDefault();
                        }

                        _libraryManager.UpdateItems(new List<BaseItem> { workItem }, null,
                            ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                        if (workItem is Video video)
                        {
                            await DeserializeChapterInfo(video, mediaSourceWithChapters.Chapters, directoryService,
                                source).ConfigureAwait(false);

                            if (video is Episode && mediaSourceWithChapters.ZeroFingerprintConfidence is true)
                            {
                                _itemRepository.LogIntroDetectionFailureFailure(video.InternalId,
                                    item.DateModified.ToUnixTimeSeconds());
                            }
                        }

                        _logger?.Info("MediaInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }

                    _logger?.Info("MediaInfoPersist - Deserialization Skipped (" + source + "): " + mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    _logger?.Error("MediaInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);

            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists is true)
            {
                try
                {
                    _logger?.Info($"MediaInfoPersist - Attempting to delete ({source}): {mediaInfoJsonPath}");
                    IgnoreMediaInfoJsonFile(mediaInfoJsonPath);
                    _fileSystem.DeleteFile(mediaInfoJsonPath);

                    var jsonRoot = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.MediaInfoJsonRootFolder;

                    if (!string.IsNullOrWhiteSpace(jsonRoot))
                    {
                        jsonRoot = _fileSystem.GetFullPath(jsonRoot).TrimEnd(Path.DirectorySeparatorChar);

                        var currentDir =
                            _fileSystem.GetFullPath(_fileSystem.GetDirectoryName(mediaInfoJsonPath) ?? string.Empty);

                        while (!string.IsNullOrEmpty(currentDir) &&
                               !string.Equals(currentDir, jsonRoot, StringComparison.OrdinalIgnoreCase) &&
                               CommonUtility.IsDirectoryEmpty(currentDir))
                        {
                            _logger?.Info(
                                $"MediaInfoPersist - Attempting to delete empty folder ({source}): {currentDir}");
                            _fileSystem.DeleteDirectory(currentDir, false);
                            currentDir = Path.GetDirectoryName(currentDir);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }
        }

        public async Task<bool> DeserializeIntroMarker(Episode item, IDirectoryService directoryService, string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters =
                        (await _jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false)).ToArray()[0];

                    if (mediaSourceWithChapters.ZeroFingerprintConfidence is true)
                    {
                        _itemRepository.LogIntroDetectionFailureFailure(item.InternalId,
                            item.DateModified.ToUnixTimeSeconds());

                        _logger?.Info("ChapterInfoPersist - Log Zero Fingerprint Confidence (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }

                    var introStart = mediaSourceWithChapters.Chapters
                        .FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);

                    var introEnd = mediaSourceWithChapters.Chapters
                        .FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

                    if (introStart != null && introEnd != null && introEnd.StartPositionTicks > introStart.StartPositionTicks)
                    {
                        var chapters = _itemRepository.GetChapters(item);
                        chapters.RemoveAll(c =>
                            c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);
                        chapters.Add(introStart);
                        chapters.Add(introEnd);
                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                        _itemRepository.SaveChapters(item.InternalId, chapters);

                        _logger?.Info("ChapterInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }
                }
                catch (Exception e)
                {
                    _logger?.Error("ChapterInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public async Task<bool> DeserializeChapterInfo(Video item, List<ChapterInfo> chapters,
            IDirectoryService directoryService, string source)
        {
            var thumbnailResult = false;

            if (Plugin.Instance.IsModSupported || !item.IsShortcut)
            {
                var localThumbnailSets =
                    Video.GetLocalThumbnailSetInfos(item.Path, item.Id, false, directoryService);

                if (localThumbnailSets.Length > 0)
                {
                    var options = _libraryManager.GetLibraryOptions(item);
                    var dummyLibraryOptions = new LibraryOptions
                    {
                        ThumbnailImagesIntervalSeconds = 10,
                        CacheImages = options.CacheImages
                    };

                    thumbnailResult = await Plugin.VideoThumbnailApi.RefreshThumbnailImages(item,
                        dummyLibraryOptions, directoryService, chapters, false, false,
                        CancellationToken.None);

                    if (thumbnailResult)
                    {
                        _logger?.Info("ChapterInfoPersist - Video Thumbnail Restore Success (" + source + "): " +
                                     localThumbnailSets[0].Path);
                    }
                }
            }

            _itemRepository.SaveChapters(item.InternalId, true, chapters);

            return thumbnailResult;
        }

        public void QueueRefreshAlternateVersions(BaseItem item, MetadataRefreshOptions options, bool force)
        {
            if (!(item is Video video)) return;

            var altIds = video.GetAlternateVersionIds();

            if (!altIds.Any()) return;

            var itemsToRefresh = force
                ? altIds
                : _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        ItemIds = altIds.ToArray(),
                        HasPath = true,
                        HasAudioStream = false,
                        MediaTypes = new[] { MediaType.Video }
                    })
                    .Select(i => i.InternalId);

            foreach (var altId in itemsToRefresh)
            {
                _providerManager.QueueRefresh(altId, options, RefreshPriority.Normal);
            }
        }

        public BaseItem GetItemByMediaSourceId(BaseItem item, string mediaSourceId)
        {
            if (string.IsNullOrEmpty(mediaSourceId)) return null;

            BaseItem targetItem = null;

            if (item.GetDefaultMediaSourceId() == mediaSourceId)
            {
                targetItem = item;
            }
            else
            {
                var mediaSource = item.GetMediaSources(true, false, null).FirstOrDefault(s => s.Id == mediaSourceId);

                if (mediaSource != null)
                {
                    targetItem = _libraryManager.GetItemById(mediaSource.ItemId);
                }
            }

            return targetItem;
        }
    }
}
