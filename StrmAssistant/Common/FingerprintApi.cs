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
                if (Plugin.Instance.DebugMode)
                {
                    _logger?.Debug(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
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
                _logger?.Error("IntroFingerprintExtract - Failed to Record Intro Detection Failure: " + item.Name +
                               " - " + item.Path);
                _logger?.Error(e.Message);
                _logger?.Debug(e.StackTrace);
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
                catch
                {
                    // ignored
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
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope()
        {
            var validLibraryIds = GetValidLibraryIds(Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.All(id => id == "-1") ||
                             validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? ls
                    : ls + Path.DirectorySeparatorChar)
                .ToList();
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
            var libraryIds = Plugin.Instance.GetPluginOptions().IntroSkipOptions.LibraryScope?
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds?.Contains("-1") == true;

            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites)
                {
                    resultItems = Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>()
                        .ToList();
                }

                if (libraryIds is null || !libraryIds.Any() || libraryIds.Any(id => id != "-1"))
                {
                    var filteredItems = incomingItems
                        .Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p)))
                        .ToList();
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

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.GetPluginOptions().IntroSkipOptions.MarkerEnabledLibraryScope;

            var itemsFingerprintQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                HasPath = true,
                HasAudioStream = false,
                HasIntroDetectionFailure = false,
            };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
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

            var isModSupported = Plugin.Instance.IsModSupported;
            var blacklist = FingerprintBlacklist.FromOptions();
            var queryItems = _libraryManager.GetItemList(itemsFingerprintQuery);
            var results = queryItems.Where(i => isModSupported || !i.IsShortcut)
                .OfType<Episode>()
                .Where(i => !blacklist.IsExcluded(i))
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            _logger?.Info("IntroFingerprintExtract - PreExtract stage counts: query=" + queryItems.Length +
                          ", filtered=" + results.Count +
                          ", isModSupported=" + isModSupported +
                          ", favoriteScope=" + (!string.IsNullOrEmpty(markerEnabledLibraryScope) &&
                                               markerEnabledLibraryScope.Contains("-1")));

            return results;
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var libraryIds = Plugin.Instance.GetPluginOptions()
                .IntroSkipOptions.MarkerEnabledLibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithMarkerDetection = _libraryManager.GetVirtualFolders()
                .Where(f => (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            f.LibraryOptions.EnableMarkerDetection)
                .ToList();
            var librariesSelected = librariesWithMarkerDetection.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger?.Info("IntroFingerprintExtract - LibraryScope: " + (!librariesWithMarkerDetection.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
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

            if (libraryIds.Length != 0 && libraryIds.All(i => i == "-1"))
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

            var isModSupported = Plugin.Instance.IsModSupported;
            var blacklist = FingerprintBlacklist.FromOptions();
            var queryItems = _libraryManager.GetItemList(itemsFingerprintQuery);
            var afterShortcut = queryItems.Where(i => isModSupported || !i.IsShortcut).ToList();
            var afterBlacklist = afterShortcut.OfType<Episode>().Where(i => !blacklist.IsExcluded(i)).ToList();
            var items = afterBlacklist
                .Where(i => i.RunTimeTicks >= TimeSpan
                    .FromMinutes(GetIntroDetectionFingerprintMinutesForItem(i)).Ticks)
                .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            _logger?.Info("IntroFingerprintExtract - Stage counts: query=" + queryItems.Length +
                          ", modOrNonShortcut=" + afterShortcut.Count +
                          ", blacklist=" + afterBlacklist.Count +
                          ", runtime=" + items.Count +
                          ", isModSupported=" + isModSupported);

            return items;
        }

        private int GetIntroDetectionFingerprintMinutesForItem(BaseItem item)
        {
            var introSkipOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;

            if (introSkipOptions.LibraryIntroDetectionFingerprintOptions?.Overrides().Any() == true)
            {
                var collectionFolder = _libraryManager.GetCollectionFolders(item).FirstOrDefault();

                if (collectionFolder != null)
                {
                    return introSkipOptions.GetIntroDetectionFingerprintMinutes(
                        collectionFolder.InternalId.ToString());
                }
            }

            return introSkipOptions.IntroDetectionFingerprintMinutes;
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
