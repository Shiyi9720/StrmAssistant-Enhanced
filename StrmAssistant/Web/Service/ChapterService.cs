using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    public class ChapterService : BaseApiService
    {
        private static readonly SemaphoreSlim RedetectLock = new SemaphoreSlim(1, 1);

        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        public ChapterService(ILibraryManager libraryManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
        }

        public void Post(ClearIntro request)
        {
            var itemById = _libraryManager.GetItemById(request.Id);

            if (!(itemById is Series || itemById is Season)) return;

            var episodes = Plugin.ChapterApi.FetchClearTaskItems(new List<BaseItem> { itemById });

            foreach (var item in episodes)
            {
                Plugin.ChapterApi.RemoveIntroCreditsMarkers(item);
                _logger?.Info("IntroSkipClear - " + item.Name + " - " + item.Path);
            }
        }

        /// <summary>
        /// Re-detects the intro and credits markers of one series or one season. Runs in the
        /// background because audio fingerprinting a whole season can take minutes; the response
        /// tells the client whether the work was queued or whether a re-detection is already
        /// running. Only one manual re-detection runs at a time, the fingerprint pipeline is
        /// expensive and serializes its marker writes anyway.
        /// </summary>
        public object Post(RedetectIntro request)
        {
            var item = _libraryManager.GetItemById(request.Id);

            if (!(item is Series) && !(item is Season))
            {
                return new RedetectIntroResponse();
            }

            if (!RedetectLock.Wait(0))
            {
                return new RedetectIntroResponse { AlreadyRunning = true };
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RedetectIntroAsync(item).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger?.Error("IntroRedetect - Failed: " + item.Name);
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
                finally
                {
                    RedetectLock.Release();
                }
            });

            return new RedetectIntroResponse { Queued = true };
        }

        private async Task RedetectIntroAsync(BaseItem item)
        {
            List<Season> seasons;

            if (item is Series series)
            {
                seasons = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Season) },
                    AncestorIds = new[] { series.InternalId },
                    Recursive = true
                }).OfType<Season>().OrderBy(season => season.IndexNumber).ToList();
            }
            else
            {
                seasons = new List<Season> { (Season)item };
            }

            _logger?.Info("IntroRedetect - Started: " + item.Name + " - Seasons: " + seasons.Count);

            foreach (var season in seasons)
            {
                // Share the concurrency budget of the scheduled fingerprint task so a manual
                // re-detect does not run next to a full library detection.
                var semaphore = QueueManager.Tier2Semaphore;

                if (semaphore != null)
                {
                    await semaphore.WaitAsync().ConfigureAwait(false);
                }

                try
                {
                    if (Plugin.FingerprintApi != null)
                    {
                        await Plugin.FingerprintApi.RedetectIntroForSeason(season, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    _logger?.Error("IntroRedetect - Season failed: " + season.Name + " - " + season.Path);
                    _logger?.Error(e.Message);
                    _logger?.Debug(e.StackTrace);
                }
                finally
                {
                    semaphore?.Release();
                }
            }

            _logger?.Info("IntroRedetect - Completed: " + item.Name);
        }
    }
}
