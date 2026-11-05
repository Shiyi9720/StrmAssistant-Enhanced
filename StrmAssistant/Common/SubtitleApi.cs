using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class SubtitleApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;
        private readonly ILocalizationManager _localizationManager;

        private readonly object _subtitleResolver;
        private readonly MethodInfo _getExternalSubtitleStreams;
        private readonly MethodInfo _getResolvedMediaStreams;
        private readonly MethodInfo _getExternalTracks;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        public SubtitleApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;
            _localizationManager = localizationManager;

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var subtitleResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.SubtitleResolver");
                var subtitleResolverConstructor = subtitleResolverType.GetConstructor(new[]
                {
                    typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                });
                _subtitleResolver = subtitleResolverConstructor?.Invoke(new object[]
                {
                    localizationManager, fileSystem, libraryManager
                });
                _getExternalSubtitleStreams = subtitleResolverType.GetMethod("GetExternalSubtitleStreams");

                var resolvedMediaStreamsMethods = subtitleResolverType
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .Where(m => m.Name == "GetResolvedMediaStreams" && m.GetParameters().Length == 6)
                    .ToList();
                _getResolvedMediaStreams =
                    resolvedMediaStreamsMethods.FirstOrDefault(m => m.DeclaringType == subtitleResolverType) ??
                    resolvedMediaStreamsMethods.FirstOrDefault();
                _getExternalTracks = subtitleResolverType.GetMethod("GetExternalTracks");

                var ffProbeSubtitleInfoType = embyProviders.GetType("Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                var ffProbeSubtitleInfoConstructor = ffProbeSubtitleInfoType.GetConstructor(new[]
                {
                    typeof(IMediaProbeManager)
                });
                _ffProbeSubtitleInfo = ffProbeSubtitleInfoConstructor?.Invoke(new object[] { mediaProbeManager });
                _updateExternalSubtitleStream = ffProbeSubtitleInfoType.GetMethod("UpdateExternalSubtitleStream");
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_subtitleResolver is null || _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null ||
                (_getExternalSubtitleStreams is null && _getResolvedMediaStreams is null && _getExternalTracks is null))
            {
                _logger.Warn($"{nameof(SubtitleApi)} Init Failed");
            }
        }

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            if (_getExternalSubtitleStreams != null)
            {
                try
                {
                    return (List<MediaStream>)_getExternalSubtitleStreams.Invoke(_subtitleResolver,
                        new object[] { item, startIndex, directoryService, namingOptions, clearCache });
                }
                catch (Exception e)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                }
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            if (_getResolvedMediaStreams != null)
            {
                return GetExternalSubtitleStreamsByResolved(item, startIndex, directoryService, libraryOptions,
                    namingOptions, clearCache);
            }

            if (_getExternalTracks != null)
            {
                return (List<MediaStream>)_getExternalTracks.Invoke(_subtitleResolver,
                    new object[] { item, startIndex, directoryService, libraryOptions, namingOptions, clearCache });
            }

            return new List<MediaStream>();
        }

        private List<MediaStream> GetExternalSubtitleStreamsByResolved(BaseItem item, int startIndex,
            IDirectoryService directoryService, LibraryOptions libraryOptions, NamingOptions namingOptions,
            bool clearCache)
        {
            var streams = new List<MediaStream>();

            if (!item.IsFileProtocol) return streams;

            var containingFolderPath = item.ContainingFolderPath;
            startIndex += AddExternalTracks(streams, containingFolderPath, item.Path, false, startIndex,
                directoryService, libraryOptions, namingOptions, clearCache, false);

            var subtitleFolderNames = namingOptions.SubtitleFolderNames;
            if (subtitleFolderNames?.Length > 0)
            {
                try
                {
                    var fileSystemEntries = directoryService.GetFileSystemEntries(containingFolderPath);
                    foreach (var fileSystemEntry in fileSystemEntries)
                    {
                        if (!fileSystemEntry.IsDirectory || !subtitleFolderNames.Contains(fileSystemEntry.Name,
                                StringComparer.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var acceptAllTracks = !item.IsInMixedFolder && !item.ExtraType.HasValue;
                        startIndex += AddExternalTracks(streams, fileSystemEntry.FullName, item.Path, false,
                            startIndex, directoryService, libraryOptions, namingOptions, clearCache, acceptAllTracks);
                    }
                }
                catch (IOException)
                {
                }
            }

            try
            {
                startIndex += AddExternalTracks(streams, item.GetInternalMetadataPath(), item.Path, false,
                    startIndex, directoryService, libraryOptions, namingOptions, clearCache, false);
            }
            catch (IOException)
            {
            }

            return streams;
        }

        private int AddExternalTracks(List<MediaStream> streams, string folder, string mediaPath, bool isAudio,
            int startIndex, IDirectoryService directoryService, LibraryOptions libraryOptions,
            NamingOptions namingOptions, bool clearCache, bool acceptAllTracks)
        {
            var fileSystemEntries = directoryService.GetFileSystemEntries(folder, clearCache);
            var mediaFileNameWithoutExtension = _fileSystem.GetFileNameWithoutExtension(mediaPath);
            mediaFileNameWithoutExtension = NormalizeFilenameForTrackComparison(mediaFileNameWithoutExtension);

            var resolvedStreams = (List<MediaStream>)_getResolvedMediaStreams.Invoke(_subtitleResolver,
                new object[]
                {
                    mediaFileNameWithoutExtension, isAudio, fileSystemEntries, libraryOptions, namingOptions,
                    acceptAllTracks
                });

            if (resolvedStreams is null || resolvedStreams.Count == 0) return 0;

            resolvedStreams = resolvedStreams
                .OrderBy(i => Path.GetFileName(i.Path), _localizationManager.DisplaySortingComparer)
                .ToList();

            foreach (var stream in resolvedStreams)
            {
                stream.Index = startIndex++;
            }

            streams.AddRange(resolvedStreams);

            return resolvedStreams.Count;
        }

        private static string NormalizeFilenameForTrackComparison(string filename)
        {
            return filename.Replace("_", string.Empty).Replace(" ", string.Empty);
        }

        private Task<bool> UpdateExternalSubtitleStream(BaseItem item,
            MediaStream subtitleStream, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            return (Task<bool>)_updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                new object[] { item, subtitleStream, options, libraryOptions, cancellationToken });
        }

        public MetadataRefreshOptions GetExternalSubtitleRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            var currentExternalSubtitleFiles = _libraryManager
                .GetExternalTracks(item.InternalId, new[] { MediaStreamType.Subtitle }, CancellationToken.None)
                .Select(i => i.Item1);
            var currentSet = new HashSet<string>(currentExternalSubtitleFiles, StringComparer.Ordinal);

            try
            {
                var newExternalSubtitleFiles = GetExternalSubtitleStreams(item, 0, directoryService, clearCache)
                    .Select(i => i.Path)
                    .ToArray();
                var newSet = new HashSet<string>(newExternalSubtitleFiles, StringComparer.Ordinal);

                return !currentSet.SetEquals(newSet);
            }
            catch
            {
                // ignored
            }

            return false;
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i =>
                    !(i.IsExternal && i.Type == MediaStreamType.Subtitle && i.Protocol == MediaProtocol.File));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache) is
                { } externalSubtitleStreams)
            {
                foreach (var subtitleStream in externalSubtitleStreams)
                {
                    var extension = Path.GetExtension(subtitleStream.Path);
                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        var result =
                            await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions,
                                CancellationToken.None).ConfigureAwait(false);

                        if (!result)
                            _logger.Warn("No result when probing external subtitle file: {0}", subtitleStream.Path);
                    }

                    _logger.Info("ExternalSubtitle - Subtitle Processed: " + subtitleStream.Path);
                }

                currentStreams.AddRange(externalSubtitleStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true,
                        "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }
    }
}
