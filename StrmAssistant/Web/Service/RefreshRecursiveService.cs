using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    public class RefreshRecursiveService : BaseApiService
    {
        private const int EpisodeConcurrency = 3;

        private static readonly ConcurrentDictionary<long, byte> Running =
            new ConcurrentDictionary<long, byte>();

        private readonly ILibraryManager _libraryManager;

        public RefreshRecursiveService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Post(RefreshRecursive request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Series) && !(item is Season))
            {
                return new RefreshRecursiveResponse();
            }

            if (!Running.TryAdd(item.InternalId, 0))
            {
                return new RefreshRecursiveResponse { AlreadyRunning = true };
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshRecursivelyAsync(item).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger?.Error("RecursiveRefresh - Failed: " + item.Name);
                    Plugin.Instance.Logger?.Error(e.Message);
                    Plugin.Instance.Logger?.Debug(e.StackTrace);
                }
                finally
                {
                    Running.TryRemove(item.InternalId, out _);
                }
            });

            return new RefreshRecursiveResponse { Queued = true };
        }

        private async Task RefreshRecursivelyAsync(BaseItem item)
        {
            var seasons = new List<Season>();

            if (item is Series series)
            {
                Plugin.Instance.Logger?.Info("RecursiveRefresh - Series: " + series.Name);

                await RefreshItemAsync(series).ConfigureAwait(false);

                seasons = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Season) },
                    AncestorIds = new[] { series.InternalId },
                    Recursive = true
                }).OfType<Season>().OrderBy(season => season.IndexNumber).ToList();
            }
            else if (item is Season season)
            {
                Plugin.Instance.Logger?.Info("RecursiveRefresh - Season: " + season.Name);

                seasons.Add(season);
            }

            foreach (var season in seasons)
            {
                await RefreshItemAsync(season).ConfigureAwait(false);
            }

            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                AncestorIds = new[] { item.InternalId },
                Recursive = true
            }).OfType<Episode>()
                .Where(episode => !episode.IsVirtualItem)
                .OrderBy(episode => episode.ParentIndexNumber)
                .ThenBy(episode => episode.IndexNumber)
                .ToList();

            Plugin.Instance.Logger?.Info("RecursiveRefresh - Episodes: " + episodes.Count);

            using (var semaphore = new SemaphoreSlim(EpisodeConcurrency))
            {
                var tasks = episodes.Select(async episode =>
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);

                    try
                    {
                        await RefreshItemAsync(episode).ConfigureAwait(false);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            Plugin.Instance.Logger?.Info("RecursiveRefresh - Completed: " + item.Name);
        }

        private static async Task RefreshItemAsync(BaseItem item)
        {
            try
            {
                var refreshOptions = Plugin.MetadataApi.GetMetadataFullRefreshOptions();
                await item.RefreshMetadata(refreshOptions, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger?.Error("RecursiveRefresh - Item failed: " + item.Name);
                Plugin.Instance.Logger?.Error(e.Message);
                Plugin.Instance.Logger?.Debug(e.StackTrace);
            }
        }
    }
}
