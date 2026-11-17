using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly IFfmpegManager _ffmpegManager;
        private readonly IMediaEncoder _mediaEncoder;
        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger _logger;

        private readonly object _audioFingerprintManager;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly MethodInfo _getAllFingerprintFilesForSeason;
        private readonly MethodInfo _updateSequencesForSeason;
        private readonly FieldInfo _timeoutMs;

        private const int RawFingerprintSampleRate = 16000;

        private static readonly object MarkerWriteLock = ChapterApi.MarkerWriteLock;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer, IItemRepository itemRepository,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;
            _ffmpegManager = ffmpegManager;
            _mediaEncoder = mediaEncoder;
            _applicationPaths = applicationPaths;

            UpdateLibraryPathsInScope();

            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                var audioFingerprintManagerConstructor = audioFingerprintManager.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                        typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                        typeof(IServerApplicationHost)
                    }, null);
                _audioFingerprintManager = audioFingerprintManagerConstructor?.Invoke(new object[]
                {
                    fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                    jsonSerializer, serverApplicationHost
                });
                _createTitleFingerprint = audioFingerprintManager.GetMethod("CreateTitleFingerprint",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[]
                    {
                        typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService),
                        typeof(CancellationToken)
                    }, null);
                _getAllFingerprintFilesForSeason = audioFingerprintManager.GetMethod("GetAllFingerprintFilesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _updateSequencesForSeason = audioFingerprintManager.GetMethod("UpdateSequencesForSeason",
                    BindingFlags.Public | BindingFlags.Instance);
                _timeoutMs = audioFingerprintManager.GetField("TimeoutMs",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                PatchTimeout(Plugin.Instance.GetPluginOptions().GeneralOptions.IntroDetectionMaxConcurrentCount);
            }
            catch (Exception e)
            {
                // Reflective init of Emby internals; a failure here degrades intro detection.
                _logger?.Warn("FingerprintApi - Init failed: " + e.Message + Environment.NewLine +
                              e.StackTrace);
            }

            if (_audioFingerprintManager is null || _createTitleFingerprint is null ||
                _getAllFingerprintFilesForSeason is null || _updateSequencesForSeason is null || _timeoutMs is null)
            {
                _logger?.Warn($"{nameof(FingerprintApi)} Init Failed");
            }
        }

        public async Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, IDirectoryService directoryService,
            CancellationToken cancellationToken, DistributionStats distributionStats = null)
        {
            if (FingerprintBlacklist.FromOptions().IsExcluded(item))
            {
                _logger.Info("FingerprintBlacklist - Skipped: " + item.Name + " - " + item.Path);
                return null;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            // Optional distributed extraction: the worker only creates the raw fingerprint file;
            // sequence detection and marker writes stay on this node. Every failure falls back to
            // the local extraction below.
            if (Plugin.DistributedExtractApi != null && DistributedExtractApi.IsFingerprintOptionEnabled)
            {
                var distributedPath = await Plugin.DistributedExtractApi
                    .TryCreateTitleFingerprintAsync(item, libraryOptions, cancellationToken, distributionStats)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(distributedPath))
                {
                    return new Tuple<string, bool>(distributedPath, true);
                }
            }

            var createTitleFingerprintTask = _createTitleFingerprint.Invoke(_audioFingerprintManager,
                new object[] { item, libraryOptions, directoryService, cancellationToken }) as Task;

            if (createTitleFingerprintTask is null)
            {
                _logger?.Debug("FingerprintApi - CreateTitleFingerprint returned no task");
                return null;
            }

            return (Tuple<string, bool>)await ReflectionTaskHelper
                .AwaitAsObject(createTitleFingerprintTask).ConfigureAwait(false);
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken)
        {
            var directoryService = new DirectoryService(_logger, _fileSystem);

            return CreateTitleFingerprint(item, directoryService, cancellationToken);
        }

        /// <summary>
        /// Records an intro detection failure for a single episode, matching Emby's
        /// AudioFingerprintManager.OnFailedToFindIntro semantics: the stored value is the
        /// episode's current DateModified in Unix seconds, so InternalItemsQuery with
        /// HasIntroDetectionFailure = false skips the episode until its DateModified changes.
        /// </summary>
        public void LogIntroDetectionFailure(Episode item)
        {
            try
            {
                _itemRepository.LogIntroDetectionFailureFailure(item.InternalId,
                    item.DateModified.ToUnixTimeSeconds());

                _logger?.Info("IntroFingerprintExtract - Intro Detection Failure Recorded: " + item.Name + " - " +
                              item.Path);
            }
            catch (Exception e)
            {
                _logger?.ErrorException("IntroFingerprintExtract - Failed to Record Intro Detection Failure: " + item.Name +
                               " - " + item.Path, e);
            }
        }

        /// <summary>
        /// Creates raw chromaprint data for a local file. This is the same ffmpeg pipeline Emby's
        /// AudioFingerprintManager uses for title fingerprints; the caller owns the start position
        /// and duration. Returns null when ffmpeg or chromaprint is unavailable.
        /// </summary>
        public async Task<byte[]> CreateRawFingerprintAsync(string inputPath, TimeSpan start, TimeSpan duration,
            int timeoutSeconds, CancellationToken cancellationToken)
        {
            if (_ffmpegManager is null || _mediaEncoder is null) return null;

            if (_ffmpegManager.FfmpegCapabilities?.SupportsChromaprint != true) return null;

            var tempPath = Path.Combine(_applicationPaths.TempDirectory, Guid.NewGuid().ToString("N"));
            var normalizedInputPath = StrmUrlHelper.Normalize(inputPath);
            var protocol = CustomFfprobeApi.IsHttpUrl(normalizedInputPath)
                ? MediaProtocol.Http
                : MediaProtocol.File;
            var inputArgument = _mediaEncoder.GetInputArgument(normalizedInputPath.AsSpan(), protocol);
            var startArgument = start > TimeSpan.Zero
                ? "-t " + _mediaEncoder.GetTimeParameter(start.Ticks)
                : string.Empty;

            var arguments = string.Join(" ", new[]
            {
                startArgument,
                "-i " + inputArgument,
                "-t " + _mediaEncoder.GetTimeParameter(duration.Ticks),
                "-ac 1",
                "-acodec pcm_s16le",
                "-ar " + RawFingerprintSampleRate.ToString(CultureInfo.InvariantCulture),
                "-vn",
                "-sn",
                "-f chromaprint",
                "-fp_format raw",
                "\"" + tempPath + "\""
            });

            try
            {
                using var runner = _ffmpegManager.CreateFfMpegRunner("CreateTitleFingerprint", null);
                runner.Start(arguments);

                if (!await runner.WaitForExitAsync(timeoutSeconds * 1000, cancellationToken)
                        .ConfigureAwait(false))
                {
                    throw new TimeoutException("Fingerprint extraction timed out after " + timeoutSeconds +
                                               " seconds");
                }

                if (runner.LastExitCode.GetValueOrDefault() == -1 || !File.Exists(tempPath))
                {
                    runner.LogOutput(LogSeverity.Error);
                    return null;
                }

                var bytes = await _fileSystem.ReadAllBytesAsync(tempPath, cancellationToken)
                    .ConfigureAwait(false);

                return bytes.Length > 0 && bytes.Length % 4 == 0 ? bytes : null;
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch (Exception e)
                {
                    _logger?.Debug("IntroFingerprintExtract - Failed to delete temporary file: " + tempPath +
                                   Environment.NewLine + e);
                }
            }
        }

        private Task<object> GetAllFingerprintFilesForSeason(Season season, Episode[] episodes,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            var task = _getAllFingerprintFilesForSeason.Invoke(_audioFingerprintManager,
                new object[] { season, episodes, libraryOptions, directoryService, cancellationToken }) as Task;

            if (task is null)
            {
                _logger?.Debug("FingerprintApi - GetAllFingerprintFilesForSeason returned no task");
                return Task.FromResult<object>(null);
            }

            return ReflectionTaskHelper.AwaitAsObject(task);
        }

        private void UpdateSequencesForSeason(Season season, object seasonFingerprintInfo, Episode episode,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            _updateSequencesForSeason.Invoke(_audioFingerprintManager,
                new object[]
                {
                    season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken
                });
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = maxConcurrentCount * Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            _timeoutMs.SetValue(_audioFingerprintManager, newTimeout);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return LibraryScopeResolver.IsPathInScope(item.Path, LibraryPathsInScope);
        }

        public void UpdateLibraryPathsInScope()
        {
            // The marker scope is the one place where "favorites only" still means "every library",
            // because a favorite can live in any of them.
            LibraryPathsInScope = LibraryScopeResolver.ResolveScopePaths(_libraryManager,
                Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope,
                f => f.LibraryOptions.EnableMarkerDetection &&
                     (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null),
                includeAllLibrariesWhenFavoritesOnly: true);
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u,
                    IsFavorite = true,
                    IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                }))
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();

            var result = expanded.GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();

            return result;
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var libraryIds = LibraryScopeResolver.ParseScopeIds(
                Plugin.Instance.GetPluginOptions().IntroSkipOptions.LibraryScope);

            var includeFavorites = LibraryScopeResolver.IncludesFavorites(libraryIds);

            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites)
                {
                    resultItems = Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>()
                        .ToList();
                }

                if (!LibraryScopeResolver.IsFavoritesOnly(libraryIds))
                {
                    var filteredItems = LibraryScopeResolver.FilterInScope(incomingItems,
                        i => i.ContainingFolderPath, LibraryPathsInScope);
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = resultItems.Where(i => isModSupported || !i.IsShortcut)
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First()).ToList();

            var blacklist = FingerprintBlacklist.FromOptions();
            resultItems = resultItems.Where(i => !blacklist.IsExcluded(i)).ToList();

            var unprocessedItems = FilterUnprocessed(resultItems);

            return unprocessedItems;
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

            var results = new List<Episode>();

            foreach (var item in items)
            {
                if (Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture))
                {
                    results.Add(item);
                }
                else if (IsExtractNeeded(item))
                {
                    results.Add(item);
                }
            }

            _logger?.Info("IntroFingerprintExtract - Number of items: " + results.Count);

            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            return !Plugin.ChapterApi.HasIntro(item) &&
                   string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        private const int ItemBatchSize = 500;

        private long[] GetItemIds(InternalItemsQuery query)
        {
            try
            {
                return _libraryManager.GetInternalItemIds(query) ?? Array.Empty<long>();
            }
            catch (Exception e)
            {
                _logger?.Debug("Fingerprint item id query failed: " + e.Message);
                return Array.Empty<long>();
            }
        }

        private IEnumerable<BaseItem[]> GetItemBatches(IReadOnlyList<long> ids)
        {
            for (var offset = 0; offset < ids.Count; offset += ItemBatchSize)
            {
                var count = Math.Min(ItemBatchSize, ids.Count - offset);
                var batchIds = new long[count];

                for (var i = 0; i < count; i++)
                {
                    batchIds[i] = ids[offset + i];
                }

                var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = batchIds });

                yield return items ?? Array.Empty<BaseItem>();
            }
        }

        private InternalItemsQuery CreateIntroPreExtractQuery(string markerEnabledLibraryScope)
        {
            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                HasPath = true,
                HasAudioStream = false,
                HasIntroDetectionFailure = false,
            };

            if (LibraryScopeResolver.IncludesFavorites(
                    LibraryScopeResolver.ParseScopeIds(markerEnabledLibraryScope)))
            {
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            return itemsFingerprintQuery;
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope;

            var itemsFingerprintQuery = CreateIntroPreExtractQuery(markerEnabledLibraryScope);

            var isModSupported = Plugin.Instance.IsModSupported;
            var blacklist = FingerprintBlacklist.FromOptions();
            var queryItemIds = GetItemIds(itemsFingerprintQuery);
            var results = new List<Episode>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in GetItemBatches(queryItemIds))
            {
                foreach (var item in batch)
                {
                    if (!isModSupported && item.IsShortcut) continue;
                    if (!(item is Episode episode)) continue;
                    if (blacklist.IsExcluded(episode)) continue;
                    if (!seenPaths.Add(episode.Path)) continue;

                    results.Add(episode);
                }
            }

            _logger?.Info("IntroFingerprintExtract - PreExtract stage counts: query=" + queryItemIds.Length +
                          ", filtered=" + results.Count +
                          ", isModSupported=" + isModSupported +
                          ", favoriteScope=" + LibraryScopeResolver.IncludesFavorites(
                              LibraryScopeResolver.ParseScopeIds(markerEnabledLibraryScope)));

            return results;
        }

        private InternalItemsQuery CreateIntroFingerprintQuery()
        {
            var libraryIds = LibraryScopeResolver.ParseScopeIds(Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope);
            var librariesWithMarkerDetection = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            f.LibraryOptions.EnableMarkerDetection)
                .ToList();
            var librariesSelected = librariesWithMarkerDetection.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger?.Info("IntroFingerprintExtract - LibraryScope: " + (!librariesWithMarkerDetection.Any()
                ? "NONE"
                : string.Join(", ",
                    (LibraryScopeResolver.IncludesFavorites(libraryIds)
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));
            _logger?.Info("IntroFingerprintExtract - MarkerEnabledLibraryScope(raw): " +
                         (string.IsNullOrEmpty(Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope)
                             ? "<empty>"
                             : Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope));
            _logger?.Info("IntroFingerprintExtract - LibraryPathsInScope(" + LibraryPathsInScope.Count + "): " +
                         (LibraryPathsInScope.Any() ? string.Join(" | ", LibraryPathsInScope) : "<empty>"));

            var introSkipOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;
            _logger?.Info("Intro Detection Fingerprint Length (Minutes): " +
                         introSkipOptions.IntroDetectionFingerprintMinutes);

            var effectiveLibraryMinutes = librariesSelected
                .Select(l => introSkipOptions.GetIntroDetectionFingerprintMinutes(l.ItemId)).ToList();

            if (effectiveLibraryMinutes.Distinct().Skip(1).Any())
            {
                _logger?.Info("Intro Detection Fingerprint Length Per Library: " + string.Join(", ",
                    librariesSelected.Select(l =>
                        l.Name + "=" + introSkipOptions.GetIntroDetectionFingerprintMinutes(l.ItemId))));
            }

            var minIntroDetectionFingerprintMinutes = effectiveLibraryMinutes
                .DefaultIfEmpty(introSkipOptions.IntroDetectionFingerprintMinutes).Min();

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                WithoutChapterMarkers = new[] { MarkerType.IntroStart },
                MinRunTimeTicks = TimeSpan.FromMinutes(minIntroDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };

            if (LibraryScopeResolver.IsFavoritesOnly(libraryIds))
            {
                var favoriteSeasonParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
                _logger?.Info("IntroFingerprintExtract - FavoriteSeasonParentIds: " + favoriteSeasonParentIds.Length);
                itemsFingerprintQuery.ParentIds = favoriteSeasonParentIds;
            }
            else
            {
                if (LibraryPathsInScope.Any())
                {
                    itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                }
            }

            return itemsFingerprintQuery;
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var itemsFingerprintQuery = CreateIntroFingerprintQuery();

            var isModSupported = Plugin.Instance.IsModSupported;
            var blacklist = FingerprintBlacklist.FromOptions();
            var queryItemIds = GetItemIds(itemsFingerprintQuery);
            var afterShortcutCount = 0;
            var afterBlacklistCount = 0;
            var items = new List<Episode>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fingerprintMinutesLookup = new IntroDetectionFingerprintMinutesLookup(_libraryManager,
                Plugin.Instance.GetPluginOptions().IntroSkipOptions);

            foreach (var batch in GetItemBatches(queryItemIds))
            {
                foreach (var item in batch)
                {
                    if (!isModSupported && item.IsShortcut) continue;

                    afterShortcutCount++;

                    if (!(item is Episode episode)) continue;
                    if (blacklist.IsExcluded(episode)) continue;

                    afterBlacklistCount++;

                    if (episode.RunTimeTicks <
                        TimeSpan.FromMinutes(GetIntroDetectionFingerprintMinutesForItem(episode,
                            fingerprintMinutesLookup)).Ticks)
                    {
                        continue;
                    }

                    if (!seenPaths.Add(episode.Path)) continue;

                    items.Add(episode);
                }
            }

            _logger?.Info("IntroFingerprintExtract - Stage counts: query=" + queryItemIds.Length +
                          ", modOrNonShortcut=" + afterShortcutCount +
                          ", blacklist=" + afterBlacklistCount +
                          ", runtime=" + items.Count +
                          ", isModSupported=" + isModSupported);

            return items;
        }

        /// <summary>
        /// Runs both intro-extraction selection passes with a single materialisation pass. The
        /// two id queries are cheap; this avoids loading the full episode set twice. Items are
        /// split back into the pre/post lists in memory, preserving each pass's own filters.
        /// </summary>
        public (List<Episode> Pre, List<Episode> Post) FetchIntroTaskItems()
        {
            var markerEnabledLibraryScope =
                Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope;

            var preQuery = CreateIntroPreExtractQuery(markerEnabledLibraryScope);
            var postQuery = CreateIntroFingerprintQuery();

            var preIds = GetItemIds(preQuery);
            var postIds = GetItemIds(postQuery);

            var preSet = new HashSet<long>(preIds);
            var postSet = new HashSet<long>(postIds);

            var allIds = new HashSet<long>(preSet);
            allIds.UnionWith(postSet);

            var isModSupported = Plugin.Instance.IsModSupported;
            var blacklist = FingerprintBlacklist.FromOptions();

            var pre = new List<Episode>();
            var post = new List<Episode>();
            var prePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var postPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var postAfterShortcut = 0;
            var postAfterBlacklist = 0;
            var fingerprintMinutesLookup = new IntroDetectionFingerprintMinutesLookup(_libraryManager,
                Plugin.Instance.GetPluginOptions().IntroSkipOptions);

            foreach (var batch in GetItemBatches(allIds.ToList()))
            {
                foreach (var item in batch)
                {
                    var id = item.InternalId;

                    if (preSet.Contains(id) && (isModSupported || !item.IsShortcut) &&
                        item is Episode preEpisode && !blacklist.IsExcluded(preEpisode) &&
                        prePaths.Add(preEpisode.Path))
                    {
                        pre.Add(preEpisode);
                    }

                    if (!postSet.Contains(id)) continue;
                    if (!isModSupported && item.IsShortcut) continue;

                    postAfterShortcut++;

                    if (!(item is Episode episode)) continue;
                    if (blacklist.IsExcluded(episode)) continue;

                    postAfterBlacklist++;

                    if (episode.RunTimeTicks <
                        TimeSpan.FromMinutes(GetIntroDetectionFingerprintMinutesForItem(episode,
                            fingerprintMinutesLookup)).Ticks)
                    {
                        continue;
                    }

                    if (!postPaths.Add(episode.Path)) continue;

                    post.Add(episode);
                }
            }

            _logger?.Info("IntroFingerprintExtract - PreExtract stage counts: query=" + preIds.Length +
                          ", filtered=" + pre.Count +
                          ", isModSupported=" + isModSupported +
                          ", favoriteScope=" + LibraryScopeResolver.IncludesFavorites(
                              LibraryScopeResolver.ParseScopeIds(markerEnabledLibraryScope)));
            _logger?.Info("IntroFingerprintExtract - Stage counts: query=" + postIds.Length +
                          ", modOrNonShortcut=" + postAfterShortcut +
                          ", blacklist=" + postAfterBlacklist +
                          ", runtime=" + post.Count +
                          ", isModSupported=" + isModSupported);

            return (pre, post);
        }

        /// <summary>
        /// Answers "how many minutes of fingerprint does this item need" for a whole run.
        ///
        /// The per-library overrides are read once (<c>Overrides()</c> re-enumerated the option
        /// collection on every single episode before), and the library of an item is looked up at
        /// most once per parent instead of once per episode: every episode of a season resolves to
        /// the same <see cref="ILibraryManager.GetCollectionFolders"/> result.
        ///
        /// The fallbacks mirror the previous inline code exactly, including the fact that only the
        /// library-aware lookup clamps to [2, 20]:
        ///  - no override configured (the common case)      → the raw global option value
        ///  - override configured, no collection folder     → the raw global option value
        ///  - override configured, library known            → GetIntroDetectionFingerprintMinutes()
        /// </summary>
        private sealed class IntroDetectionFingerprintMinutesLookup
        {
            private readonly ILibraryManager _libraryManager;
            private readonly IntroSkipOptions _introSkipOptions;
            private readonly Dictionary<string, int> _minutesByLibraryId;
            private readonly Dictionary<long, int> _minutesByParentId = new Dictionary<long, int>();

            public IntroDetectionFingerprintMinutesLookup(ILibraryManager libraryManager,
                IntroSkipOptions introSkipOptions)
            {
                _libraryManager = libraryManager;
                _introSkipOptions = introSkipOptions;

                if (introSkipOptions.LibraryIntroDetectionFingerprintOptions?.Overrides().Any() != true) return;

                // Same id pairing as UpdateLibraryIntroDetectionFingerprintLength / the container
                // library lookup: the assignment option id is the virtual folder's ItemId, which is
                // the collection folder's InternalId.
                _minutesByLibraryId = libraryManager.GetVirtualFolders()
                    .GroupBy(f => f.ItemId)
                    .ToDictionary(g => g.Key,
                        g => introSkipOptions.GetIntroDetectionFingerprintMinutes(g.Key));
            }

            public int Get(BaseItem item)
            {
                if (_minutesByLibraryId is null) return _introSkipOptions.IntroDetectionFingerprintMinutes;

                var parentId = item.ParentId;

                if (parentId != 0 && _minutesByParentId.TryGetValue(parentId, out var cached))
                {
                    return cached;
                }

                var minutes = _introSkipOptions.IntroDetectionFingerprintMinutes;

                var collectionFolder = _libraryManager.GetCollectionFolders(item).FirstOrDefault();

                if (collectionFolder != null)
                {
                    minutes = _minutesByLibraryId.TryGetValue(collectionFolder.InternalId.ToString(), out var value)
                        ? value
                        : _introSkipOptions.GetIntroDetectionFingerprintMinutes(
                            collectionFolder.InternalId.ToString());
                }

                if (parentId != 0) _minutesByParentId[parentId] = minutes;

                return minutes;
            }
        }

        /// <summary>
        /// Single item convenience overload. Loops share one <paramref name="lookup"/> instead, so
        /// that the per-library overrides and the per-item library lookup are paid for once.
        /// </summary>
        private int GetIntroDetectionFingerprintMinutesForItem(BaseItem item,
            IntroDetectionFingerprintMinutesLookup lookup = null)
        {
            lookup = lookup ?? new IntroDetectionFingerprintMinutesLookup(_libraryManager,
                Plugin.Instance.GetPluginOptions().IntroSkipOptions);

            return lookup.Get(item);
        }

        public void UpdateLibraryIntroDetectionFingerprintLength()
        {
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .ToList();
            
            var introSkipOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;

            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;
                var libraryLength = introSkipOptions.GetIntroDetectionFingerprintMinutes(library.ItemId);

                if (options.IntroDetectionFingerprintLength != libraryLength &&
                    long.TryParse(library.ItemId, out var itemId))
                {
                    options.IntroDetectionFingerprintLength = libraryLength;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        private async Task TryDistributeSeasonFingerprintsAsync(IEnumerable<Episode> episodes,
            LibraryOptions libraryOptions, CancellationToken cancellationToken)
        {
            if (Plugin.DistributedExtractApi is null ||
                !DistributedExtractApi.IsFingerprintDistributionEnabled) return;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await Plugin.DistributedExtractApi
                        .TryCreateTitleFingerprintAsync(episode, libraryOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _logger?.Debug("IntroFingerprintDistributed - Failed: " + episode.Name + " - " +
                                   episode.Path + " - " + e.Message);
                }
            }
        }

#nullable enable
        /// <summary>
        /// Re-detects the intro and credits markers of one season on demand. The markers that were
        /// written for the season before are removed first, otherwise both the IntroDb lookup and
        /// Emby's fingerprint detection would skip every episode that already carries a marker.
        /// After the cleanup the normal pipeline (<see cref="UpdateIntroMarkerForSeason"/>) runs
        /// unchanged, so IntroDb timestamps are applied first and only the remaining episodes are
        /// fingerprinted.
        /// </summary>
        public async Task RedetectIntroForSeason(Season season, CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            if (FingerprintBlacklist.FromOptions().IsExcluded(season))
            {
                _logger.Info("FingerprintBlacklist - Skipped re-detect: " + season.Name + " - " + season.Path);
                return;
            }

            _logger?.Info("IntroRedetect - " + season.SeriesName + " - " + season.Name + " - " + season.Path);

            ClearSeasonIntroCreditsMarkers(season, cancellationToken);

            await UpdateIntroMarkerForSeason(season, cancellationToken, progress).ConfigureAwait(false);
        }

        /// <summary>
        /// Removes the intro/credits markers of the season that were written before: the ones from
        /// Emby's fingerprint pipeline (they do not carry the #SA suffix) and the ones from IntroDb
        /// or the playback based marker update (#SA). Saving the chapters also clears Emby's
        /// intro detection failure record, so episodes that failed before are retried instead of
        /// being skipped again.
        /// </summary>
        private void ClearSeasonIntroCreditsMarkers(Season season, CancellationToken cancellationToken)
        {
            var episodes = season.GetEpisodes(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            }).Items.OfType<Episode>().ToList();

            var cleared = 0;

            lock (MarkerWriteLock)
            {
                foreach (var episode in episodes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var chapters = _itemRepository.GetChapters(episode);
                    var removed = chapters.RemoveAll(c =>
                        c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd ||
                        c.MarkerType == MarkerType.CreditsStart);

                    // Always save: Emby clears the intro detection failure record on save, which
                    // makes episodes whose earlier detection failed eligible for a retry.
                    _itemRepository.SaveChapters(episode.InternalId, chapters);

                    if (removed > 0)
                    {
                        cleared++;
                    }
                }
            }

            _logger?.Info("IntroRedetect - Cleared markers for " + cleared + " of " + episodes.Count +
                          " episodes in " + season.Name);
        }

        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken,
            IProgress<double>? progress = null)
        {
            if (FingerprintBlacklist.FromOptions().IsExcluded(season))
            {
                _logger.Info("FingerprintBlacklist - Skipped marker update: " + season.Name + " - " + season.Path);
                return;
            }

            // Fingerprint detection compares the episodes of one season with each other. A season
            // with a single episode has no second fingerprint to compare against, so any detected
            // title sequence would be a false positive. IntroDb markers are skipped as well.
            var seasonEpisodeCount = season.GetEpisodes(new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                IncludeItemTypes = new[] { nameof(Episode) },
                HasPath = true,
                MediaTypes = new[] { MediaType.Video }
            }).Items.OfType<Episode>().Count();

            if (seasonEpisodeCount < 2)
            {
                _logger?.Debug("IntroMarkerFilter - Skipped season with fewer than 2 episodes: " +
                               season.Name + " - " + season.Path);
                return;
            }

            var introSkipOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;
            var introDetectionFingerprintMinutes = GetIntroDetectionFingerprintMinutesForItem(season);

            var libraryOptions = _libraryManager.GetLibraryOptions(season);
            var directoryService = new DirectoryService(_logger, _fileSystem);

            var episodeQuery = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                EnableTotalRecordCount = false,
                MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks,
                HasIntroDetectionFailure = false,
                HasAudioStream = true
            };
            var allEpisodes = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToArray();

            // Known IntroDb timestamps are written first, so the expensive fingerprint detection
            // below only runs for the episodes that are still missing markers.
            if (Plugin.IntroDbApi != null)
            {
                await Plugin.IntroDbApi.ApplyToSeasonAsync(season, allEpisodes, cancellationToken)
                    .ConfigureAwait(false);
            }

            episodeQuery.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var episodesWithoutMarkers = season.GetEpisodes(episodeQuery).Items.OfType<Episode>().ToList();

            // Emby's season pipeline below creates the missing fingerprints itself. Pre-create
            // them through worker nodes so the local chromaprint work is skipped; the result is
            // the same .fp file Emby would have written, so the pipeline is otherwise unchanged.
            await TryDistributeSeasonFingerprintsAsync(allEpisodes, libraryOptions, cancellationToken)
                .ConfigureAwait(false);

            var seasonFingerprintInfo = await GetAllFingerprintFilesForSeason(season,
                allEpisodes, libraryOptions, directoryService, cancellationToken).ConfigureAwait(false);

            double total = episodesWithoutMarkers.Count;
            var index = 0;

            // Seasons are processed in parallel, but chapter marker writes must stay serialized
            // to avoid concurrent writes to the same SQLite database.
            lock (MarkerWriteLock)
            {
                foreach (var episode in episodesWithoutMarkers)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    UpdateSequencesForSeason(season, seasonFingerprintInfo, episode, libraryOptions,
                        directoryService, cancellationToken);

                    // The fingerprint pipeline writes the markers directly through the item
                    // repository, so the configurable bounds are applied right after it runs.
                    IntroMarkerHelper.NormalizeEpisodeMarkers(episode, _itemRepository, introSkipOptions,
                        _logger);

                    index++;
                    progress?.Report(index / total);
                }
            }

            progress?.Report(1.0);
        }
#nullable restore
    }
}
