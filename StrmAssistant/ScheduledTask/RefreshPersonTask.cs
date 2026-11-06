using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;

namespace StrmAssistant.ScheduledTask
{
    public class RefreshPersonTask : IScheduledTask
    {
        private const int MainCastPerItemLimit = 10;
        private const int MainCastMinimumAppearances = 2;

        private ILogger _logger => Plugin.Instance?.Logger;
        private readonly ILibraryManager _libraryManager;

        private static readonly HashSet<string> ProviderIdCheckKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tmdb", "imdb", "tvdb" };

        public RefreshPersonTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("RefreshPerson - Scheduled Task Execute");
            await Task.Yield();
            progress.Report(0);

            var options = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions;
            var refreshPersonMode = options.GetEffectiveRefreshPersonMode();
            var isFullRefresh = refreshPersonMode == RefreshPersonModeOption.Manual;
            var refreshScope = options.RefreshPersonScope;
            var skipLockedPeople = options.RefreshPersonSkipLockedPeople;
            var skipMissingTmdbId = options.RefreshPersonSkipMissingTmdbId;
            var skipRecentlyRefreshed = options.RefreshPersonSkipRecentlyRefreshed;
            var recentRefreshDays = Math.Max(1, options.RefreshPersonRecentRefreshDays);
            var maxConcurrentCount = Math.Max(1, Math.Min(4, options.RefreshPersonMaxConcurrentCount));
            var requestIntervalMs = Math.Max(0, Math.Min(10000, options.RefreshPersonRequestIntervalMs));
            var requireChineseLanguage = options.RefreshPersonRequireChineseLanguage;

            _logger?.Info("Refresh Person Mode: " + refreshPersonMode);
            _logger?.Info("Refresh Person Scope: " + refreshScope);
            _logger?.Info("Refresh Person Max Concurrent Count: " + maxConcurrentCount);
            _logger?.Info("Refresh Person Request Interval (ms): " + requestIntervalMs);
            _logger?.Info("Refresh Person Skip Locked People: " + skipLockedPeople);
            _logger?.Info("Refresh Person Skip Missing TMDB Id: " + skipMissingTmdbId);
            _logger?.Info("Refresh Person Skip Recently Refreshed: " + skipRecentlyRefreshed + " (" +
                          recentRefreshDays + " days)");
            _logger?.Info("Refresh Person Require Chinese Server Language: " + requireChineseLanguage);

            if (refreshPersonMode == RefreshPersonModeOption.Off)
            {
                progress.Report(100.0);
                _logger?.Info("RefreshPerson - Scheduled Task Disabled");
                return;
            }

            var serverPreferredMetadataLanguage = Plugin.MetadataApi.GetServerPreferredMetadataLanguage();
            _logger?.Info("Server Preferred Metadata Language: " + serverPreferredMetadataLanguage);
            var isServerPreferZh = IsChineseLanguageCode(serverPreferredMetadataLanguage);

            if (requireChineseLanguage && !isServerPreferZh)
            {
                if (refreshPersonMode == RefreshPersonModeOption.Auto)
                {
                    progress.Report(100.0);
                    _ = Plugin.NotificationApi.SendMessageToAdmins(
                        $"[{Resources.PluginOptions_EditorTitle_Strm_Assistant}] {Resources.ServerPreferredMetadataLanguageIsNotZh}",
                        10000);
                    _logger?.Warn("Server Preferred Metadata Language is not set to Chinese.");
                    _logger?.Warn("RefreshPerson - Scheduled Task Aborted");
                    return;
                }

                _logger?.Warn(
                    "Server Preferred Metadata Language is not set to Chinese. Manual mode continues.");
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-recentRefreshDays);
            var requestRateLimiter = new RequestRateLimiter(requestIntervalMs);
            var mainCastLookup = refreshScope == RefreshPersonScopeOption.MainCast
                ? BuildMainCastLookup(cancellationToken)
                : null;

            var personItems = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Person) }
                }, cancellationToken)
                .Cast<Person>()
                .ToList();
            _logger?.Info("RefreshPerson - Number of Persons Before: " + personItems.Count);

            var duplicatePersonItems = personItems
                .Where(item => !item.IsLocked && item.ProviderIds != null)
                .SelectMany(item => item.ProviderIds
                    .Where(kvp => ProviderIdCheckKeys.Contains(kvp.Key))
                    .Select(kvp => new { kvp.Key, kvp.Value, item }))
                .GroupBy(kvp => new { kvp.Key, kvp.Value })
                .Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(g => g.item))
                .GroupBy(i => i.InternalId)
                .Select(g => g.First())
                .ToList();

            if (duplicatePersonItems.Count > 0)
            {
                foreach (var dupItem in duplicatePersonItems)
                {
                    _logger?.Info($"RefreshPerson - Duplicate Person: {dupItem.Name}");
                    var relatedItems = _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        PersonIds = new[] { dupItem.InternalId },
                        Recursive = true,
                        IncludeItemTypes = new[]
                            { nameof(Movie), nameof(Series), nameof(Episode), nameof(Video), nameof(Trailer) }
                    });
                    foreach (var relatedItem in relatedItems)
                    {
                        _logger?.Info(
                            $"RefreshPerson - Deleting duplicate person {dupItem.Name} related to {relatedItem.Path}");
                    }
                }

                try
                {
                    _libraryManager.DeleteItems(duplicatePersonItems.Select(i => i.InternalId).ToArray());
                }
                catch (Exception e)
                {
                    _logger?.Error("RefreshPerson - Failed to delete duplicate persons: " + e.Message);
                }
            }
            _logger?.Info("RefreshPerson - Number of Duplicate Persons Deleted: " + duplicatePersonItems.Count);

            var deletedIds = new HashSet<long>(duplicatePersonItems.Select(i => i.InternalId));
            var candidates = personItems
                .Where(item => !deletedIds.Contains(item.InternalId))
                .ToList();

            double total = candidates.Count;
            var current = 0;
            var processedCount = 0;
            var succeededCount = 0;
            var skippedCount = 0;
            var failedCount = 0;
            var cancelledCount = 0;
            var failedNoIdCount = 0;
            var failedRequestCount = 0;
            var failedNoChineseDataCount = 0;
            var skippedLockedCount = 0;
            var skippedNoTmdbIdCount = 0;
            var skippedRecentCount = 0;
            var skippedScopeCount = 0;
            var skippedLockedFieldCount = 0;

            void ReportProgress()
            {
                var currentCount = Interlocked.Increment(ref current);
                if (total > 0)
                {
                    progress.Report(Math.Min(100.0, currentCount / total * 100));
                }
            }

            IsRunning = true;

            using var concurrencySemaphore = new SemaphoreSlim(maxConcurrentCount, maxConcurrentCount);
            var tasks = new List<Task>();

            try
            {
                foreach (var personItem in candidates)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger?.Info("RefreshPerson - Scheduled Task Cancelled");
                        break;
                    }

                    var taskItem = personItem;
                    string skipReason;
                    bool metadataRefreshSkip;
                    bool imageRefreshSkip;

                    if (skipLockedPeople && taskItem.IsLocked)
                    {
                        skipReason = "Locked";
                        metadataRefreshSkip = true;
                        imageRefreshSkip = true;
                    }
                    else if (skipMissingTmdbId && !taskItem.HasProviderId(MetadataProviders.Tmdb))
                    {
                        skipReason = "NoTmdbId";
                        metadataRefreshSkip = true;
                        imageRefreshSkip = true;
                    }
                    else if (refreshScope == RefreshPersonScopeOption.MissingChineseName &&
                             IsChinese(taskItem.Name))
                    {
                        skipReason = "Scope";
                        metadataRefreshSkip = true;
                        imageRefreshSkip = true;
                    }
                    else if (refreshScope == RefreshPersonScopeOption.MainCast &&
                             !mainCastLookup.IsMainCast(taskItem))
                    {
                        skipReason = "Scope";
                        metadataRefreshSkip = true;
                        imageRefreshSkip = true;
                    }
                    else
                    {
                        skipReason = null;

                        if (refreshScope == RefreshPersonScopeOption.FollowRefreshMode)
                        {
                            metadataRefreshSkip =
                                (taskItem.IsFieldLocked(MetadataFields.Name) &&
                                 taskItem.IsFieldLocked(MetadataFields.Overview)) ||
                                (!isFullRefresh && skipRecentlyRefreshed && IsChinese(taskItem.Name) &&
                                 IsChinese(taskItem.Overview) && taskItem.DateLastSaved >= cutoff);
                        }
                        else
                        {
                            metadataRefreshSkip =
                                taskItem.IsFieldLocked(MetadataFields.Name) &&
                                taskItem.IsFieldLocked(MetadataFields.Overview);

                            if (refreshScope == RefreshPersonScopeOption.MissingChineseName &&
                                taskItem.IsFieldLocked(MetadataFields.Name))
                            {
                                metadataRefreshSkip = true;
                            }

                            if (skipRecentlyRefreshed && !isFullRefresh &&
                                GetLastTouched(taskItem) >= cutoff)
                            {
                                skipReason = "RecentlyRefreshed";
                                metadataRefreshSkip = true;
                                imageRefreshSkip = true;
                            }
                        }

                        imageRefreshSkip = skipReason != null ||
                                           taskItem.HasImage(ImageType.Primary) ||
                                           (!isFullRefresh && skipRecentlyRefreshed &&
                                            taskItem.DateLastRefreshed >= cutoff);

                        if (skipReason == null && metadataRefreshSkip && imageRefreshSkip)
                        {
                            skipReason = "LockedField";
                        }
                    }

                    if (skipReason != null)
                    {
                        Interlocked.Increment(ref skippedCount);
                        switch (skipReason)
                        {
                            case "Locked":
                                Interlocked.Increment(ref skippedLockedCount);
                                break;
                            case "NoTmdbId":
                                Interlocked.Increment(ref skippedNoTmdbIdCount);
                                break;
                            case "RecentlyRefreshed":
                                Interlocked.Increment(ref skippedRecentCount);
                                break;
                            case "Scope":
                                Interlocked.Increment(ref skippedScopeCount);
                                break;
                            case "LockedField":
                                Interlocked.Increment(ref skippedLockedFieldCount);
                                break;
                        }

                        ReportProgress();
                        _logger?.Debug("RefreshPerson - Skipped (" + skipReason + "): " + taskItem.Name);
                        continue;
                    }

                    try
                    {
                        await concurrencySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.Info("RefreshPerson - Scheduled Task Cancelled");
                        break;
                    }

                    var task = Task.Run(async () =>
                    {
                        var didWork = false;
                        var wasCancelled = false;
                        var failureReason = RefreshFailureReason.None;

                        try
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var refreshOptions = Plugin.MetadataApi.GetMetadataFullRefreshOptions();
                            var hasTmdbId = taskItem.HasProviderId(MetadataProviders.Tmdb);

                            if (!metadataRefreshSkip)
                            {
                                MetadataResult<Person> result = null;

                                try
                                {
                                    await requestRateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                                    result = await Plugin.MetadataApi
                                        .GetPersonMetadataFromMovieDb(taskItem, serverPreferredMetadataLanguage,
                                            refreshOptions.DirectoryService, cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (Exception e)
                                {
                                    failureReason = RefreshFailureReason.RequestFailed;
                                    _logger?.Error("RefreshPerson - Request Failed: " + taskItem.Name);
                                    _logger?.Error(e.Message);
                                    _logger?.Debug(e.StackTrace);
                                }

                                if (failureReason == RefreshFailureReason.None)
                                {
                                    if (result?.Item != null)
                                    {
                                        var newName = result.Item.Name;
                                        var newOverview = result.Item.Overview;
                                        var hasChineseName = IsChinese(newName);
                                        var hasChineseOverview = IsChinese(newOverview);

                                        if (!hasChineseName && !hasChineseOverview)
                                        {
                                            failureReason = RefreshFailureReason.NoChineseData;
                                            _logger?.Debug("RefreshPerson - No Chinese Data: " + taskItem.Name);
                                        }
                                        else
                                        {
                                            var metadataApplied = false;

                                            if (!taskItem.IsFieldLocked(MetadataFields.Name) &&
                                                !string.IsNullOrEmpty(newName))
                                            {
                                                taskItem.Name = Plugin.MetadataApi.ProcessPersonInfo(newName, true);
                                                metadataApplied = true;
                                            }

                                            if (!taskItem.IsFieldLocked(MetadataFields.Overview) &&
                                                !string.IsNullOrEmpty(newOverview))
                                            {
                                                taskItem.Overview =
                                                    Plugin.MetadataApi.ProcessPersonInfo(newOverview, false);
                                                metadataApplied = true;
                                            }

                                            if (metadataApplied)
                                            {
                                                _libraryManager.UpdateItems(new List<BaseItem> { taskItem }, null,
                                                    ItemUpdateType.MetadataDownload, true, false, null,
                                                    CancellationToken.None);
                                                didWork = true;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        failureReason = hasTmdbId
                                            ? RefreshFailureReason.RequestFailed
                                            : RefreshFailureReason.NoId;
                                    }
                                }
                            }

                            if (!imageRefreshSkip)
                            {
                                await requestRateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
                                await taskItem.RefreshMetadata(refreshOptions, cancellationToken)
                                    .ConfigureAwait(false);
                                didWork = true;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            wasCancelled = true;
                            _logger?.Info("RefreshPerson - Item Cancelled: " + taskItem.Name);
                        }
                        catch (Exception e)
                        {
                            failureReason = RefreshFailureReason.RequestFailed;
                            _logger?.Error("RefreshPerson - Item Failed: " + taskItem.Name);
                            _logger?.Error(e.Message);
                            _logger?.Debug(e.StackTrace);
                        }
                        finally
                        {
                            Interlocked.Increment(ref processedCount);

                            if (didWork)
                            {
                                Interlocked.Increment(ref succeededCount);
                            }
                            else if (wasCancelled)
                            {
                                Interlocked.Increment(ref cancelledCount);
                            }
                            else
                            {
                                Interlocked.Increment(ref failedCount);

                                switch (failureReason)
                                {
                                    case RefreshFailureReason.NoId:
                                        Interlocked.Increment(ref failedNoIdCount);
                                        _logger?.Info("RefreshPerson - Item Failed (No TMDB Id): " +
                                                      taskItem.Name);
                                        break;
                                    case RefreshFailureReason.RequestFailed:
                                        Interlocked.Increment(ref failedRequestCount);
                                        _logger?.Info("RefreshPerson - Item Failed (Request Failed): " +
                                                      taskItem.Name);
                                        break;
                                    case RefreshFailureReason.NoChineseData:
                                        Interlocked.Increment(ref failedNoChineseDataCount);
                                        _logger?.Info("RefreshPerson - Item Failed (No Chinese Data): " +
                                                      taskItem.Name);
                                        break;
                                }
                            }

                            concurrencySemaphore.Release();
                            ReportProgress();
                            _logger?.Info("RefreshPerson - Task " + Volatile.Read(ref current) + "/" + total +
                                          " - " + taskItem.Name);
                        }
                    });

                    tasks.Add(task);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                IsRunning = false;
            }

            progress.Report(100.0);
            _logger?.Info(
                "RefreshPerson - Statistics: " +
                $"Total={total}, Processed={processedCount}, Succeeded={succeededCount}, " +
                $"Skipped={skippedCount}, Failed={failedCount}, Cancelled={cancelledCount}");
            _logger?.Info(
                "RefreshPerson - Failure Reasons: " +
                $"NoId={failedNoIdCount}, RequestFailed={failedRequestCount}, " +
                $"NoChineseData={failedNoChineseDataCount}");
            _logger?.Info(
                "RefreshPerson - Skip Details: " +
                $"Locked={skippedLockedCount}, NoTmdbId={skippedNoTmdbIdCount}, " +
                $"RecentlyRefreshed={skippedRecentCount}, Scope={skippedScopeCount}, " +
                $"LockedField={skippedLockedFieldCount}");
            _logger?.Info("RefreshPerson - Scheduled Task Complete");
        }

        private static DateTimeOffset GetLastTouched(BaseItem item)
        {
            return item.DateLastSaved > item.DateLastRefreshed
                ? item.DateLastSaved
                : item.DateLastRefreshed;
        }

        private MainCastLookup BuildMainCastLookup(CancellationToken cancellationToken)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[]
                    { nameof(Movie), nameof(Series), nameof(Episode), nameof(Video), nameof(Trailer) }
            }, cancellationToken);

            var appearances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var mainCastKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!item.SupportsPeople) continue;

                List<PersonInfo> people;
                try
                {
                    people = _libraryManager.GetItemPeople(item);
                }
                catch (Exception e)
                {
                    _logger?.Debug("RefreshPerson - Main cast lookup failed for " + item.Name + ": " + e.Message);
                    continue;
                }

                if (people == null) continue;

                var actorIndex = 0;
                foreach (var person in people)
                {
                    if (person == null || person.Type != PersonType.Actor ||
                        string.IsNullOrWhiteSpace(person.Name))
                    {
                        continue;
                    }

                    var key = GetPersonKey(person.Id, person.Name);
                    appearances.TryGetValue(key, out var count);
                    appearances[key] = count + 1;

                    if (actorIndex < MainCastPerItemLimit)
                    {
                        mainCastKeys.Add(key);
                    }

                    actorIndex++;
                }
            }

            foreach (var pair in appearances)
            {
                if (pair.Value >= MainCastMinimumAppearances)
                {
                    mainCastKeys.Add(pair.Key);
                }
            }

            var lookup = new MainCastLookup();
            foreach (var key in mainCastKeys)
            {
                if (key.StartsWith("id:", StringComparison.Ordinal))
                {
                    if (long.TryParse(key.Substring(3), out var id)) lookup.Ids.Add(id);
                }
                else if (key.StartsWith("name:", StringComparison.Ordinal))
                {
                    lookup.Names.Add(key.Substring(5));
                }
            }

            _logger?.Info("RefreshPerson - Main Cast Persons: " + mainCastKeys.Count + " from " + items.Length +
                          " items");

            return lookup;
        }

        private static string GetPersonKey(long id, string name)
        {
            return id != 0
                ? "id:" + id
                : "name:" + (name ?? string.Empty).Trim();
        }

        private sealed class MainCastLookup
        {
            public HashSet<long> Ids { get; } = new HashSet<long>();
            public HashSet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool IsMainCast(Person person)
            {
                if (person == null) return false;
                if (person.InternalId != 0 && Ids.Contains(person.InternalId)) return true;

                return !string.IsNullOrWhiteSpace(person.Name) && Names.Contains(person.Name.Trim());
            }
        }

        private sealed class RequestRateLimiter
        {
            private readonly int _intervalMs;
            private readonly object _sync = new object();
            private DateTimeOffset _nextSlotUtc = DateTimeOffset.MinValue;

            public RequestRateLimiter(int intervalMs)
            {
                _intervalMs = intervalMs;
            }

            public async Task WaitAsync(CancellationToken cancellationToken)
            {
                if (_intervalMs <= 0) return;

                int delayMs;
                lock (_sync)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (_nextSlotUtc < now) _nextSlotUtc = now;

                    delayMs = (int)Math.Ceiling((_nextSlotUtc - now).TotalMilliseconds);
                    _nextSlotUtc = _nextSlotUtc.AddMilliseconds(_intervalMs);
                }

                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private enum RefreshFailureReason
        {
            None,
            NoId,
            RequestFailed,
            NoChineseData
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "RefreshPersonTask";

        public string Description => Resources.ResourceManager.GetString(
            "RefreshPersonTask_Description_Refreshes_and_repairs_Chinese_actors", Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString("RefreshPersonTask_Name_Refresh_Chinese_Actor",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public static bool IsRunning { get; private set; }
    }
}
