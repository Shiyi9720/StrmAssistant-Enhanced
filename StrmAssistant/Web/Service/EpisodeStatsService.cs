using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Concurrent;

namespace StrmAssistant.Web.Service
{
    public class EpisodeStatsService : BaseApiService
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        private static readonly ConcurrentDictionary<long, CacheEntry> Cache =
            new ConcurrentDictionary<long, CacheEntry>();

        private readonly ILibraryManager _libraryManager;

        public EpisodeStatsService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        /// <summary>
        /// Counts the episodes Emby knows for a series, split into "all known" (including the
        /// virtual episodes created by the missing episodes feature) and "in library" (episodes
        /// that actually have a media source). Season 0 specials are not counted.
        /// </summary>
        public object Get(GetEpisodeStats request)
        {
            if (!(_libraryManager.GetItemById(request.Id) is Series series))
            {
                return new EpisodeStatsResponse();
            }

            if (Cache.TryGetValue(series.InternalId, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < CacheDuration)
            {
                return cached.Response;
            }

            var response = new EpisodeStatsResponse { IsSeries = true };

            try
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { nameof(Episode) },
                    AncestorIds = new[] { series.InternalId },
                    Recursive = true
                });

                foreach (var item in episodes)
                {
                    if (!(item is Episode episode)) continue;
                    if (episode.ParentIndexNumber.HasValue && episode.ParentIndexNumber.Value < 1) continue;

                    response.Total++;
                    if (!episode.IsVirtualItem) response.InLibrary++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Debug("EpisodeStats - Query failed: " + ex.Message);
            }

            if (Cache.Count > 500)
            {
                Cache.Clear();
            }

            Cache[series.InternalId] = new CacheEntry { CachedAt = DateTime.UtcNow, Response = response };

            return response;
        }

        private class CacheEntry
        {
            public DateTime CachedAt { get; set; }
            public EpisodeStatsResponse Response { get; set; }
        }
    }
}
