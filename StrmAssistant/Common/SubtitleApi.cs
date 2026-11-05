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
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

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

        public async Task<bool> DownloadSubtitles(BaseItem item, CancellationToken cancellationToken)
        {
            var subtitleManager = GetSubtitleManager();
            var provider = GetOpenSubtitlesProvider();

            if (subtitleManager is null || provider is null)
            {
                _logger.Warn("SubtitleDownload - OpenSubtitles provider is not available");
                return false;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            if (libraryOptions.DisabledSubtitleFetchers?.Contains(provider.Name,
                    StringComparer.OrdinalIgnoreCase) == true)
            {
                _logger.Info("SubtitleDownload - OpenSubtitles is disabled for library: " + item.Path);
                return false;
            }

            var targetLanguages = GetTargetSubtitleLanguages(item, libraryOptions);
            var downloaded = false;

            foreach (var language in targetLanguages)
            {
                if (HasSubtitleLanguage(item, language))
                {
                    _logger.Debug("SubtitleDownload - Subtitle already exists: " +
                                  language.TwoLetterISOLanguageName + " - " + item.Name);
                    continue;
                }

                var match = await SearchSubtitles(provider, item, language, libraryOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (match is null)
                {
                    _logger.Info("SubtitleDownload - No subtitle found: " + language.TwoLetterISOLanguageName +
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
                    _logger.Info("SubtitleDownload - Downloaded subtitle: " + language.TwoLetterISOLanguageName +
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

                    _logger.Info("SubtitleDownload - Rate limit reached during " + description +
                                 ", retrying in " + delay.TotalSeconds + " seconds");

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (RateLimitExceededException)
                {
                    _logger.Warn("SubtitleDownload - Rate limit still reached during " + description + ", give up");
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
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
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
                        _logger.Debug(e.Message);
                        _logger.Debug(e.StackTrace);
                    }
                }
            }

            return _openSubtitlesProvider;
        }
    }
}
