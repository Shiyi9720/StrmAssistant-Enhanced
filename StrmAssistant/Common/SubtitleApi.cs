using Emby.Naming.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
        private readonly object _audioTrackResolver;
        private readonly MethodInfo _getResolvedAudioMediaStreams;
        private readonly MethodInfo _getAudioLanguageFromFileName;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        private static readonly string[] DefaultExternalAudioExtensions =
        {
            ".flac", ".ac3", ".dts", ".dtshd", ".mka", ".aac", ".mp3", ".truehd", ".thd"
        };

        private const string OpenSubtitlesProviderName = "Open Subtitles";
        private const int MaxRateLimitRetries = 3;
        private const int RateLimitBaseDelaySeconds = 5;

        private readonly object _remoteSyncLock = new object();
        private ISubtitleManager _subtitleManager;
        private ISubtitleProvider _openSubtitlesProvider;

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

                var audioTrackResolverType = embyProviders.GetType("Emby.Providers.MediaInfo.AudioTrackResolver");
                var audioTrackResolverConstructor = audioTrackResolverType?.GetConstructor(new[]
                {
                    typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                });
                _audioTrackResolver = audioTrackResolverConstructor?.Invoke(new object[]
                {
                    localizationManager, fileSystem, libraryManager
                });
                _getResolvedAudioMediaStreams = audioTrackResolverType is null
                    ? null
                    : FindMethod(audioTrackResolverType, "GetResolvedMediaStreams", 6);
                _getAudioLanguageFromFileName = audioTrackResolverType is null
                    ? null
                    : FindMethod(audioTrackResolverType, "GetLanguageFromFileName", 1);

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
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            if (_subtitleResolver is null || _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null ||
                (_getExternalSubtitleStreams is null && _getResolvedMediaStreams is null && _getExternalTracks is null))
            {
                _logger?.Warn($"{nameof(SubtitleApi)} Init Failed");
            }

            if (_audioTrackResolver is null || _getResolvedAudioMediaStreams is null)
            {
                _logger?.Debug("External audio track resolver is not available");
            }
        }

        private List<MediaStream> GetExternalMediaStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            if (_getResolvedMediaStreams != null)
            {
                return GetExternalTracksByResolved(item, startIndex, directoryService, libraryOptions,
                    namingOptions, clearCache);
            }

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
                        _logger?.Debug(e.Message);
                        _logger?.Debug(e.StackTrace);
                    }
                }
            }

            if (_getExternalTracks != null)
            {
                try
                {
                    return (List<MediaStream>)_getExternalTracks.Invoke(_subtitleResolver,
                        new object[]
                        {
                            item, startIndex, directoryService, libraryOptions, namingOptions, clearCache
                        });
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

            return new List<MediaStream>();
        }

        private List<MediaStream> GetExternalTracksByResolved(BaseItem item, int startIndex,
            IDirectoryService directoryService, LibraryOptions libraryOptions, NamingOptions namingOptions,
            bool clearCache)
        {
            var streams = new List<MediaStream>();

            if (!item.IsFileProtocol) return streams;

            var includeAudio = IsExternalAudioScanEnabled() && _getResolvedAudioMediaStreams != null;
            var containingFolderPath = item.ContainingFolderPath;
            startIndex += AddExternalTracks(streams, containingFolderPath, item.Path, startIndex,
                directoryService, libraryOptions, namingOptions, clearCache, false, true, includeAudio);

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
                        startIndex += AddExternalTracks(streams, fileSystemEntry.FullName, item.Path, startIndex,
                            directoryService, libraryOptions, namingOptions, clearCache, acceptAllTracks, true,
                            false);
                    }
                }
                catch (IOException)
                {
                }
            }

            try
            {
                startIndex += AddExternalTracks(streams, item.GetInternalMetadataPath(), item.Path, startIndex,
                    directoryService, libraryOptions, namingOptions, clearCache, false, true, false);
            }
            catch (IOException)
            {
            }

            return streams;
        }

        private int AddExternalTracks(List<MediaStream> streams, string folder, string mediaPath, int startIndex,
            IDirectoryService directoryService, LibraryOptions libraryOptions, NamingOptions namingOptions,
            bool clearCache, bool acceptAllTracks, bool includeSubtitle, bool includeAudio)
        {
            var fileSystemEntries = directoryService.GetFileSystemEntries(folder, clearCache);
            var count = 0;

            if (includeSubtitle && _getResolvedMediaStreams != null)
            {
                count += AddResolvedTracks(streams, _subtitleResolver, _getResolvedMediaStreams, fileSystemEntries,
                    mediaPath, false, startIndex + count, libraryOptions, namingOptions, acceptAllTracks);
            }

            if (includeAudio && _getResolvedAudioMediaStreams != null)
            {
                count += AddExternalAudioTracks(streams, fileSystemEntries, mediaPath, startIndex + count,
                    libraryOptions, namingOptions, acceptAllTracks);
            }

            return count;
        }

        private int AddResolvedTracks(List<MediaStream> streams, object resolver, MethodInfo method,
            FileSystemMetadata[] fileSystemEntries, string mediaPath, bool isAudio, int startIndex,
            LibraryOptions libraryOptions, NamingOptions namingOptions, bool acceptAllTracks)
        {
            var mediaFileNameWithoutExtension = _fileSystem.GetFileNameWithoutExtension(mediaPath);
            mediaFileNameWithoutExtension = NormalizeFilenameForTrackComparison(mediaFileNameWithoutExtension);

            try
            {
                var resolvedStreams = (List<MediaStream>)method.Invoke(resolver,
                    new object[]
                    {
                        mediaFileNameWithoutExtension, isAudio, fileSystemEntries, libraryOptions, namingOptions,
                        acceptAllTracks
                    });

                return AddTrackStreams(streams, resolvedStreams, startIndex);
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }

                return 0;
            }
        }

        private int AddExternalAudioTracks(List<MediaStream> streams, FileSystemMetadata[] fileSystemEntries,
            string mediaPath, int startIndex, LibraryOptions libraryOptions, NamingOptions namingOptions,
            bool acceptAllTracks)
        {
            var extensions = GetExternalAudioExtensions();
            if (extensions.Count == 0) return 0;

            var embyAudioExtensions = new HashSet<string>(
                NamingOptions.StaticAudioFileExtensions.Where(i =>
                    !string.Equals(i, ".strm", StringComparison.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);

            var resolvedEntries = new List<FileSystemMetadata>();
            var additionalEntries = new List<FileSystemMetadata>();

            foreach (var entry in fileSystemEntries)
            {
                if (entry.IsDirectory) continue;

                var extension = Path.GetExtension(entry.FullName);
                if (string.IsNullOrEmpty(extension) || !extensions.Contains(extension)) continue;

                if (embyAudioExtensions.Contains(extension))
                {
                    resolvedEntries.Add(entry);
                }
                else
                {
                    additionalEntries.Add(entry);
                }
            }

            var count = 0;

            if (resolvedEntries.Count > 0)
            {
                // Emby 4.10 moved external audio resolution from SubtitleResolver to
                // AudioTrackResolver. Its GetResolvedMediaStreams isAudio flag means "the item is
                // an audio file (lyrics)", so video items must always pass false here.
                count += AddResolvedTracks(streams, _audioTrackResolver, _getResolvedAudioMediaStreams,
                    resolvedEntries.ToArray(), mediaPath, false, startIndex + count, libraryOptions, namingOptions,
                    acceptAllTracks);
            }

            if (additionalEntries.Count > 0)
            {
                count += AddConfiguredAudioTracks(streams, additionalEntries, mediaPath, startIndex + count,
                    namingOptions, acceptAllTracks);
            }

            return count;
        }

        private int AddConfiguredAudioTracks(List<MediaStream> streams, List<FileSystemMetadata> fileSystemEntries,
            string mediaPath, int startIndex, NamingOptions namingOptions, bool acceptAllTracks)
        {
            var mediaFileNameWithoutExtension = _fileSystem.GetFileNameWithoutExtension(mediaPath);
            mediaFileNameWithoutExtension = NormalizeFilenameForTrackComparison(mediaFileNameWithoutExtension);
            var resolvedStreams = new List<MediaStream>();

            foreach (var fileSystemEntry in fileSystemEntries)
            {
                var name = fileSystemEntry.Name;
                var fullName = fileSystemEntry.FullName;
                var extension = Path.GetExtension(fullName);
                if (string.IsNullOrEmpty(extension)) continue;

                var fileNameWithoutExtension = _fileSystem.GetFileNameWithoutExtension(name);
                var originalFileNameWithoutExtension = fileNameWithoutExtension;
                fileNameWithoutExtension = NormalizeFilenameForTrackComparison(fileNameWithoutExtension);

                string titleSuffix = null;

                if (string.Equals(mediaFileNameWithoutExtension, fileNameWithoutExtension,
                        StringComparison.OrdinalIgnoreCase))
                {
                }
                else
                {
                    if (!acceptAllTracks &&
                        !fileNameWithoutExtension.StartsWith(mediaFileNameWithoutExtension + ".",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (fileNameWithoutExtension.StartsWith(mediaFileNameWithoutExtension + ".",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        titleSuffix = fileNameWithoutExtension.Substring(mediaFileNameWithoutExtension.Length + 1);
                    }
                }

                var isDefault = namingOptions.SubtitleDefaultFlags?.Any(i =>
                    name.IndexOf("." + i + ".", StringComparison.OrdinalIgnoreCase) != -1) == true;

                GetAudioLanguageAndTitle(originalFileNameWithoutExtension, titleSuffix, out var language,
                    out var title);

                resolvedStreams.Add(new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    IsExternal = true,
                    Path = fullName,
                    Protocol = MediaProtocol.File,
                    Codec = extension.TrimStart('.').ToLowerInvariant(),
                    Language = language,
                    IsDefault = isDefault,
                    Title = title
                });
            }

            return AddTrackStreams(streams, resolvedStreams, startIndex);
        }

        private void GetAudioLanguageAndTitle(string fileNameWithoutExtension, string titleSuffix,
            out string language, out string title)
        {
            language = null;
            title = titleSuffix;

            if (string.Equals(title, "default", StringComparison.OrdinalIgnoreCase)) title = null;

            if (_getAudioLanguageFromFileName is null || _audioTrackResolver is null) return;

            try
            {
                var languageInfo = _getAudioLanguageFromFileName.Invoke(_audioTrackResolver,
                    new object[] { string.IsNullOrEmpty(titleSuffix) ? fileNameWithoutExtension : titleSuffix });

                if (languageInfo is null) return;

                var languageInfoType = languageInfo.GetType();
                var culture = languageInfoType.GetField("Language")?.GetValue(languageInfo) as CultureDto;
                var languagePart = languageInfoType.GetField("FilenamePart")?.GetValue(languageInfo) as string;
                var allParts = languageInfoType.GetField("AllFilenameParts")?.GetValue(languageInfo) as string[];

                language = culture?.TwoLetterISOLanguageName;

                if (!string.IsNullOrEmpty(titleSuffix) && !string.IsNullOrEmpty(languagePart) && allParts != null)
                {
                    title = string.Join(".", allParts.Where(i =>
                        !string.Equals(i, languagePart, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(i, "default", StringComparison.OrdinalIgnoreCase)));
                }
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
            }

            if (string.IsNullOrEmpty(title) || string.Equals(title, "default", StringComparison.OrdinalIgnoreCase))
            {
                title = null;
            }
        }

        private int AddTrackStreams(List<MediaStream> streams, List<MediaStream> resolvedStreams, int startIndex)
        {
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

        private bool IsExternalAudioScanEnabled()
        {
            return _getResolvedMediaStreams != null && _audioTrackResolver != null &&
                   _getResolvedAudioMediaStreams != null &&
                   Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions?.EnableExternalAudioScan == true;
        }

        private static HashSet<string> GetExternalAudioExtensions()
        {
            var configured = Plugin.Instance?.GetPluginOptions()?.MediaInfoExtractOptions?.ExternalAudioExtensions;
            var values = string.IsNullOrWhiteSpace(configured)
                ? DefaultExternalAudioExtensions
                : configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var value in values)
            {
                var extension = value.Trim().ToLowerInvariant();
                if (extension.Length == 0) continue;
                if (!extension.StartsWith(".")) extension = "." + extension;
                extensions.Add(extension);
            }

            return extensions;
        }

        private static MethodInfo FindMethod(Type type, string name, int parameterCount)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var method = current
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                BindingFlags.DeclaredOnly)
                    .FirstOrDefault(i => i.Name == name && i.GetParameters().Length == parameterCount);

                if (method != null) return method;
            }

            return null;
        }

        private static string NormalizeFilenameForTrackComparison(string filename)
        {
            return filename.Replace("_", string.Empty).Replace(" ", string.Empty);
        }

        private async Task<bool> UpdateExternalSubtitleStream(BaseItem item,
            MediaStream stream, MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            var task = _updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                new object[] { item, stream, options, libraryOptions, cancellationToken }) as Task;

            if (task is null)
            {
                _logger?.Debug("SubtitleApi - UpdateExternalSubtitleStream returned no task");
                return false;
            }

            var result = await ReflectionTaskHelper.AwaitAsObject(task).ConfigureAwait(false);

            return result is bool updated && updated;
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
            var includeAudio = IsExternalAudioScanEnabled() && _getResolvedAudioMediaStreams != null;
            var trackTypes = includeAudio
                ? new[] { MediaStreamType.Subtitle, MediaStreamType.Audio }
                : new[] { MediaStreamType.Subtitle };
            var currentExternalSubtitleFiles = _libraryManager
                .GetExternalTracks(item.InternalId, trackTypes, CancellationToken.None)
                .Select(i => i.Item1);
            var currentSet = new HashSet<string>(currentExternalSubtitleFiles, StringComparer.Ordinal);

            try
            {
                var newExternalSubtitleFiles = GetExternalMediaStreams(item, 0, directoryService, clearCache)
                    .Where(i => i.Type == MediaStreamType.Subtitle ||
                                (includeAudio && i.Type == MediaStreamType.Audio))
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

        /// <summary>
        /// Completes the plugin's own external stream data for subtitles and, when enabled, audio.
        /// This does not take over Emby transcoding selection; it only refreshes the streams that
        /// the plugin merges and persists for the item.
        /// </summary>
        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var includeAudio = IsExternalAudioScanEnabled() && _getResolvedAudioMediaStreams != null;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i =>
                    !(i.IsExternal && i.Protocol == MediaProtocol.File &&
                      (i.Type == MediaStreamType.Subtitle ||
                       (includeAudio && i.Type == MediaStreamType.Audio))));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalMediaStreams(item, startIndex, directoryService, clearCache) is
                { } externalTracks)
            {
                foreach (var externalTrack in externalTracks)
                {
                    var extension = Path.GetExtension(externalTrack.Path);

                    if (externalTrack.Type == MediaStreamType.Audio)
                    {
                        var result = await UpdateExternalSubtitleStream(item, externalTrack, refreshOptions,
                            CancellationToken.None).ConfigureAwait(false);

                        if (!result)
                        {
                            _logger?.Warn("No result when probing external audio file: {0}", externalTrack.Path);
                        }

                        _logger?.Info("ExternalAudio - Audio Processed: " + externalTrack.Path);
                        continue;
                    }

                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        var result =
                            await UpdateExternalSubtitleStream(item, externalTrack, refreshOptions,
                                CancellationToken.None).ConfigureAwait(false);

                        if (!result)
                        {
                            _logger?.Warn("No result when probing external subtitle file: {0}",
                                externalTrack.Path);
                        }
                    }

                    _logger?.Info("ExternalSubtitle - Subtitle Processed: " + externalTrack.Path);
                }

                currentStreams.AddRange(externalTracks);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);
                Plugin.MediaInfoApi.RemoveBlacklistedSubtitleStreams(item, "External Subtitle Update");

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true,
                        "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }

        public async Task<bool> DownloadSubtitles(BaseItem item, CancellationToken cancellationToken)
        {
            var subtitleManager = GetSubtitleManager();
            var provider = GetOpenSubtitlesProvider();

            if (subtitleManager is null || provider is null)
            {
                _logger?.Warn("SubtitleDownload - OpenSubtitles provider is not available");
                return false;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            if (libraryOptions.DisabledSubtitleFetchers?.Contains(provider.Name,
                    StringComparer.OrdinalIgnoreCase) == true)
            {
                _logger?.Info("SubtitleDownload - OpenSubtitles is disabled for library: " + item.Path);
                return false;
            }

            var targetLanguages = GetTargetSubtitleLanguages(item, libraryOptions);
            var downloaded = false;

            foreach (var language in targetLanguages)
            {
                if (HasSubtitleLanguage(item, language))
                {
                    _logger?.Debug("SubtitleDownload - Subtitle already exists: " +
                                  language.TwoLetterISOLanguageName + " - " + item.Name);
                    continue;
                }

                var match = await SearchSubtitles(provider, item, language, libraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (match is null)
                {
                    _logger?.Info("SubtitleDownload - No subtitle found: " + language.TwoLetterISOLanguageName +
                                 " - " + item.Name + " - " + item.Path);
                    continue;
                }

                var subtitleId = GetProviderId(provider.Name) + "_" + language.TwoLetterISOLanguageName + "_" +
                                 match.Id;

                var savePath = await ExecuteWithRateLimitRetry(
                        () => subtitleManager.DownloadSubtitles(item, subtitleId, libraryOptions, cancellationToken),
                        "download " + language.TwoLetterISOLanguageName, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(savePath))
                {
                    downloaded = true;
                    _logger?.Info("SubtitleDownload - Downloaded subtitle: " + language.TwoLetterISOLanguageName +
                                 " - " + item.Name + " - " + savePath);
                }
            }

            if (downloaded)
            {
                var persistMediaInfo =
                    Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.PersistMediaInfoMode !=
                    StrmAssistant.Options.MediaInfoExtractOptions.PersistMediaInfoOption.None.ToString();

                await UpdateExternalSubtitles(item, GetExternalSubtitleRefreshOptions(), true, persistMediaInfo)
                    .ConfigureAwait(false);
            }

            return downloaded;
        }

        private async Task<RemoteSubtitleInfo> SearchSubtitles(ISubtitleProvider provider, BaseItem item,
            CultureDto language, LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            var request = CreateSearchRequest(item, language, libraryOptions);

            var results = await ExecuteWithRateLimitRetry(() => SearchProvider(provider, request, cancellationToken),
                "search " + language.TwoLetterISOLanguageName, cancellationToken).ConfigureAwait(false);

            if (results != null && results.Length > 0)
            {
                return SelectBestMatch(results);
            }

            if (libraryOptions.RequirePerfectSubtitleMatch)
            {
                return null;
            }

            // Fall back to a non-perfect search so that title based matches are still considered.
            request.IsPerfectMatch = false;

            results = await ExecuteWithRateLimitRetry(() => SearchProvider(provider, request, cancellationToken),
                "search " + language.TwoLetterISOLanguageName, cancellationToken).ConfigureAwait(false);

            return results != null && results.Length > 0 ? SelectBestMatch(results) : null;
        }

        private static async Task<RemoteSubtitleInfo[]> SearchProvider(ISubtitleProvider provider,
            SubtitleSearchRequest request, CancellationToken cancellationToken)
        {
            var results = await provider.Search(request, cancellationToken).ConfigureAwait(false);

            return results?.ToArray() ?? Array.Empty<RemoteSubtitleInfo>();
        }

        private static RemoteSubtitleInfo SelectBestMatch(IEnumerable<RemoteSubtitleInfo> results)
        {
            return results.OrderByDescending(i => i.IsHashMatch == true)
                .ThenByDescending(i => i.DownloadCount ?? 0)
                .First();
        }

        private static SubtitleSearchRequest CreateSearchRequest(BaseItem item, CultureDto language,
            LibraryOptions libraryOptions)
        {
            var contentType = item is Episode
                ? VideoContentType.Episode
                : item is Movie
                    ? VideoContentType.Movie
                    : VideoContentType.Audio;

            var request = new SubtitleSearchRequest
            {
                ContentType = contentType,
                LanguageInfo = language,
                Language = language.TwoLetterISOLanguageName,
                MediaPath = item.Path,
                Name = item.Name,
                IndexNumber = item.IndexNumber,
                ParentIndexNumber = item.ParentIndexNumber,
                ProductionYear = item.ProductionYear,
                RuntimeTicks = item.RunTimeTicks,
                ProviderIds = new ProviderIdDictionary(item.ProviderIds),
                SearchAllProviders = false,
                // Prefer the exact (movie hash) match first, fall back later when allowed.
                IsPerfectMatch = true,
                IsForced = libraryOptions.ForcedSubtitlesOnly ? true : (bool?)null,
                IsHearingImpaired = libraryOptions.HearingImpairedSubtitlesOnly ? true : (bool?)null,
                DisabledSubtitleFetchers = libraryOptions.DisabledSubtitleFetchers ?? Array.Empty<string>(),
                SubtitleFetcherOrder = libraryOptions.SubtitleFetcherOrder ?? Array.Empty<string>()
            };

            if (item is Episode episode)
            {
                request.IndexNumberEnd = episode.IndexNumberEnd;
                request.SeriesName = episode.SeriesName;
                request.SeriesProviderIds = episode.Series != null
                    ? new ProviderIdDictionary(episode.Series.ProviderIds)
                    : new ProviderIdDictionary();
            }

            return request;
        }

        private List<CultureDto> GetTargetSubtitleLanguages(BaseItem item, LibraryOptions libraryOptions)
        {
            var languages = new List<CultureDto>();

            if (libraryOptions.SubtitleDownloadLanguages != null)
            {
                foreach (var configuredLanguage in libraryOptions.SubtitleDownloadLanguages)
                {
                    if (string.IsNullOrWhiteSpace(configuredLanguage)) continue;

                    var languageInfo = _localizationManager.FindLanguageInfo(configuredLanguage);

                    if (languageInfo != null) languages.Add(languageInfo);
                }
            }

            if (languages.Count == 0)
            {
                // Fall back to the language of the media itself.
                var mediaLanguage = GetMediaLanguage(item);

                if (!string.IsNullOrEmpty(mediaLanguage))
                {
                    var languageInfo = _localizationManager.FindLanguageInfo(mediaLanguage);

                    if (languageInfo != null) languages.Add(languageInfo);
                }
            }

            return languages.GroupBy(l => l.TwoLetterISOLanguageName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();
        }

        private static string GetMediaLanguage(BaseItem item)
        {
            var streams = item.GetMediaStreams();
            var audioStream = streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio && s.IsDefault) ??
                              streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio);

            return audioStream?.Language;
        }

        private bool HasSubtitleLanguage(BaseItem item, CultureDto language)
        {
            foreach (var subtitleStream in item.GetMediaStreams()
                         .Where(s => s.Type == MediaStreamType.Subtitle && !string.IsNullOrEmpty(s.Language)))
            {
                var existingLanguage = _localizationManager.FindLanguageInfo(subtitleStream.Language);

                if (existingLanguage != null && string.Equals(existingLanguage.TwoLetterISOLanguageName,
                        language.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<T> ExecuteWithRateLimitRetry<T>(Func<Task<T>> action, string description,
            CancellationToken cancellationToken)
        {
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    return await action().ConfigureAwait(false);
                }
                catch (RateLimitExceededException) when (attempt < MaxRateLimitRetries)
                {
                    var delay = TimeSpan.FromSeconds(RateLimitBaseDelaySeconds * Math.Pow(2, attempt));

                    _logger?.Info("SubtitleDownload - Rate limit reached during " + description +
                                 ", retrying in " + delay.TotalSeconds + " seconds");

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (RateLimitExceededException)
                {
                    _logger?.Warn("SubtitleDownload - Rate limit still reached during " + description + ", give up");
                    return default;
                }
            }
        }

        private static string GetProviderId(string providerName)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.Unicode.GetBytes(providerName.ToLowerInvariant()));

                return new Guid(hash).ToString("N");
            }
        }

        private ISubtitleManager GetSubtitleManager()
        {
            if (_subtitleManager != null) return _subtitleManager;

            lock (_remoteSyncLock)
            {
                if (_subtitleManager != null) return _subtitleManager;

                try
                {
                    var applicationHost = Plugin.Instance.ApplicationHost;
                    var property = applicationHost.GetType().GetProperty("SubtitleManager",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    _subtitleManager = property?.GetValue(applicationHost) as ISubtitleManager;
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

            return _subtitleManager;
        }

        private ISubtitleProvider GetOpenSubtitlesProvider()
        {
            if (_openSubtitlesProvider != null) return _openSubtitlesProvider;

            var subtitleManager = GetSubtitleManager();

            if (subtitleManager is null) return null;

            lock (_remoteSyncLock)
            {
                if (_openSubtitlesProvider != null) return _openSubtitlesProvider;

                try
                {
                    var field = subtitleManager.GetType().GetField("_subtitleProviders",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    var providers = field?.GetValue(subtitleManager) as IEnumerable<ISubtitleProvider>;

                    _openSubtitlesProvider = providers?.FirstOrDefault(p =>
                        string.Equals(p.Name, OpenSubtitlesProviderName, StringComparison.OrdinalIgnoreCase));

                    ProxyApi.PatchProviderHttpClients(_openSubtitlesProvider);
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

            return _openSubtitlesProvider;
        }
    }
}
