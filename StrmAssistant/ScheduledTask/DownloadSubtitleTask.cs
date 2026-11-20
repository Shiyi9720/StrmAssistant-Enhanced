using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    public class DownloadSubtitleTask : IScheduledTask
    {
        private ILogger _logger => Plugin.Instance?.Logger;

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("SubtitleDownload - Scheduled Task Execute");
            _logger?.Info("Tier2 Max Concurrent Count: " +
                         Plugin.Instance.GetPluginOptions().GeneralOptions.Tier2MaxConcurrentCount);

            await Task.Yield();
            progress.Report(0);

            var items = Plugin.LibraryApi.FetchPostExtractTaskItems(false)
                .Where(i => i is Movie || i is Episode).ToList();
            _logger?.Info("SubtitleDownload - Number of items: " + items.Count);

            double total = items.Count;
            var index = 0;
            var current = 0;
            var failed = 0;
            var skip = 0;

            var tasks = new List<Task>();

            foreach (var item in items)
            {
                try
                {
                    await QueueManager.Tier2Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected: the scheduled task was cancelled, so it stops taking new items.
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    QueueManager.Tier2Semaphore.Release();
                    _logger?.Info("SubtitleDownload - Scheduled Task Cancelled");
                    return;
                }

                var taskIndex = ++index;
                var taskItem = item;
                var task = Task.Run(async () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger?.Info("SubtitleDownload - Scheduled Task Cancelled");
                            return;
                        }

                        var outcome = await Plugin.SubtitleApi.DownloadSubtitles(taskItem, cancellationToken)
                            .ConfigureAwait(false);

                        switch (outcome)
                        {
                            case SubtitleDownloadOutcome.ProviderUnavailable:
                                // The provider is missing for every item, so this is a real
                                // failure rather than a normal "no subtitle found".
                                Interlocked.Increment(ref failed);
                                _logger?.Warn("SubtitleDownload - Item failed (provider unavailable): " +
                                              taskItem.Name + " - " + taskItem.Path);
                                break;
                            case SubtitleDownloadOutcome.DisabledForLibrary:
                                Interlocked.Increment(ref skip);
                                break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (ScheduledTaskFailurePolicy.IsTaskCancellation(cancellationToken))
                        {
                            _logger?.Info("SubtitleDownload - Scheduled Task Cancelled");
                        }
                        else
                        {
                            Interlocked.Increment(ref failed);
                            _logger?.Warn("SubtitleDownload - Item timed out: " + taskItem.Name + " - " +
                                          taskItem.Path);
                        }
                    }
                    catch (Exception e)
                    {
                        Interlocked.Increment(ref failed);
                        _logger?.ErrorException("SubtitleDownload - Item failed: " + taskItem.Name + " - " + taskItem.Path, e);
                    }
                    finally
                    {
                        QueueManager.Tier2Semaphore.Release();

                        var currentCount = Interlocked.Increment(ref current);
                        progress.Report(currentCount / total * 100);

                        _logger?.Info("SubtitleDownload - Progress " + currentCount + "/" + total + " - Task " +
                                     taskIndex + ": " + taskItem.Path);
                    }
                }, cancellationToken);
                tasks.Add(task);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            progress.Report(100.0);

            var succeeded = current - failed - skip;
            _logger?.Info("SubtitleDownload - Outcome: " +
                          ScheduledTaskFailurePolicy.Summary(succeeded, failed, skip));

            if (!cancellationToken.IsCancellationRequested)
            {
                ScheduledTaskFailurePolicy.ThrowIfFailed("SubtitleDownload", succeeded, failed, skip);
            }

            _logger?.Info("SubtitleDownload - Scheduled Task Complete");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "DownloadSubtitleTask";

        public string Description => Resources.ResourceManager.GetString(
            "DownloadSubtitleTask_Description_Downloads_missing_subtitles_from_OpenSubtitles",
            Plugin.Instance.DefaultUICulture);

        public string Name => "Download Subtitles";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
