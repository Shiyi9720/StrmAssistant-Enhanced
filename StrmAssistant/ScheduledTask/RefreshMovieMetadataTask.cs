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
    /// Refreshes metadata for movies that are already in the library. The configured movie
    /// refresh mode decides whether complete items are skipped and whether existing values may
    /// be replaced. The task has no default trigger so upgrading does not start a mass refresh.
    /// </summary>
    public class RefreshMovieMetadataTask : IScheduledTask, IConfigurableScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;

        private static readonly Random Random = new Random();

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("MovieMetadataRefresh - Scheduled Task Execute");
            await Task.Yield();
            progress.Report(0);

            var tier2MaxConcurrentCount = Plugin.Instance.GetPluginOptions().GeneralOptions.Tier2MaxConcurrentCount;
            _logger?.Info("Tier2 Max Concurrent Count: " + tier2MaxConcurrentCount);

            var itemsToRefresh = Plugin.LibraryApi.FetchMovieMetadataRefreshTaskItems();

            IsRunning = true;

            double total = itemsToRefresh.Count;
            var index = 0;
            var current = 0;

            try
            {
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
                        _logger?.Info("MovieMetadataRefresh - Scheduled Task Cancelled");
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
                                        Math.Max(0,
                                            tier2MaxConcurrentCount - QueueManager.Tier2Semaphore.CurrentCount) *
                                        MetadataApi.RequestIntervalMs), cancellationToken)
                                .ConfigureAwait(false);

                            if (cancellationToken.IsCancellationRequested)
                            {
                                _logger?.Info("MovieMetadataRefresh - Scheduled Task Cancelled");
                                return;
                            }

                            await Plugin.LibraryApi.RefreshMovieMetadata(taskItem, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            _logger?.Info("MovieMetadataRefresh - Item cancelled: " + taskItem.Name);
                        }
                        catch (Exception e)
                        {
                            _logger?.Info("MovieMetadataRefresh - Item failed: " + taskItem.Name);
                            _logger?.Debug(e.Message);
                            _logger?.Debug(e.StackTrace);
                        }
                        finally
                        {
                            QueueManager.Tier2Semaphore.Release();

                            var currentCount = Interlocked.Increment(ref current);
                            progress.Report(total > 0 ? currentCount / total * 100 : 100);
                            _logger?.Info("MovieMetadataRefresh - Progress " + currentCount + "/" + total + " - " +
                                         "Task " + taskIndex + ": " + taskItem.Path);
                        }
                    }, cancellationToken);

                    tasks.Add(task);
                    Task.Delay(10).Wait();
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            finally
            {
                IsRunning = false;
            }

            progress.Report(100.0);
            _logger?.Info("MovieMetadataRefresh - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "RefreshMovieMetadataTask";

        public string Description => Resources.ResourceManager.GetString(
            "RefreshMovieMetadataTask_Description_Refresh_existing_movies_using_the_configured_movie_metadata_refresh_mode",
            Plugin.Instance.DefaultUICulture);

        public string Name => Resources.ResourceManager.GetString(
            "RefreshMovieMetadataTask_Refresh_Movie_Metadata", Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public bool IsLogged => true;

        public static bool IsRunning { get; private set; }
    }
}
