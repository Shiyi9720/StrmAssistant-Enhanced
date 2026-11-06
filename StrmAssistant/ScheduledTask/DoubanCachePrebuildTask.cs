using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// Walks every movie/series library in batches and fills the Douban rating cache for items
    /// that do not have a cached rating yet. The cache is the same one the detail page badge
    /// reads, so this task only pre-warms it; it never changes item metadata and it is never
    /// triggered by opening a detail page.
    /// Items are processed one by one with a fixed request interval. A failed item is logged and
    /// skipped; a Douban rate-limit block stops the whole run instead of retrying.
    /// </summary>
    public class DoubanCachePrebuildTask : IScheduledTask, IConfigurableScheduledTask
    {
        private static readonly string[] ItemTypes = { nameof(Movie), nameof(Series) };

        private readonly ILibraryManager _libraryManager;
        private ILogger _logger => Plugin.Instance?.Logger;

        public DoubanCachePrebuildTask(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            _logger?.Info("DoubanCachePrebuild - Scheduled Task Execute");
            await Task.Yield();
            progress?.Report(0);

            var options = DoubanCacheOptions.GetEffective();

            if (!options.EnableDoubanCachePrebuild)
            {
                _logger?.Info("DoubanCachePrebuild - Disabled, skipped");
                progress?.Report(100.0);
                return;
            }

            var cacheApi = Plugin.DoubanCacheApi;

            if (cacheApi is null)
            {
                _logger?.Warn("DoubanCachePrebuild - Douban cache API is not initialized");
                progress?.Report(100.0);
                return;
            }

            var batchSize = Math.Max(1, options.BatchSize);

            var libraries = _libraryManager.GetVirtualFolders()
                .Where(l => !LibraryApi.ExcludedCollectionTypes.Contains(l.CollectionType))
                .Where(l => l.CollectionType is null ||
                            l.CollectionType == CollectionType.Movies.ToString() ||
                            l.CollectionType == CollectionType.TvShows.ToString())
                .ToList();

            var itemIdsByLibrary = new List<(string Name, long[] ItemIds)>();
            var seenItemIds = new HashSet<long>();
            var total = 0;

            foreach (var library in libraries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger?.Info("DoubanCachePrebuild - Scheduled Task Cancelled");
                    return;
                }

                BaseItem libraryFolder = null;

                if (Guid.TryParse(library.Guid, out var libraryGuid))
                {
                    libraryFolder = _libraryManager.GetItemById(libraryGuid);
                }

                if (libraryFolder is null && !string.IsNullOrEmpty(library.ItemId))
                {
                    libraryFolder = _libraryManager.GetItemById(library.ItemId);
                }

                if (libraryFolder is null)
                {
                    _logger?.Warn("DoubanCachePrebuild - Library not found: " + library.Name);
                    continue;
                }

                var ids = _libraryManager.GetInternalItemIds(new InternalItemsQuery
                {
                    AncestorIds = new[] { libraryFolder.InternalId },
                    Recursive = true,
                    IncludeItemTypes = ItemTypes
                }).Where(seenItemIds.Add).ToArray();

                _logger?.Info($"DoubanCachePrebuild - {library.Name}: {ids.Length} item(s)");
                itemIdsByLibrary.Add((library.Name, ids));
                total += ids.Length;
            }

            if (total == 0)
            {
                _logger?.Info("DoubanCachePrebuild - No items in scope");
                progress?.Report(100.0);
                return;
            }

            _logger?.Info($"DoubanCachePrebuild - Number of items: {total}, batch size: {batchSize}, " +
                          $"request interval: {options.GetRequestIntervalSeconds()}s");

            var processed = 0;
            var fetched = 0;
            var alreadyCached = 0;
            var notFound = 0;
            var failed = 0;
            var stoppedByBlock = false;

            foreach (var library in itemIdsByLibrary)
            {
                if (stoppedByBlock || cancellationToken.IsCancellationRequested) break;

                for (var offset = 0; offset < library.ItemIds.Length; offset += batchSize)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        _logger?.Info("DoubanCachePrebuild - Scheduled Task Cancelled");
                        return;
                    }

                    var batchIds = library.ItemIds.Skip(offset).Take(batchSize).ToArray();
                    var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = batchIds });

                    foreach (var item in items)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            _logger?.Info("DoubanCachePrebuild - Scheduled Task Cancelled");
                            return;
                        }

                        processed++;

                        if (Plugin.DoubanApi?.GetCachedRating(item.InternalId) != null)
                        {
                            alreadyCached++;
                            continue;
                        }

                        try
                        {
                            var entry = await cacheApi.PrebuildRatingAsync(item, cancellationToken)
                                .ConfigureAwait(false);

                            if (entry != null) fetched++;
                            else notFound++;
                        }
                        catch (OperationCanceledException)
                        {
                            _logger?.Info("DoubanCachePrebuild - Scheduled Task Cancelled");
                            return;
                        }
                        catch (Exception e)
                        {
                            failed++;
                            _logger?.Warn("DoubanCachePrebuild - Item failed: " + item.Name);
                            _logger?.Debug(e.Message);
                            _logger?.Debug(e.StackTrace);
                        }

                        if (DoubanCacheApi.IsTemporarilyBlocked)
                        {
                            stoppedByBlock = true;
                            _logger?.Warn("DoubanCachePrebuild - Douban rate limited, stopping this run");
                            break;
                        }
                    }

                    progress?.Report(Math.Min(100.0, processed * 100.0 / total));
                    _logger?.Info($"DoubanCachePrebuild - Progress {processed}/{total}, fetched: {fetched}, " +
                                  $"cached: {alreadyCached}, not found: {notFound}, failed: {failed}");
                }
            }

            progress?.Report(100.0);
            _logger?.Info("DoubanCachePrebuild - Scheduled Task Complete");
            _logger?.Info($"DoubanCachePrebuild - Fetched: {fetched}, Already cached: {alreadyCached}, " +
                          $"Not found: {notFound}, Failed: {failed}, Rate limited: {stoppedByBlock}");
        }

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public string Key => "DoubanCachePrebuildTask";

        public string Name => Resources.ResourceManager.GetString(
            "DoubanCachePrebuildTask_Name_Prebuild_Douban_Cache", Plugin.Instance.DefaultUICulture);

        public string Description => Resources.ResourceManager.GetString(
            "DoubanCachePrebuildTask_Description_Fetch_Douban_ratings_for_movies_and_series_that_do_not_have_a_cached_rating_yet__Requests_are_rate_limited_and_the_task_can_be_cancelled_at_any_time_",
            Plugin.Instance.DefaultUICulture);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Emby only consumes the default trigger when it first registers the task. Changes to
            // the option afterwards must be made in the scheduled task settings.
            var hours = Math.Max(1, Math.Min(168, DoubanCacheOptions.GetEffective().ExecutionIntervalHours));

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerInterval,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks
            };
        }

        public bool IsHidden => !DoubanCacheOptions.GetEffective().EnableDoubanCachePrebuild;

        public bool IsEnabled => true;

        public bool IsLogged => true;
    }
}
