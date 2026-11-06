using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    public class DoubanService : BaseApiService
    {
        private static readonly TimeSpan MatchCacheTime = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, CachedMatch> MatchCache =
            new ConcurrentDictionary<string, CachedMatch>(StringComparer.OrdinalIgnoreCase);

        private readonly ILibraryManager _libraryManager;

        public DoubanService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public async Task<object> Get(GetDoubanRecommendations request)
        {
            var response = new DoubanRecommendationsResponse();
            var options = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions;

            if (!options.EnableDoubanRecommendations) return response;

            var limit = Math.Max(1, Math.Min(request.Limit ?? 18, 40));
            List<DoubanRecommendation> recommendations;

            try
            {
                recommendations = await Plugin.DoubanApi
                    .GetRecommendationsAsync(request.Kind, limit, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger?.Debug("DoubanRecommendations - Request failed: " + e.Message);
                return response;
            }

            foreach (var recommendation in recommendations)
            {
                var match = FindMatch(recommendation);

                response.Items.Add(new DoubanRecommendationInfo
                {
                    DoubanId = recommendation.DoubanId,
                    Title = recommendation.Title,
                    Year = recommendation.Year,
                    CoverUrl = recommendation.CoverUrl,
                    Subtitle = recommendation.Subtitle,
                    Rating = recommendation.Rating,
                    ItemId = match.ItemId,
                    ItemType = match.ItemType
                });
            }

            return response;
        }

        private CachedMatch FindMatch(DoubanRecommendation recommendation)
        {
            var cacheKey = recommendation.DoubanId + "|" + recommendation.Title + "|" + recommendation.Year;

            if (MatchCache.TryGetValue(cacheKey, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < MatchCacheTime)
            {
                return cached;
            }

            var result = FindMatchInLibrary(recommendation);

            if (MatchCache.Count > 500) MatchCache.Clear();

            MatchCache[cacheKey] = result;

            return result;
        }

        private CachedMatch FindMatchInLibrary(DoubanRecommendation recommendation)
        {
            var year = ParseYear(recommendation.Year);
            var titles = new[] { recommendation.Title, recommendation.OriginalTitle }
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .SelectMany(GetTitleVariants)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var title in titles)
            {
                var candidates = FindCandidates(title);

                var exact = candidates.FirstOrDefault(item =>
                    string.Equals(item.Name, title, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.OriginalTitle, title, StringComparison.OrdinalIgnoreCase));

                if (exact != null) return ToCachedMatch(exact);

                if (year.HasValue)
                {
                    var sameYear = candidates.FirstOrDefault(item => item.ProductionYear == year.Value);

                    if (sameYear != null) return ToCachedMatch(sameYear);
                }
            }

            return new CachedMatch { CachedAt = DateTime.UtcNow };
        }

        private List<BaseItem> FindCandidates(string title)
        {
            var includeTypes = new[] { nameof(Movie), nameof(Series) };

            try
            {
                var candidates = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = includeTypes,
                    Recursive = true,
                    SearchTerm = title,
                    Limit = 10
                }).ToList();

                if (candidates.Count > 0) return candidates;

                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = includeTypes,
                    Recursive = true,
                    NameContains = title,
                    Limit = 10
                }).ToList();
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger?.Debug("DoubanRecommendations - Match failed: " + e.Message);
                return new List<BaseItem>();
            }
        }

        private static IEnumerable<string> GetTitleVariants(string title)
        {
            yield return title;

            string traditional = null;
            string simplified = null;

            try
            {
                traditional = LanguageUtility.ConvertSimplifiedToTraditional(title);
                simplified = LanguageUtility.ConvertTraditionalToSimplified(title);
            }
            catch
            {
                // The conversion dependency is optional for this endpoint.
            }

            if (!string.IsNullOrEmpty(traditional) &&
                !string.Equals(traditional, title, StringComparison.Ordinal)) yield return traditional;

            if (!string.IsNullOrEmpty(simplified) &&
                !string.Equals(simplified, title, StringComparison.Ordinal)) yield return simplified;
        }

        private static CachedMatch ToCachedMatch(BaseItem item)
        {
            return new CachedMatch
            {
                CachedAt = DateTime.UtcNow,
                ItemId = item.Id.ToString("N"),
                ItemType = item is Series ? nameof(Series) : item is Movie ? nameof(Movie) : null
            };
        }

        private static int? ParseYear(string year)
        {
            if (string.IsNullOrWhiteSpace(year)) return null;

            var value = year.Trim();

            if (value.Length > 4) value = value.Substring(0, 4);

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : (int?)null;
        }

        private class CachedMatch
        {
            public DateTime CachedAt { get; set; }
            public string ItemId { get; set; }
            public string ItemType { get; set; }
        }
    }
}
