using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// Applies the episode metadata refresh rules to movies. The rules and thresholds are the
    /// ones configured for episodes; movies are scoped by the date they were added to the library
    /// instead of their release date. The task stays hidden while movie refresh is disabled.
    /// </summary>
    public class RefreshMovieTask : IScheduledTask, IConfigurableScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;

        private static readonly Random Random = new Random();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("MovieRefresh - Scheduled Task Execute");
            var tier2MaxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.Tier2MaxConcurrentCount;
            _logger?.Info("Tier2 Max Concurrent Count: " + tier2MaxConcurrentCount);

            await Task.Yield();
            progress.Report(0);

            var itemsToRefresh = Plugin.LibraryApi.FetchMovieRefreshTaskItems();

            double total = itemsToRefresh.Count;
            var index = 0;
            var current = 0;

            var tasks = new List<Task>();

            foreach (var item in itemsToRefresh)
            {
                try
                {
                    await QueueManager.Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    QueueManager.Tier2Semaphore.Release();
                    _logger?.Info("MovieRefresh - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(
                                Random.Next(0,
                                    Math.Max(0, tier2MaxConcurrentCount - QueueManager.Tier2Semaphore.CurrentCount) *
                                    MetadataApi.RequestIntervalMs), cancellationToken)
                            .ConfigureAwait(false);

                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger?.Info("MovieRefresh - Scheduled Task Cancelled");
                            return;
                        }

                        await Plugin.LibraryApi.RefreshMovieMetadata(taskItem, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        _logger?.Info("MovieRefresh - Item cancelled: " + taskItem.Name + " - " + taskItem.Path);
                    }
                    catch (Exception e)
                    {
                        _logger?.Info("MovieRefresh - Item failed: " + taskItem.Name + " - " + taskItem.Path);
                        _logger?.Debug(e.Message);
                        _logger?.Debug(e.StackTrace);
                    }
                    finally
                    {
                        QueueManager.Tier2Semaphore.Release();

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);
                        _logger?.Info("MovieRefresh - Progress " + currentCount + "/" + total + " - " +
                                      "Task " + taskIndex + ": " + taskItem.Path);
                    }
                }, cancellationToken);
                tasks.Add(task);
                Task.Delay(10).Wait();
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress.Report(100.0);
            _logger?.Info("MovieRefresh - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "MovieRefreshTask";

        public string Description => Resources.ResourceManager.GetString(
            "MovieRefreshTask_Description_Refresh_metadata_for_movies_missing_overview_image_or_Chinese_overview",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString("MovieRefreshTask_Name_Refresh_Movie",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden =>
            !Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions.MovieRefreshOptions.EnableMovieRefresh;

        public bool IsEnabled => true;

        public bool IsLogged => true;
    }
}
