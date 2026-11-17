using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public static class QueueManager
    {
        private static readonly ILogger Logger;
        private static DateTime _mediaInfoProcessLastRunTime = DateTime.MinValue;
        private static DateTime _introSkipProcessLastRunTime = DateTime.MinValue;
        private static DateTime _fingerprintProcessLastRunTime = DateTime.MinValue;
        private static DateTime _episodeRefreshProcessLastRunTime = DateTime.MinValue;
        private static int _currentMasterMaxConcurrentCount;
        private static int _currentTier2MaxConcurrentCount;
        private static int _currentFingerprintMaxConcurrentCount;

        public static CancellationTokenSource MediaInfoTokenSource;
        public static CancellationTokenSource IntroSkipTokenSource;
        public static CancellationTokenSource FingerprintTokenSource;
        public static CancellationTokenSource EpisodeRefreshTokenSource;
        public static AsyncSemaphore MasterSemaphore;
        public static AsyncSemaphore Tier2Semaphore;
        public static AsyncSemaphore FingerprintSemaphore;
        public static ConcurrentQueue<BaseItem> MediaInfoExtractItemQueue = new ConcurrentQueue<BaseItem>();

        // Item ids that are currently in a media info queue or being processed by its worker.
        // The invariant is: the id stays here for exactly as long as the item is queued or owned
        // by the worker, so the O(1) admission check in TryEnqueueMediaInfo rejects any second
        // enqueue of the same item no matter which path it comes from.
        // Set on enqueue, cleared when the item is processed (or dropped) - never on dequeue.
        public static readonly ConcurrentDictionary<long, byte> PendingMediaInfoItemIds =
            new ConcurrentDictionary<long, byte>();
        public static ConcurrentQueue<Episode> IntroSkipItemQueue = new ConcurrentQueue<Episode>();
        public static ConcurrentQueue<BaseItem> FingerprintItemQueue = new ConcurrentQueue<BaseItem>();
        public static ConcurrentQueue<BaseItem> EpisodeRefreshItemQueue = new ConcurrentQueue<BaseItem>();

        // When favorite catch-up priority is enabled, favorites get their own queues so the
        // catch-up work caused by a favorite is never batched together with the regular
        // catch-up work of newly added items. The workers drain the favorite queue first.
        public static ConcurrentQueue<BaseItem> FavoriteMediaInfoExtractItemQueue =
            new ConcurrentQueue<BaseItem>();
        public static ConcurrentQueue<Episode> FavoriteIntroSkipItemQueue = new ConcurrentQueue<Episode>();
        public static ConcurrentQueue<BaseItem> FavoriteFingerprintItemQueue = new ConcurrentQueue<BaseItem>();
        public static Task MediaInfoProcessTask;
        public static Task FingerprintProcessTask;
        public static Task EpisodeRefreshProcessTask;

        public static bool IsMediaInfoProcessTaskRunning { get; private set; }
        public static bool IsEpisodeRefreshProcessTaskRunning { get; private set; }

        private static readonly object MediaInfoStartLock = new object();

        static QueueManager()
        {
            Logger = Plugin.Instance.Logger;
            _currentMasterMaxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount;
            _currentTier2MaxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.Tier2MaxConcurrentCount;
            _currentFingerprintMaxConcurrentCount =
                Plugin.Instance.GetPluginOptions().GeneralOptions.IntroDetectionMaxConcurrentCount;

            MasterSemaphore = new AsyncSemaphore(_currentMasterMaxConcurrentCount);
            Tier2Semaphore = new AsyncSemaphore(_currentTier2MaxConcurrentCount);
            FingerprintSemaphore = new AsyncSemaphore(_currentFingerprintMaxConcurrentCount);
        }

        public static void Initialize()
        {
            if (MediaInfoProcessTask is null)
            {
                MediaInfoExtractItemQueue.Clear();
                FavoriteMediaInfoExtractItemQueue.Clear();
                PendingMediaInfoItemIds.Clear();
            }

            EnsureMediaInfoProcessTaskStarted();

            if (FingerprintProcessTask is null)
            {
                FingerprintItemQueue.Clear();
                FavoriteFingerprintItemQueue.Clear();
                FingerprintProcessTask = Fingerprint_ProcessItemQueueAsync()
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Debug(
                                $"(Trace) Fingerprint_ProcessItemQueueAsync terminated unexpectedly. Exception: {task.Exception?.Flatten()}");
                        }
                        else if (task.IsCanceled)
                        {
                            Logger.Debug("(Trace) Fingerprint_ProcessItemQueueAsync was canceled.");
                        }
                        else
                        {
                            Logger.Debug("(Trace) Fingerprint_ProcessItemQueueAsync completed successfully.");
                        }

                        FingerprintProcessTask = null;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            if (EpisodeRefreshProcessTask is null)
            {
                EpisodeRefreshItemQueue.Clear();
                EpisodeRefreshProcessTask = EpisodeRefresh_ProcessItemQueueAsync()
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Debug(
                                $"(Trace) EpisodeRefresh_ProcessItemQueueAsync terminated unexpectedly. Exception: {task.Exception?.Flatten()}");
                        }
                        else if (task.IsCanceled)
                        {
                            Logger.Debug("(Trace) EpisodeRefresh_ProcessItemQueueAsync was canceled.");
                        }
                        else
                        {
                            Logger.Debug("(Trace) EpisodeRefresh_ProcessItemQueueAsync completed successfully.");
                        }

                        EpisodeRefreshProcessTask = null;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Starts the media info queue worker without touching any other queue. This is used by
        /// the auto-extract path so newly added items are processed even when Catch-up Mode is
        /// off; it never clears the queues because callers enqueue before requesting the start.
        /// </summary>
        public static void EnsureMediaInfoProcessTaskStarted()
        {
            lock (MediaInfoStartLock)
            {
                if (MediaInfoProcessTask != null) return;

                MediaInfoProcessTask = MediaInfo_ProcessItemQueueAsync()
                    .ContinueWith(task =>
                    {
                        if (task.IsFaulted)
                        {
                            Logger.Debug(
                                $"(Trace) MediaInfo_ProcessItemQueueAsync terminated unexpectedly. Exception: {task.Exception?.Flatten()}");
                        }
                        else if (task.IsCanceled)
                        {
                            Logger.Debug("(Trace) MediaInfo_ProcessItemQueueAsync was canceled.");
                        }
                        else
                        {
                            Logger.Debug("(Trace) MediaInfo_ProcessItemQueueAsync completed successfully.");
                        }

                        MediaInfoProcessTask = null;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }

        public static void UpdateMasterSemaphore(int maxConcurrentCount)
        {
            if (_currentMasterMaxConcurrentCount != maxConcurrentCount)
            {
                _currentMasterMaxConcurrentCount = maxConcurrentCount;

                // Adjust the existing instance in place. Workers that already acquired a permit
                // keep a reference to this object, so their Release can never hit a disposed
                // semaphore or a different counter.
                MasterSemaphore?.SetLimit(maxConcurrentCount);
            }
        }

        public static void UpdateTier2Semaphore(int maxConcurrentCount)
        {
            if (_currentTier2MaxConcurrentCount != maxConcurrentCount)
            {
                _currentTier2MaxConcurrentCount = maxConcurrentCount;

                Tier2Semaphore?.SetLimit(maxConcurrentCount);
            }
        }

        public static void UpdateFingerprintSemaphore(int maxConcurrentCount)
        {
            if (_currentFingerprintMaxConcurrentCount != maxConcurrentCount)
            {
                _currentFingerprintMaxConcurrentCount = maxConcurrentCount;

                FingerprintSemaphore?.SetLimit(maxConcurrentCount);
            }
        }

        private static TimeSpan GetCatchupMinimumDelay()
        {
            // Never let the loops spin without a delay; the option is the minimum interval
            // between two queue runs, not a way to disable the wait.
            var seconds = Plugin.Instance.GetPluginOptions().GeneralOptions.CatchupMinimumDelaySeconds;
            return TimeSpan.FromSeconds(Math.Max(seconds, 1));
        }

        private static bool IsFavoriteCatchupPriorityEnabled()
        {
            return Plugin.Instance?.GetPluginOptions()?.GeneralOptions.EnableFavoriteCatchupPriority is true;
        }

        /// <summary>
        /// Registers the item as pending and puts it in a media info queue. Every path that adds
        /// new work goes through here - catch-up on ItemAdded, auto-extract, favorite catch-up,
        /// the intro fingerprint worker handing items over and the chapter helpers - so an item
        /// that is already queued (or still being processed) is never enqueued a second time.
        /// Returns false when the item was already pending; nothing is enqueued in that case.
        /// </summary>
        public static bool TryEnqueueMediaInfo(BaseItem item, bool isFavorite = false)
        {
            if (item is null) return false;

            return MediaInfoQueueAdmission.TryEnqueue(PendingMediaInfoItemIds, item.InternalId,
                () => GetMediaInfoQueue(isFavorite).Enqueue(item));
        }

        /// <summary>
        /// Puts an item into a media info queue without the admission check, for work the worker
        /// already owns: items deferred because a library scan is running, items put back after a
        /// permit could not be acquired, and items moved between the favorite and regular queues.
        /// The pending marker stays registered (or is re-registered) so the item keeps being
        /// deduplicated while it waits for the next run.
        /// </summary>
        public static void RequeueMediaInfo(BaseItem item, bool isFavorite = false)
        {
            if (item is null) return;

            MediaInfoQueueAdmission.Requeue(PendingMediaInfoItemIds, item.InternalId,
                () => GetMediaInfoQueue(isFavorite).Enqueue(item));
        }

        private static ConcurrentQueue<BaseItem> GetMediaInfoQueue(bool isFavorite)
        {
            return isFavorite ? FavoriteMediaInfoExtractItemQueue : MediaInfoExtractItemQueue;
        }

        /// <summary>
        /// Puts back items that were already dequeued but never started because a permit could
        /// not be acquired (cancellation or an unexpected failure). They stay in the queue for
        /// the next run instead of disappearing silently, and stay pending with them.
        /// </summary>
        private static void RequeueMediaInfoItems(List<BaseItem> items, int startIndex, bool isFavorite)
        {
            for (var i = startIndex; i < items.Count; i++)
            {
                RequeueMediaInfo(items[i], isFavorite);
            }
        }

        private static void DrainFavoriteQueuesWhenDisabled()
        {
            if (IsFavoriteCatchupPriorityEnabled()) return;

            while (FavoriteMediaInfoExtractItemQueue.TryDequeue(out var mediaInfoItem))
            {
                RequeueMediaInfo(mediaInfoItem);
            }

            while (FavoriteFingerprintItemQueue.TryDequeue(out var fingerprintItem))
            {
                FingerprintItemQueue.Enqueue(fingerprintItem);
            }

            while (FavoriteIntroSkipItemQueue.TryDequeue(out var introSkipItem))
            {
                IntroSkipItemQueue.Enqueue(introSkipItem);
            }
        }

        public static async Task MediaInfo_ProcessItemQueueAsync()
        {
            Logger.Info("MediaInfo - ProcessItemQueueAsync Started");

            MediaInfoTokenSource = new CancellationTokenSource();
            var cancellationToken = MediaInfoTokenSource.Token;

            var dequeueItems = new List<BaseItem>();
            var deferredItems = new List<BaseItem>();
            var tasks = new List<Task>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _mediaInfoProcessLastRunTime;
                var remainingTime = GetCatchupMinimumDelay() - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected: the queue is stopping; unprocessed items stay for the next run.
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.ErrorException("QueueManager - Delay failed", e);
                        break;
                    }
                }

                DrainFavoriteQueuesWhenDisabled();
                var isFavorite = IsFavoriteCatchupPriorityEnabled() && !FavoriteMediaInfoExtractItemQueue.IsEmpty;
                var queue = isFavorite ? FavoriteMediaInfoExtractItemQueue : MediaInfoExtractItemQueue;

                if (!queue.IsEmpty)
                {
                    var maxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount;
                    var currentQueueCount = queue.Count;

                    dequeueItems.Clear();
                    deferredItems.Clear();

                    while (queue.TryDequeue(out var dequeueItem))
                    {
                        // The pending marker deliberately survives the dequeue: the item is still
                        // owned by the worker until its task finishes, and releasing it here is
                        // what let a second path (auto-extract, 15s behind ItemAdded) enqueue the
                        // same item again while the first pass was still running.
                        var library = dequeueItem.GetTopParent();
                        var progress = library?.GetRefreshProgress();

                        if (currentQueueCount < maxConcurrentCount && progress.HasValue)
                        {
                            deferredItems.Add(dequeueItem);
                            //Logger.Debug("MediaInfoExtract - Item Deferred: " + dequeueItem.Name + " - " + dequeueItem.Path);
                            //Logger.Debug($"MediaInfoExtract - Library Scan Running - Progress {progress:F2}% - {library.Path}");
                        }
                        else
                        {
                            dequeueItems.Add(dequeueItem);
                        }
                    }

                    Logger.Info("MediaInfoExtract - Clear Item Queue Started");

                    var dedupQueueItems =
                        dequeueItems.GroupBy(i => i.InternalId).Select(g => g.First()).ToList();
                    var mediaInfoItems = Plugin.LibraryApi.FetchExtractQueueItems(dedupQueueItems);

                    // Everything the scope, favorite and blacklist filters dropped will never get
                    // a task, so its pending marker has to be released here; keeping it would
                    // block the id from ever being queued again.
                    var scheduledIds = new HashSet<long>(mediaInfoItems.Select(i => i.InternalId));

                    foreach (var droppedItem in dequeueItems.Where(i => !scheduledIds.Contains(i.InternalId)))
                    {
                        MediaInfoQueueAdmission.Complete(PendingMediaInfoItemIds, droppedItem.InternalId);
                    }

                    if (mediaInfoItems.Count > 0)
                    {
                        Logger.Info("Master Max Concurrent Count: " + maxConcurrentCount);
                        var cooldownSeconds = maxConcurrentCount == 1
                            ? Plugin.Instance.GetPluginOptions().GeneralOptions.CooldownDurationSeconds
                            : (int?)null;
                        if (cooldownSeconds.HasValue)
                            Logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);
                        Logger.Info("Tier2 Max Concurrent Count: " +
                                    Plugin.Instance.GetPluginOptions().GeneralOptions
                                        .Tier2MaxConcurrentCount);

                        IsMediaInfoProcessTaskRunning = true;

                        for (var itemIndex = 0; itemIndex < mediaInfoItems.Count; itemIndex++)
                        {
                            var taskItem = mediaInfoItems[itemIndex];

                            try
                            {
                                await MasterSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // expected: cancellation requeues the remaining items and stops the loop.
                                RequeueMediaInfoItems(mediaInfoItems, itemIndex, isFavorite);
                                break;
                            }
                            catch (Exception e)
                            {
                                Logger.ErrorException("MediaInfoExtract - Acquire permit failed", e);
                                RequeueMediaInfoItems(mediaInfoItems, itemIndex, isFavorite);
                                break;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                MasterSemaphore.Release();
                                RequeueMediaInfoItems(mediaInfoItems, itemIndex, isFavorite);
                                Logger.Info("MediaInfoExtract - Item Cancelled: " + taskItem.Name + " - " +
                                            taskItem.Path);
                                break;
                            }

                            var task = Task.Run(async () =>
                            {
                                bool? result = null;

                                try
                                {
                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        Logger.Info("MediaInfoExtract - Item cancelled: " + taskItem.Name + " - " +
                                                    taskItem.Path);
                                        return;
                                    }

                                    result = await Plugin.LibraryApi
                                        .OrchestrateMediaInfoProcessAsync(taskItem, "MediaInfoExtract Catchup",
                                            cancellationToken).ConfigureAwait(false);

                                    if (result is null)
                                    {
                                        Logger.Info("MediaInfoExtract - Item skipped or non-existent: " + taskItem.Name + " - " +
                                                    taskItem.Path);
                                        return;
                                    }

                                    if (IsCatchupTaskSelected(CatchupTask.IntroSkip) &&
                                        taskItem is Episode episode && Plugin.PlaySessionMonitor.IsLibraryInScope(episode))
                                    {
                                        if (isFavorite)
                                        {
                                            FavoriteIntroSkipItemQueue.Enqueue(episode);
                                        }
                                        else
                                        {
                                            IntroSkipItemQueue.Enqueue(episode);
                                        }
                                    }

                                    Logger.Info("MediaInfoExtract - Item processed: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (OperationCanceledException)
                                {
                                    Logger.Info("MediaInfoExtract - Item cancelled: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (Exception e)
                                {
                                    Logger.ErrorException("MediaInfoExtract - Item failed: " + taskItem.Name + " - " +
                                                 taskItem.Path, e);
                                }
                                finally
                                {
                                    if (result is true && cooldownSeconds.HasValue)
                                    {
                                        try
                                        {
                                            await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken)
                                                .ConfigureAwait(false);
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            // expected: the queue stopped while waiting.
                                        }
                                        catch (Exception e)
                                        {
                                            Logger.ErrorException("QueueManager - Cooldown delay failed", e);
                                        }
                                    }

                                    MasterSemaphore.Release();

                                    // The item is finished - processed, skipped or failed - so it
                                    // may be queued again from now on. A permit failure or a
                                    // cancellation before the task started is handled above by
                                    // RequeueMediaInfoItems, which keeps the marker instead.
                                    MediaInfoQueueAdmission.Complete(PendingMediaInfoItemIds, taskItem.InternalId);
                                }
                            }, cancellationToken);
                            tasks.Add(task);
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                        tasks.Clear();

                        IsMediaInfoProcessTaskRunning = false;
                    }

                    Logger.Info("MediaInfoExtract - Clear Item Queue Stopped");
                }

                if (deferredItems.Count > 0)
                {
                    Logger.Info("MediaInfoExtract - Enqueue Deferred Count: " + deferredItems.Count);

                    foreach (var item in deferredItems)
                    {
                        RequeueMediaInfo(item, isFavorite);
                    }
                }

                _mediaInfoProcessLastRunTime = DateTime.UtcNow;
            }

            if (MediaInfoExtractItemQueue.IsEmpty && FavoriteMediaInfoExtractItemQueue.IsEmpty)
            {
                Logger.Info("MediaInfo - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("MediaInfo - ProcessItemQueueAsync Cancelled");
            }
        }

        public static async Task Fingerprint_ProcessItemQueueAsync()
        {
            Logger.Info("Fingerprint - ProcessItemQueueAsync Started");

            FingerprintTokenSource = new CancellationTokenSource();
            var cancellationToken = FingerprintTokenSource.Token;

            var dequeueItems = new List<BaseItem>();
            var deferredItems = new List<BaseItem>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _fingerprintProcessLastRunTime;
                var remainingTime = GetCatchupMinimumDelay() - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected: the queue is stopping; unprocessed items stay for the next run.
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.ErrorException("QueueManager - Delay failed", e);
                        break;
                    }
                }

                DrainFavoriteQueuesWhenDisabled();
                var isFavorite = IsFavoriteCatchupPriorityEnabled() && !FavoriteFingerprintItemQueue.IsEmpty;
                var queue = isFavorite ? FavoriteFingerprintItemQueue : FingerprintItemQueue;

                if (!queue.IsEmpty)
                {
                    var maxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.MaxConcurrentCount;
                    var fingerprintMaxConcurrentCount =
                        Plugin.Instance.GetPluginOptions().GeneralOptions.IntroDetectionMaxConcurrentCount;
                    var currentQueueCount = queue.Count;

                    dequeueItems.Clear();
                    deferredItems.Clear();

                    while (queue.TryDequeue(out var dequeueItem))
                    {
                        var library = dequeueItem.GetTopParent();
                        var progress = library?.GetRefreshProgress();

                        if (currentQueueCount < maxConcurrentCount && progress.HasValue)
                        {
                            deferredItems.Add(dequeueItem);
                        }
                        else
                        {
                            dequeueItems.Add(dequeueItem);
                        }
                    }

                    Logger.Info("IntroFingerprintExtract - Clear Item Queue Started");

                    var episodes = Plugin.FingerprintApi.FetchFingerprintQueueItems(dequeueItems);

                    if (IsCatchupTaskSelected(CatchupTask.MediaInfo, CatchupTask.IntroSkip))
                    {
                        var episodeIds = episodes.Select(e => e.InternalId).ToHashSet();
                        var mediaInfoItems = dequeueItems.Where(i => !episodeIds.Contains(i.InternalId));

                        foreach (var item in mediaInfoItems)
                        {
                            // Handed over to the media info worker; the admission check keeps an
                            // item that is already queued there from being added twice.
                            TryEnqueueMediaInfo(item, isFavorite);
                        }
                    }

                    if (episodes.Count > 0)
                    {
                        Logger.Info("Intro Detection Max Concurrent Count: " + fingerprintMaxConcurrentCount);
                        var cooldownSeconds = fingerprintMaxConcurrentCount == 1
                            ? Plugin.Instance.GetPluginOptions().GeneralOptions.CooldownDurationSeconds
                            : (int?)null;
                        if (cooldownSeconds.HasValue)
                            Logger.Info("Cooldown Duration Seconds: " + cooldownSeconds.Value);
                        var enableImageCapture = Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.EnableImageCapture;

                        var groupedBySeason = episodes.GroupBy(e => e.Season).ToList();
                        var episodesBySeason = groupedBySeason.Select(g => g.ToList()).ToList();
                        var seasonTasks = new List<Task>();
                        var abortFingerprint = false;

                        void RequeueFingerprintFrom(int seasonIndex, int episodeIndex)
                        {
                            for (var si = seasonIndex; si < episodesBySeason.Count; si++)
                            {
                                var list = episodesBySeason[si];

                                for (var ei = si == seasonIndex ? episodeIndex : 0; ei < list.Count; ei++)
                                {
                                    queue.Enqueue(list[ei]);
                                }
                            }
                        }

                        IsMediaInfoProcessTaskRunning = true;

                        for (var seasonIndex = 0; seasonIndex < groupedBySeason.Count; seasonIndex++)
                        {
                            var season = groupedBySeason[seasonIndex];
                            var taskSeason = season.Key;

                            if (cancellationToken.IsCancellationRequested)
                            {
                                RequeueFingerprintFrom(seasonIndex, 0);
                                Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name + " - " +
                                            taskSeason.Path);
                                break;
                            }

                            var episodeTasks = new List<Task>();
                            var seasonSkip = false;

                            var seasonEpisodes = episodesBySeason[seasonIndex];

                            for (var episodeIndex = 0; episodeIndex < seasonEpisodes.Count; episodeIndex++)
                            {
                                var taskItem = seasonEpisodes[episodeIndex];

                                try
                                {
                                    await FingerprintSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                                }
                                catch (OperationCanceledException)
                                {
                                    // expected: cancellation requeues the remaining episodes and stops the loop.
                                    RequeueFingerprintFrom(seasonIndex, episodeIndex);
                                    abortFingerprint = true;
                                    break;
                                }
                                catch (Exception e)
                                {
                                    Logger.ErrorException("IntroFingerprintExtract - Acquire permit failed", e);
                                    RequeueFingerprintFrom(seasonIndex, episodeIndex);
                                    abortFingerprint = true;
                                    break;
                                }

                                if (cancellationToken.IsCancellationRequested)
                                {
                                    FingerprintSemaphore.Release();
                                    RequeueFingerprintFrom(seasonIndex, episodeIndex);
                                    abortFingerprint = true;
                                    break;
                                }

                                var task = Task.Run(async () =>
                                {
                                    bool? result1 = null;
                                    Tuple<string, bool> result2 = null;

                                    try
                                    {
                                        if (cancellationToken.IsCancellationRequested)
                                        {
                                            Logger.Info("IntroFingerprintExtract - Episode cancelled: " +
                                                        taskItem.Name + " - " + taskItem.Path);
                                            return;
                                        }

                                        if (Plugin.LibraryApi.IsExtractNeeded(taskItem, enableImageCapture))
                                        {
                                            result1 = await Plugin.LibraryApi.OrchestrateMediaInfoProcessAsync(taskItem,
                                                    "IntroFingerprintExtract Catchup", cancellationToken)
                                                .ConfigureAwait(false);

                                            if (result1 is null)
                                            {
                                                Logger.Info(
                                                    "IntroFingerprintExtract - Episode skipped or non-existent: " +
                                                    taskItem.Name + " - " + taskItem.Path);
                                                seasonSkip = true;
                                                return;
                                            }
                                        }

                                        var dateCreated = taskItem.DateCreated;
                                        taskItem.DateCreated = new DateTimeOffset(
                                            new DateTime(dateCreated.Year, dateCreated.Month, dateCreated.Day,
                                                dateCreated.Hour, dateCreated.Minute, dateCreated.Second),
                                            dateCreated.Offset);

                                        result2 = await Plugin.FingerprintApi
                                            .CreateTitleFingerprint(taskItem, cancellationToken)
                                            .ConfigureAwait(false);

                                        Logger.Info("IntroFingerprintExtract - Episode processed: " + taskItem.Name +
                                                    " - " + taskItem.Path);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Logger.Info("IntroFingerprintExtract - Episode cancelled: " + taskItem.Name +
                                                    " - " + taskItem.Path);
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.ErrorException("IntroFingerprintExtract - Episode failed: " + taskItem.Name +
                                                     " - " + taskItem.Path, e);
                                    }
                                    finally
                                    {
                                        if ((result1 is true || result2?.Item2 is true) && cooldownSeconds.HasValue)
                                        {
                                            try
                                            {
                                                await Task.Delay(cooldownSeconds.Value * 1000, cancellationToken)
                                                    .ConfigureAwait(false);
                                            }
                                            catch (OperationCanceledException)
                                            {
                                                // expected: the queue stopped while waiting.
                                            }
                                            catch (Exception e)
                                            {
                                                Logger.ErrorException("QueueManager - Cooldown delay failed", e);
                                            }
                                        }

                                        FingerprintSemaphore.Release();
                                    }
                                }, cancellationToken);
                                episodeTasks.Add(task);
                            }

                            if (abortFingerprint)
                            {
                                if (episodeTasks.Count > 0)
                                {
                                    await Task.WhenAll(episodeTasks).ConfigureAwait(false);
                                }

                                break;
                            }

                            if (seasonSkip)
                            {
                                await Task.WhenAll(episodeTasks).ConfigureAwait(false);

                                Logger.Info("IntroFingerprintExtract - Season skipped: " + taskSeason.Name + " - " +
                                            taskSeason.Path);
                            }
                            else
                            {
                                if (cancellationToken.IsCancellationRequested)
                                {
                                    RequeueFingerprintFrom(seasonIndex + 1, 0);
                                    Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name +
                                                " - " + taskSeason.Path);
                                    break;
                                }

                                var seasonTask = Task.Run(async () =>
                                {
                                    await Task.WhenAll(episodeTasks).ConfigureAwait(false);

                                    try
                                    {
                                        await Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // expected: cancellation stops this season; remaining work stays queued.
                                        return;
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.ErrorException("IntroFingerprintExtract - Tier2 wait failed", e);
                                        return;
                                    }

                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        Tier2Semaphore.Release();
                                        Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name +
                                                    " - " + taskSeason.Path);
                                        return;
                                    }

                                    try
                                    {
                                        await Plugin.FingerprintApi
                                            .UpdateIntroMarkerForSeason(taskSeason, cancellationToken)
                                            .ConfigureAwait(false);
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Logger.Info("IntroFingerprintExtract - Season cancelled: " + taskSeason.Name +
                                                    " - " + taskSeason.Path);
                                    }
                                    catch (Exception e)
                                    {
                                        Logger.ErrorException("IntroFingerprintExtract - Season failed: " + taskSeason.Name +
                                                     " - " + taskSeason.Path, e);
                                    }
                                    finally
                                    {
                                        Tier2Semaphore.Release();
                                    }
                                }, cancellationToken);
                                seasonTasks.Add(seasonTask);
                            }
                        }
                        await Task.WhenAll(seasonTasks).ConfigureAwait(false);

                        IsMediaInfoProcessTaskRunning = false;
                    }

                    Logger.Info("IntroFingerprintExtract - Clear Item Queue Stopped");
                }

                if (deferredItems.Count > 0)
                {
                    Logger.Info("IntroFingerprintExtract - Enqueue Deferred Count: " + deferredItems.Count);

                    foreach (var item in deferredItems)
                    {
                        queue.Enqueue(item);
                    }
                }

                _fingerprintProcessLastRunTime = DateTime.UtcNow;
            }

            if (FingerprintItemQueue.IsEmpty && FavoriteFingerprintItemQueue.IsEmpty)
            {
                Logger.Info("Fingerprint - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("Fingerprint - ProcessItemQueueAsync Cancelled");
            }
        }

        public static async Task IntroSkip_ProcessItemQueueAsync()
        {
            Logger.Info("IntroSkip - ProcessItemQueueAsync Started");

            IntroSkipTokenSource = new CancellationTokenSource();
            var cancellationToken = IntroSkipTokenSource.Token;

            var dequeueItems = new List<Episode>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _introSkipProcessLastRunTime;
                var remainingTime = GetCatchupMinimumDelay() - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected: the queue is stopping; unprocessed items stay for the next run.
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.ErrorException("QueueManager - Delay failed", e);
                        break;
                    }
                }

                DrainFavoriteQueuesWhenDisabled();
                var queue = IsFavoriteCatchupPriorityEnabled() && !FavoriteIntroSkipItemQueue.IsEmpty
                    ? FavoriteIntroSkipItemQueue
                    : IntroSkipItemQueue;

                if (!queue.IsEmpty)
                {
                    dequeueItems.Clear();
                    while (queue.TryDequeue(out var dequeueItem))
                    {
                        dequeueItems.Add(dequeueItem);
                    }

                    if (dequeueItems.Count > 0)
                    {
                        Logger.Info("IntroSkip - Clear Item Queue Started");

                        Plugin.ChapterApi.PopulateIntroCredits(dequeueItems);

                        Logger.Info("IntroSkip - Clear Item Queue Stopped");
                    }
                }
                _introSkipProcessLastRunTime = DateTime.UtcNow;
            }

            if (IntroSkipItemQueue.IsEmpty && FavoriteIntroSkipItemQueue.IsEmpty)
            {
                Logger.Info("IntroSkip - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("IntroSkip - ProcessItemQueueAsync Cancelled");
            }
        }

        public static async Task EpisodeRefresh_ProcessItemQueueAsync()
        {
            Logger.Info("EpisodeRefresh - ProcessItemQueueAsync Started");

            EpisodeRefreshTokenSource = new CancellationTokenSource();
            var cancellationToken = EpisodeRefreshTokenSource.Token;

            var dequeueItems = new List<BaseItem>();
            var tasks = new List<Task>();

            while (!cancellationToken.IsCancellationRequested)
            {
                var timeSinceLastRun = DateTime.UtcNow - _episodeRefreshProcessLastRunTime;
                var remainingTime = GetCatchupMinimumDelay() - timeSinceLastRun;
                if (remainingTime > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(remainingTime, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected: the queue is stopping; unprocessed items stay for the next run.
                        break;
                    }
                    catch (Exception e)
                    {
                        Logger.ErrorException("QueueManager - Delay failed", e);
                        break;
                    }
                }

                if (!EpisodeRefreshItemQueue.IsEmpty)
                {
                    dequeueItems.Clear();

                    while (EpisodeRefreshItemQueue.TryDequeue(out var dequeueItem))
                    {
                        dequeueItems.Add(dequeueItem);
                    }

                    Logger.Info("EpisodeRefresh - Clear Item Queue Started");


                    var itemsToRefresh = Plugin.LibraryApi.FetchEpisodeRefreshQueueItems(dequeueItems);

                    if (itemsToRefresh.Count > 0)
                    {
                        var tier2MaxConcurrentCount =
                            Plugin.Instance.GetPluginOptions().GeneralOptions.Tier2MaxConcurrentCount;
                        Logger.Info("Tier2 Max Concurrent Count: " + tier2MaxConcurrentCount);

                        IsEpisodeRefreshProcessTaskRunning = true;

                        for (var itemIndex = 0; itemIndex < itemsToRefresh.Count; itemIndex++)
                        {
                            var taskItem = itemsToRefresh[itemIndex];

                            try
                            {
                                await Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                // expected: cancellation re-enqueues the remaining refreshes and stops the loop.
                                for (var i = itemIndex; i < itemsToRefresh.Count; i++)
                                {
                                    EpisodeRefreshItemQueue.Enqueue(itemsToRefresh[i]);
                                }

                                break;
                            }
                            catch (Exception e)
                            {
                                Logger.ErrorException("EpisodeRefresh - Acquire permit failed", e);

                                for (var i = itemIndex; i < itemsToRefresh.Count; i++)
                                {
                                    EpisodeRefreshItemQueue.Enqueue(itemsToRefresh[i]);
                                }

                                break;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                Tier2Semaphore.Release();

                                for (var i = itemIndex; i < itemsToRefresh.Count; i++)
                                {
                                    EpisodeRefreshItemQueue.Enqueue(itemsToRefresh[i]);
                                }

                                Logger.Info("EpisodeRefresh - Item Cancelled: " + taskItem.Name + " - " +
                                            taskItem.Path);
                                break;
                            }

                            var task = Task.Run(async () =>
                            {
                                try
                                {
                                    await Task.Delay(
                                            ConcurrentRandom.Next(0,
                                                Math.Max(0, tier2MaxConcurrentCount - Tier2Semaphore.CurrentCount) *
                                                MetadataApi.RequestIntervalMs), cancellationToken)
                                        .ConfigureAwait(false);

                                    if (cancellationToken.IsCancellationRequested)
                                    {
                                        Logger.Info("EpisodeRefresh - Item cancelled: " + taskItem.Name + " - " +
                                                    taskItem.Path);
                                        return;
                                    }

                                    await Plugin.LibraryApi.RefreshItemMetadata(taskItem, cancellationToken)
                                        .ConfigureAwait(false);

                                    Logger.Info("EpisodeRefresh - Item processed: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (OperationCanceledException)
                                {
                                    Logger.Info("EpisodeRefresh - Item cancelled: " + taskItem.Name + " - " +
                                                taskItem.Path);
                                }
                                catch (Exception e)
                                {
                                    Logger.ErrorException("EpisodeRefresh - Item failed: " + taskItem.Name + " - " +
                                                 taskItem.Path, e);
                                }
                                finally
                                {
                                    Tier2Semaphore.Release();
                                }
                            }, cancellationToken);
                            tasks.Add(task);
                        }

                        await Task.WhenAll(tasks).ConfigureAwait(false);
                        tasks.Clear();

                        IsEpisodeRefreshProcessTaskRunning = false;
                    }

                    Logger.Info("EpisodeRefresh - Clear Item Queue Stopped");
                }

                _episodeRefreshProcessLastRunTime = DateTime.UtcNow;
            }

            if (EpisodeRefreshItemQueue.IsEmpty)
            {
                Logger.Info("EpisodeRefresh - ProcessItemQueueAsync Stopped");
            }
            else
            {
                Logger.Info("EpisodeRefresh - ProcessItemQueueAsync Cancelled");
            }
        }

        public static void Dispose()
        {
            MediaInfoTokenSource?.Cancel();
            FingerprintTokenSource?.Cancel();
            EpisodeRefreshTokenSource?.Cancel();
        }
    }
}
