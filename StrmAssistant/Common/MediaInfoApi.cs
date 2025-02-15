using MediaBrowser.Controller.Entities;
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
        
        private const string MediaInfoFileExtension = "-mediainfo.json";

        private readonly bool _fallbackProbeApproach;
        private readonly MethodInfo _getPlaybackMediaSources;
        private readonly MethodInfo _getStaticMediaSources;
        
        internal class MediaSourceWithChapters
        {
            public MediaSourceInfo MediaSourceInfo { get; set; }
            public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
            public bool? ZeroFingerprintConfidence { get; set; }
        }

        public MediaInfoApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IMediaSourceManager mediaSourceManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer,
            ILibraryMonitor libraryMonitor)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _mediaSourceManager = mediaSourceManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;

            if (Plugin.Instance.ApplicationHost.ApplicationVersion >= new Version("4.9.0.25"))
            {
                try
                {
                    _getPlaybackMediaSources = mediaSourceManager.GetType()
                        .GetMethod("GetPlayackMediaSources",
                            new[]
                            {
                                typeof(BaseItem), typeof(User), typeof(bool), typeof(string), typeof(bool),
                                typeof(bool), typeof(DeviceProfile), typeof(CancellationToken)
                            });
                    _getStaticMediaSources = mediaSourceManager.GetType()
                        .GetMethod("GetStaticMediaSources",
                            new[]
                            {
                                typeof(BaseItem), typeof(bool), typeof(bool), typeof(bool), typeof(bool),
                                typeof(LibraryOptions), typeof(DeviceProfile), typeof(User)
                            });
                    _fallbackProbeApproach = true;
                }
                catch (Exception e)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }

                if (_getPlaybackMediaSources is null || _getStaticMediaSources is null)
                {
                    _logger.Warn($"{nameof(MediaInfoApi)} Init Failed");
                }
            }

            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var libraryMonitorImpl =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.IO.LibraryMonitor");
                var alwaysIgnoreExtensions = libraryMonitorImpl.GetField("_alwaysIgnoreExtensions",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var currentArray = (string[])alwaysIgnoreExtensions.GetValue(libraryMonitor);
                var newArray = new string[currentArray.Length + 1];
                Array.Copy(currentArray, newArray, currentArray.Length);
                newArray[newArray.Length - 1] = ".json";
                alwaysIgnoreExtensions.SetValue(libraryMonitor, newArray);
            }
            catch (Exception e)
            {
                _logger.Debug(e.Message);
                _logger.Debug(e.StackTrace);
                _logger.Warn($"{nameof(MediaInfoApi)} Init Failed");
            }
        }

        private async Task<List<MediaSourceInfo>> GetPlaybackMediaSourcesByApi(BaseItem item,
            CancellationToken cancellationToken)
        {
            return await _mediaSourceManager
                .GetPlayackMediaSources(item, null, true, item.GetDefaultMediaSourceId(), false, null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<List<MediaSourceInfo>> GetPlaybackMediaSourcesByReflection(BaseItem item,
            CancellationToken cancellationToken)
        {
            //Method Signature:
            //Task<List<MediaSourceInfo>> GetPlayackMediaSources(BaseItem item, User user, bool allowMediaProbe,
            //    string probeMediaSourceId, bool enablePathSubstitution, bool fillChapters, DeviceProfile deviceProfile,
            //    CancellationToken cancellationToken);
            return await ((Task<List<MediaSourceInfo>>)_getPlaybackMediaSources.Invoke(_mediaSourceManager,
                new object[]
                {
                    item, null, true, item.GetDefaultMediaSourceId(), true, false, null, cancellationToken
                })).ConfigureAwait(false);
        }

        public async Task<List<MediaSourceInfo>> GetPlaybackMediaSources(BaseItem item,
            CancellationToken cancellationToken)
        {
            return await (!_fallbackProbeApproach
                ? GetPlaybackMediaSourcesByApi(item, cancellationToken)
                : GetPlaybackMediaSourcesByReflection(item, cancellationToken)).ConfigureAwait(false);
        }

        private List<MediaSourceInfo> GetStaticMediaSourcesByApi(BaseItem item, bool enableAlternateMediaSources)
        {
            return _mediaSourceManager.GetStaticMediaSources(item, enableAlternateMediaSources, false,
                _libraryManager.GetLibraryOptions(item), null, null);
        }

        private List<MediaSourceInfo> GetStaticMediaSourcesByReflection(BaseItem item, bool enableAlternateMediaSources)
        {
            //Method Signature:
            //Task<List<MediaSourceInfo>> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources,
            //    bool enablePathSubstitution, bool fillMediaStreams, bool fillChapters, LibraryOptions libraryOptions,
            //    DeviceProfile deviceProfile, User user = null)
            return (List<MediaSourceInfo>)_getStaticMediaSources.Invoke(_mediaSourceManager,
                new object[]
                {
                    item, enableAlternateMediaSources, false, false, false, _libraryManager.GetLibraryOptions(item),
                    null, null
                });
        }

        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            return !_fallbackProbeApproach
                ? GetStaticMediaSourcesByApi(item, enableAlternateMediaSources)
                : GetStaticMediaSourcesByReflection(item, enableAlternateMediaSources);
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

        public async Task<bool> SerializeMediaInfo(BaseItem item, IDirectoryService directoryService, bool overwrite,
            string source, CancellationToken cancellationToken)
        {
            if (!Plugin.LibraryApi.IsLibraryInScope(item)) return false;

            var workItem = _libraryManager.GetItemById(item.InternalId);

            if (!Plugin.LibraryApi.HasMediaInfo(workItem))
            {
                _logger.Info("MediaInfoPersist - Serialization Skipped - No MediaInfo (" + source + ")");
                return false;
            }

            var mediaInfoJsonPath = GetMediaInfoJsonPath(workItem);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (overwrite || file?.Exists != true || Plugin.LibraryApi.HasFileChanged(workItem, directoryService))
            {
                try
                {
                    await Task.Run(() =>
                        {
                            var options = _libraryManager.GetLibraryOptions(workItem);
                            var mediaSources = workItem.GetMediaSources(false, false, options);
                            var chapters = BaseItem.ItemRepository.GetChapters(workItem);
                            var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                                    new MediaSourceWithChapters
                                        { MediaSourceInfo = mediaSource, Chapters = chapters })
                                .ToList();

                            foreach (var jsonItem in mediaSourcesWithChapters)
                            {
                                jsonItem.MediaSourceInfo.Id = null;
                                jsonItem.MediaSourceInfo.ItemId = null;
                                jsonItem.MediaSourceInfo.Path = null;

                                if (workItem is Episode)
                                {
                                    jsonItem.ZeroFingerprintConfidence =
                                        !string.IsNullOrEmpty(
                                            BaseItem.ItemRepository.GetIntroDetectionFailureResult(
                                                workItem.InternalId));
                                }
                            }

                            var parentDirectory = Path.GetDirectoryName(mediaInfoJsonPath);
                            if (!string.IsNullOrEmpty(parentDirectory))
                            {
                                Directory.CreateDirectory(parentDirectory);
                            }

                            _jsonSerializer.SerializeToFile(mediaSourcesWithChapters, mediaInfoJsonPath);
                        }, cancellationToken)
                        .ConfigureAwait(false);

                    _logger.Info("MediaInfoPersist - Serialization Success (" + source + "): " + mediaInfoJsonPath);

                    return true;
                }
                catch (Exception e)
                {
                    _logger.Error("MediaInfoPersist - Serialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public async Task<bool> SerializeMediaInfo(long itemId, bool overwrite, string source, CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId);

            if (!Plugin.LibraryApi.IsLibraryInScope(item) || !Plugin.LibraryApi.HasMediaInfo(item)) return false;

            var directoryService = new DirectoryService(_logger, _fileSystem);

            return await SerializeMediaInfo(item, directoryService, overwrite, source, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<bool> DeserializeMediaInfo(BaseItem item, IDirectoryService directoryService, string source,
            CancellationToken cancellationToken)
        {
            var workItem = _libraryManager.GetItemById(item.InternalId);
            
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

                    if (mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks.HasValue &&
                        !Plugin.LibraryApi.HasFileChanged(item, directoryService))
                    {
                        _itemRepository.SaveMediaStreams(item.InternalId,
                            mediaSourceWithChapters.MediaSourceInfo.MediaStreams, cancellationToken);

                        workItem.Size = mediaSourceWithChapters.MediaSourceInfo.Size.GetValueOrDefault();
                        workItem.RunTimeTicks = mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks;
                        workItem.Container = mediaSourceWithChapters.MediaSourceInfo.Container;
                        workItem.TotalBitrate = mediaSourceWithChapters.MediaSourceInfo.Bitrate.GetValueOrDefault();

                        _libraryManager.UpdateItems(new List<BaseItem> { workItem }, null,
                            ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                        if (workItem is Video)
                        {
                            _itemRepository.SaveChapters(workItem.InternalId, true, mediaSourceWithChapters.Chapters);

                            if (workItem is Episode && mediaSourceWithChapters.ZeroFingerprintConfidence is true)
                            {
                                BaseItem.ItemRepository.LogIntroDetectionFailureFailure(workItem.InternalId,
                                    item.DateModified.ToUnixTimeSeconds());
                            }
                        }

                        _logger.Info("MediaInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }

                    _logger.Info("MediaInfoPersist - Deserialization Skipped (" + source + "): " + mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    _logger.Error("MediaInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);

            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    _logger.Info("MediaInfoPersist - Attempting to delete (" + source + "): " + mediaInfoJsonPath);
                    _fileSystem.DeleteFile(mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    _logger.Error("MediaInfoPersist - Failed to delete (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }
        }

        public async Task<bool> DeserializeChapterInfo(Episode item, IDirectoryService directoryService, string source,
            CancellationToken cancellationToken)
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
                        BaseItem.ItemRepository.LogIntroDetectionFailureFailure(item.InternalId,
                            item.DateModified.ToUnixTimeSeconds());

                        _logger.Info("ChapterInfoPersist - Log Zero Fingerprint Confidence (" + source + "): " + mediaInfoJsonPath);

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

                        _logger.Info("ChapterInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("ChapterInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }
    }
}
