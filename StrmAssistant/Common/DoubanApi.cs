using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Fills in a missing Chinese overview and primary image from Douban. It only ever adds
    /// values that Emby does not already have, and it degrades silently: Douban rate limits
    /// aggressively, so requests are serialized, spaced out and never retried in a loop.
    /// </summary>
    public class DoubanApi
    {
        private const string DoubanApiBase = "https://m.douban.com/rexxar/api/v2";
        private const string DoubanReferer = "https://m.douban.com/";

        private static readonly string[] UserAgents =
        {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15",
            "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1",
            "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Mobile Safari/537.36"
        };

        private static readonly Random Random = new Random();
        private static readonly SemaphoreSlim RequestGate = new SemaphoreSlim(1, 1);
        private static readonly LruCache DetailCache = new LruCache(200);
        private static readonly LruCache RecommendationCache = new LruCache(16);
        private static readonly TimeSpan RecommendationCacheTime = TimeSpan.FromMinutes(15);

        private static DateTime _lastRequestUtc = DateTime.MinValue;
        private static DateTime _blockedUntilUtc = DateTime.MinValue;

        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILibraryManager _libraryManager;
        private readonly IJsonSerializer _jsonSerializer;

        public DoubanApi(IProviderManager providerManager, IFileSystem fileSystem, ILibraryManager libraryManager,
            IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = Plugin.Instance.Logger;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
            _httpClient = ProxyApi.Wrap(httpClient);
        }

        public async Task SupplementAsync(BaseItem item, CancellationToken cancellationToken)
        {
            try
            {
                var options = Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions;

                if (!options.EnableDoubanSupplement || item is null || item.IsLocked) return;

                if (!(item is Movie || item is Series)) return;

                var preferredLanguage = Plugin.MetadataApi.GetPreferredMetadataLanguage(item);

                if (string.IsNullOrEmpty(preferredLanguage) ||
                    !preferredLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return;

                var needsOverview = string.IsNullOrWhiteSpace(item.Overview);
                var needsPoster = item.GetImageInfo(ImageType.Primary, 0) is null;

                if (!needsOverview && !needsPoster) return;

                var detail = await FindDetailAsync(item, cancellationToken).ConfigureAwait(false);

                if (detail is null) return;

                if (needsOverview && !string.IsNullOrWhiteSpace(detail.intro))
                {
                    item.Overview = detail.intro.Trim();
                    item.UpdateToRepository(ItemUpdateType.MetadataEdit);

                    _logger?.Info("DoubanSupplement - Overview updated: " + item.Name);
                }

                if (needsPoster && !string.IsNullOrWhiteSpace(detail.cover_url) &&
                    item.GetImageInfo(ImageType.Primary, 0) is null)
                {
                    await SavePosterAsync(item, detail.cover_url, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Failed: " + e.Message);
                _logger?.Debug(e.StackTrace);
            }
        }

        private async Task<DoubanDetail> FindDetailAsync(BaseItem item, CancellationToken cancellationToken)
        {
            var isMovie = item is Movie;
            var subjectType = isMovie ? "movie" : "tv";

            var queries = new[] { item.Name, item.OriginalTitle }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var query in queries)
            {
                var target = await SearchAsync(query, subjectType, item.ProductionYear, cancellationToken)
                    .ConfigureAwait(false);

                if (target is null || string.IsNullOrWhiteSpace(target.id)) continue;

                var detail = await GetDetailAsync(target.id, subjectType, cancellationToken).ConfigureAwait(false);

                if (detail != null) return detail;
            }

            return null;
        }

        private async Task<DoubanSearchTarget> SearchAsync(string query, string subjectType, int? year,
            CancellationToken cancellationToken)
        {
            var url = DoubanApiBase + "/search?q=" + Uri.EscapeDataString(query) + "&type=" + subjectType +
                      "&start=0&count=10";

            var response = await GetJsonAsync<DoubanSearchResponse>(url, cancellationToken).ConfigureAwait(false);
            var targets = response?.items?.Select(i => i.target).Where(t => t != null && !string.IsNullOrEmpty(t.id))
                .ToList();

            if (targets is null || targets.Count == 0) return null;

            if (year.HasValue)
            {
                var sameYear = targets.FirstOrDefault(t =>
                    int.TryParse(t.year, out var targetYear) && targetYear == year.Value);

                if (sameYear != null) return sameYear;
            }

            var exactTitle = targets.FirstOrDefault(t =>
                string.Equals(t.title, query, StringComparison.OrdinalIgnoreCase));

            return exactTitle ?? targets[0];
        }

        private async Task<DoubanDetail> GetDetailAsync(string subjectId, string subjectType,
            CancellationToken cancellationToken)
        {
            var cacheKey = subjectType + ":" + subjectId;

            if (DetailCache.TryGetFromCache(cacheKey, out DoubanDetail cached)) return cached;

            var detail = await GetJsonAsync<DoubanDetail>(DoubanApiBase + "/" + subjectType + "/" + subjectId,
                cancellationToken).ConfigureAwait(false);

            if (detail != null) DetailCache.AddOrUpdateCache(cacheKey, detail);

            return detail;
        }

        /// <summary>
        /// Returns Douban's hot movie list. The mobile collection endpoints are public and can
        /// rate limit or disappear; callers receive an empty list instead of an exception.
        /// </summary>
        public async Task<List<DoubanRecommendation>> GetRecommendationsAsync(string kind, int limit,
            CancellationToken cancellationToken)
        {
            limit = Math.Max(1, Math.Min(limit, 40));
            kind = string.IsNullOrWhiteSpace(kind) ? "hot" : kind.Trim().ToLowerInvariant();

            var cacheKey = "recommendations:" + kind + ":" + limit;

            if (RecommendationCache.TryGetFromCache<DoubanRecommendationCache>(cacheKey, out var cached) &&
                DateTime.UtcNow - cached.CachedAt < RecommendationCacheTime)
            {
                return cached.Items;
            }

            var items = await GetCollectionRecommendationsAsync(kind, limit, cancellationToken)
                .ConfigureAwait(false);

            if (items.Count == 0 && kind != "showing")
            {
                items = await GetCollectionRecommendationsAsync("showing", limit, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (items.Count == 0)
            {
                items = await GetRecommendFeedAsync(limit, cancellationToken).ConfigureAwait(false);
            }

            items = items
                .Where(item => !string.IsNullOrEmpty(item.DoubanId) && !string.IsNullOrEmpty(item.Title))
                .GroupBy(item => item.DoubanId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(limit)
                .ToList();

            if (items.Count > 0)
            {
                RecommendationCache.AddOrUpdateCache(cacheKey, new DoubanRecommendationCache
                {
                    CachedAt = DateTime.UtcNow,
                    Items = items
                });
            }

            return items;
        }

        private async Task<List<DoubanRecommendation>> GetCollectionRecommendationsAsync(string kind, int limit,
            CancellationToken cancellationToken)
        {
            var collection = kind == "showing" ? "movie_showing" : "movie_hot";
            var url = DoubanApiBase + "/subject_collection/" + collection + "/items?start=0&count=" + limit;

            var response = await GetJsonAsync<DoubanSubjectCollectionResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            return response?.subject_collection_items?
                .Select(ConvertSubject)
                .Where(item => item != null)
                .ToList() ?? new List<DoubanRecommendation>();
        }

        private async Task<List<DoubanRecommendation>> GetRecommendFeedAsync(int limit,
            CancellationToken cancellationToken)
        {
            var url = DoubanApiBase + "/movie/recommend?start=0&count=" + Math.Min(limit * 3, 60);
            var response = await GetJsonAsync<DoubanRecommendFeedResponse>(url, cancellationToken)
                .ConfigureAwait(false);

            if (response?.items is null) return new List<DoubanRecommendation>();

            return response.items
                .Where(item => string.Equals(item.type, "movie", StringComparison.OrdinalIgnoreCase))
                .Select(ConvertFeedItem)
                .Where(item => item != null)
                .ToList();
        }

        private static DoubanRecommendation ConvertSubject(DoubanSubject subject)
        {
            if (subject is null || string.IsNullOrEmpty(subject.id)) return null;

            return new DoubanRecommendation
            {
                DoubanId = subject.id,
                Title = subject.title,
                OriginalTitle = subject.original_title,
                Year = subject.year,
                CoverUrl = NormalizeCoverUrl(subject.cover?.url),
                Subtitle = subject.card_subtitle,
                Rating = subject.rating?.value
            };
        }

        private static DoubanRecommendation ConvertFeedItem(DoubanFeedItem item)
        {
            if (item is null || string.IsNullOrEmpty(item.id)) return null;

            var coverUrl = item.pic?.large;

            if (string.IsNullOrEmpty(coverUrl)) coverUrl = item.pic?.normal;
            if (string.IsNullOrEmpty(coverUrl)) coverUrl = item.cover_url;

            return new DoubanRecommendation
            {
                DoubanId = item.id,
                Title = item.title,
                OriginalTitle = item.original_title,
                Year = item.year,
                CoverUrl = NormalizeCoverUrl(coverUrl),
                Subtitle = item.card_subtitle,
                Rating = item.rating?.value
            };
        }

        private static string NormalizeCoverUrl(string coverUrl)
        {
            if (string.IsNullOrEmpty(coverUrl)) return null;

            return coverUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + coverUrl : coverUrl;
        }

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken) where T : class
        {
            // The gate stays held until the response is read so that two items refreshed at the
            // same time cannot fire concurrent requests at Douban.
            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

                if (DateTime.UtcNow < _blockedUntilUtc)
                {
                    _logger?.Debug("DoubanSupplement - Temporarily blocked, skipping request");
                    return null;
                }

                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    Referer = DoubanReferer,
                    UserAgent = UserAgents[Random.Next(UserAgents.Length)],
                    BufferContent = true,
                    LogRequest = false,
                    LogResponse = false
                };

                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Forbidden ||
                    response.StatusCode == (HttpStatusCode)418 ||
                    response.StatusCode == (HttpStatusCode)429)
                {
                    // Douban has flagged us. Back off for a while instead of retrying.
                    _blockedUntilUtc = DateTime.UtcNow.AddMinutes(10);
                    _logger?.Info("DoubanSupplement - Request blocked with " + (int)response.StatusCode +
                                 ", pausing for 10 minutes");
                    return null;
                }

                if (response.StatusCode != HttpStatusCode.OK) return null;

                await using var contentStream = response.Content;

                return _jsonSerializer.DeserializeFromStream<T>(contentStream);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Request failed: " + e.Message);
                return null;
            }
            finally
            {
                _lastRequestUtc = DateTime.UtcNow;
                RequestGate.Release();
            }
        }

        private static async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
        {
            var interval = Math.Max(2,
                Plugin.Instance.GetPluginOptions().MetadataEnhanceOptions.DoubanRequestIntervalSeconds);

            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            var remaining = TimeSpan.FromSeconds(interval) - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            // Random jitter keeps a fixed request cadence from looking like a bot.
            await Task.Delay(Random.Next(0, 500), cancellationToken).ConfigureAwait(false);
        }

        private async Task SavePosterAsync(BaseItem item, string coverUrl, CancellationToken cancellationToken)
        {
            string tempPath = null;

            try
            {
                var options = new HttpRequestOptions
                {
                    Url = coverUrl,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "image/*",
                    Referer = DoubanReferer,
                    UserAgent = UserAgents[Random.Next(UserAgents.Length)],
                    BufferContent = true,
                    LogRequest = false,
                    LogResponse = false
                };

                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK) return;

                byte[] imageBytes;

                await using (var contentStream = response.Content)
                {
                    using var memoryStream = new MemoryStream();
                    await contentStream.CopyToAsync(memoryStream, 81920, cancellationToken).ConfigureAwait(false);
                    imageBytes = memoryStream.ToArray();
                }

                if (imageBytes.Length == 0) return;

                tempPath = Path.Combine(Plugin.Instance.ApplicationPaths.TempDirectory,
                    Guid.NewGuid() + ".jpg");
                await _fileSystem.WriteAllBytesAsync(tempPath, imageBytes, CancellationToken.None)
                    .ConfigureAwait(false);

                var libraryOptions = _libraryManager.GetLibraryOptions(item);
                var directoryService = new DirectoryService(_logger, _fileSystem);

                await _providerManager.SaveImage(item, libraryOptions, tempPath, ImageType.Primary, null,
                        Array.Empty<long>(), directoryService, false, CancellationToken.None)
                    .ConfigureAwait(false);

                _logger?.Info("DoubanSupplement - Poster updated: " + item.Name);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Poster download failed: " + e.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempPath))
                {
                    try
                    {
                        _fileSystem.DeleteFile(tempPath);
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }
                }
            }
        }

        // Douban's mobile endpoints return lower case field names.
        private class DoubanSearchResponse
        {
            public List<DoubanSearchItem> items { get; set; }
        }

        private class DoubanSearchItem
        {
            public DoubanSearchTarget target { get; set; }
        }

        private class DoubanSearchTarget
        {
            public string id { get; set; }
            public string title { get; set; }
            public string year { get; set; }
            public string cover_url { get; set; }
        }

        private class DoubanDetail
        {
            public string id { get; set; }
            public string title { get; set; }
            public string intro { get; set; }
            public string cover_url { get; set; }
        }

        private class DoubanRecommendationCache
        {
            public DateTime CachedAt { get; set; }
            public List<DoubanRecommendation> Items { get; set; } = new List<DoubanRecommendation>();
        }

        private class DoubanSubjectCollectionResponse
        {
            public List<DoubanSubject> subject_collection_items { get; set; }
        }

        private class DoubanSubject
        {
            public string id { get; set; }
            public string title { get; set; }
            public string original_title { get; set; }
            public string year { get; set; }
            public string card_subtitle { get; set; }
            public DoubanCover cover { get; set; }
            public DoubanRating rating { get; set; }
        }

        private class DoubanCover
        {
            public string url { get; set; }
        }

        private class DoubanRating
        {
            public double? value { get; set; }
        }

        private class DoubanRecommendFeedResponse
        {
            public List<DoubanFeedItem> items { get; set; }
        }

        private class DoubanFeedItem
        {
            public string id { get; set; }
            public string type { get; set; }
            public string title { get; set; }
            public string original_title { get; set; }
            public string year { get; set; }
            public string card_subtitle { get; set; }
            public string cover_url { get; set; }
            public DoubanPic pic { get; set; }
            public DoubanRating rating { get; set; }
        }

        private class DoubanPic
        {
            public string large { get; set; }
            public string normal { get; set; }
        }
    }

    public class DoubanRecommendation
    {
        public string DoubanId { get; set; }
        public string Title { get; set; }
        public string OriginalTitle { get; set; }
        public string Year { get; set; }
        public string CoverUrl { get; set; }
        public string Subtitle { get; set; }
        public double? Rating { get; set; }
    }
}
