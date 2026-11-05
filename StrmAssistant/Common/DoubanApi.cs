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

                    _logger.Info("DoubanSupplement - Overview updated: " + item.Name);
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
                _logger.Debug("DoubanSupplement - Failed: " + e.Message);
                _logger.Debug(e.StackTrace);
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
                    _logger.Debug("DoubanSupplement - Temporarily blocked, skipping request");
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
                    _logger.Info("DoubanSupplement - Request blocked with " + (int)response.StatusCode +
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
                _logger.Debug("DoubanSupplement - Request failed: " + e.Message);
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

                _logger.Info("DoubanSupplement - Poster updated: " + item.Name);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger.Debug("DoubanSupplement - Poster download failed: " + e.Message);
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
    }
}
