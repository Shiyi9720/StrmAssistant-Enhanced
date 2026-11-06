using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Actively fills the Douban rating store that <see cref="DoubanApi"/> reads on the detail
    /// page. It deliberately writes through the same <see cref="DoubanRatingStore"/> (same file,
    /// same entry shape), so a rating cached here is immediately visible to the badge without any
    /// change to the read path.
    /// Requests are serialized and spaced out exactly like the passive supplement path. Douban
    /// blocks aggressive clients, so a 403/418/429 pauses the whole prebuild run for ten minutes;
    /// the scheduled task checks <see cref="IsTemporarilyBlocked"/> and stops instead of hammering.
    /// </summary>
    public class DoubanCacheApi
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
        private static readonly SemaphoreSlim _requestGate = new SemaphoreSlim(1, 1);

        private static DateTime _lastRequestUtc = DateTime.MinValue;
        private static DateTime _blockedUntilUtc = DateTime.MinValue;

        // Shared-limiter hooks. DoubanApi can point its own gate and timestamps here so the
        // passive supplement path and this scheduled prebuild path never hit Douban at the same
        // time. The tiny DoubanApi diff is listed in the batch report; prebuild works without it,
        // but applying it removes the only remaining chance of two concurrent request streams.
        internal static SemaphoreSlim SharedRequestGate => _requestGate;

        internal static DateTime LastRequestUtc
        {
            get => _lastRequestUtc;
            set => _lastRequestUtc = value;
        }

        internal static DateTime BlockedUntilUtc
        {
            get => _blockedUntilUtc;
            set => _blockedUntilUtc = value;
        }

        private readonly ILogger _logger;
        private readonly IHttpClient _httpClient;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly DoubanRatingStore _ratingStore;

        public DoubanCacheApi(IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = Plugin.Instance.Logger;
            _jsonSerializer = jsonSerializer;
            _httpClient = ProxyApi.Wrap(httpClient);
            _ratingStore = new DoubanRatingStore(jsonSerializer);
        }

        /// <summary>
        /// True when Douban has rate limited us and the prebuild run should stop for now.
        /// </summary>
        public static bool IsTemporarilyBlocked => DateTime.UtcNow < _blockedUntilUtc;

        /// <summary>
        /// Fetches and caches the Douban rating of a movie or series when it is not cached yet.
        /// Returns the cached entry, or null when the item has no rating or no match was found.
        /// </summary>
        public async Task<DoubanRatingCacheEntry> PrebuildRatingAsync(BaseItem item,
            CancellationToken cancellationToken)
        {
            if (item is null || !(item is Movie || item is Series)) return null;

            var cached = _ratingStore.Get(item.InternalId);

            if (cached != null) return cached;

            var subjectType = item is Movie ? "movie" : "tv";
            var detail = await FindDetailAsync(item, subjectType, cancellationToken).ConfigureAwait(false);

            var rating = detail?.rating?.value;

            if (!rating.HasValue || rating.Value <= 0) return null;

            _ratingStore.Save(item.InternalId, detail.id, item.Name, item.GetType().Name, rating,
                detail.rating.count);

            return _ratingStore.Get(item.InternalId);
        }

        private async Task<DoubanDetail> FindDetailAsync(BaseItem item, string subjectType,
            CancellationToken cancellationToken)
        {
            // A Douban id written by the supplement path lets us skip the search request entirely.
            var doubanId = item.GetProviderId("Douban");

            if (!string.IsNullOrWhiteSpace(doubanId))
            {
                var detail = await GetDetailAsync(doubanId.Trim(), subjectType, cancellationToken)
                    .ConfigureAwait(false);

                if (detail != null) return detail;

                if (IsTemporarilyBlocked) return null;
            }

            var queries = new[] { item.Name, item.OriginalTitle }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var query in queries)
            {
                var target = await SearchAsync(query, subjectType, item.ProductionYear, cancellationToken)
                    .ConfigureAwait(false);

                if (target is null || string.IsNullOrWhiteSpace(target.id)) continue;

                var detail = await GetDetailAsync(target.id, subjectType, cancellationToken)
                    .ConfigureAwait(false);

                if (detail != null) return detail;

                if (IsTemporarilyBlocked) return null;
            }

            return null;
        }

        private async Task<DoubanSearchTarget> SearchAsync(string query, string subjectType, int? year,
            CancellationToken cancellationToken)
        {
            var url = DoubanApiBase + "/search?q=" + Uri.EscapeDataString(query) + "&type=" + subjectType +
                      "&start=0&count=10";

            var response = await GetJsonAsync<DoubanSearchResponse>(url, cancellationToken).ConfigureAwait(false);
            var targets = response?.items?.Select(i => i.target)
                .Where(t => t != null && !string.IsNullOrEmpty(t.id)).ToList();

            if (targets is null || targets.Count == 0) return null;

            // Same matching rules as the passive supplement path so both paths cache the same
            // subject for the same item.
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
            return await GetJsonAsync<DoubanDetail>(
                    DoubanApiBase + "/" + subjectType + "/" + subjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken) where T : class
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);

                if (IsTemporarilyBlocked)
                {
                    _logger?.Debug("DoubanCachePrebuild - Temporarily blocked, skipping request");
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
                    _blockedUntilUtc = DateTime.UtcNow.AddMinutes(10);
                    _logger?.Info("DoubanCachePrebuild - Request blocked with " + (int)response.StatusCode +
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
                _logger?.Debug("DoubanCachePrebuild - Request failed: " + e.Message);
                return null;
            }
            finally
            {
                _lastRequestUtc = DateTime.UtcNow;
                _requestGate.Release();
            }
        }

        private static async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
        {
            var interval = DoubanCacheOptions.GetEffective().GetRequestIntervalSeconds();

            var elapsed = DateTime.UtcNow - _lastRequestUtc;
            var remaining = TimeSpan.FromSeconds(interval) - elapsed;

            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }

            // Random jitter keeps a fixed request cadence from looking like a bot.
            await Task.Delay(Random.Next(0, 500), cancellationToken).ConfigureAwait(false);
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
        }

        private class DoubanDetail
        {
            public string id { get; set; }
            public string title { get; set; }
            public DoubanRating rating { get; set; }
        }

        private class DoubanRating
        {
            public double? value { get; set; }
            public int? count { get; set; }
        }
    }
}
