using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using StrmAssistant.Options;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// 探索发现 Internet Channel runtime. It exposes configured Douban subject collections as
    /// channel folders, matches each entry against the local library (已在库 / 缺失), resolves a
    /// trailer on demand and posts a MoviePilot subscription when a channel item is favourited.
    ///
    /// Everything is off by default and the channel reads its settings on every request, so a
    /// settings save needs no restart. The native "Refresh Internet Channels" task clears the
    /// collection cache, which makes that button refresh the data immediately.
    /// </summary>
    public class DiscoverApi : IDisposable
    {
        private static readonly TimeSpan LibraryCacheTtl = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan TrailerCacheTtl = TimeSpan.FromHours(6);
        private static readonly TimeSpan SubscribeResultTtl = TimeSpan.FromMinutes(5);
        private const int DefaultCount = DiscoverCollectionSpecParser.DefaultCount;

        private static readonly HttpClient MoviePilotHttp = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        private readonly ILibraryManager _libraryManager;
        private readonly IUserDataManager _userDataManager;
        private readonly ITaskManager _taskManager;
        private readonly IServerApplicationHost _appHost;
        private readonly ILogger _logger;
        private readonly object _gate = new object();
        private readonly object _subscribeGate = new object();

        private readonly Dictionary<string, CollectionCacheEntry> _collections =
            new Dictionary<string, CollectionCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SubscribeCacheEntry> _subscribed =
            new Dictionary<string, SubscribeCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, byte> _favoriteSubscribed =
            new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private LibraryCacheEntry _libraryCache;
        private string _settingsSignature;

        public DiscoverApi(ILibraryManager libraryManager, IUserDataManager userDataManager,
            ITaskManager taskManager, IServerApplicationHost appHost)
        {
            _libraryManager = libraryManager;
            _userDataManager = userDataManager;
            _taskManager = taskManager;
            _appHost = appHost;
            _logger = Plugin.Instance?.Logger;

            if (_userDataManager != null) _userDataManager.UserDataSaved += OnUserDataSaved;
            if (_taskManager != null) _taskManager.TaskCompleted += OnTaskCompleted;
        }

        public long CollectionRequests;
        public long CollectionFailures;
        public long TrailerRequests;
        public long TrailerFailures;
        public long Subscribes;
        public long SubscribeFailures;

        private DiscoverOptions Options =>
            Plugin.Instance?.GetPluginOptions()?.DiscoverOptions ?? new DiscoverOptions();

        public bool IsEnabled => Options.Enabled;

        public string ChannelName
        {
            get
            {
                var name = Options.ChannelName;
                return string.IsNullOrWhiteSpace(name) ? "探索发现" : name.Trim();
            }
        }

        public async Task<ChannelItemResult> GetChannelItemsAsync(string folderId,
            CancellationToken cancellationToken)
        {
            var items = new List<ChannelItemInfo>();
            if (!IsEnabled) return new ChannelItemResult { Items = items };

            var options = Options;

            // A changed collection list or cache window must be visible immediately, not after
            // the old TTL expires.
            var signature = (options.Collections ?? string.Empty) + "|" + options.CacheMinutes + "|" +
                            ChannelName;
            lock (_gate)
            {
                if (!string.Equals(signature, _settingsSignature, StringComparison.Ordinal))
                {
                    _collections.Clear();
                    _libraryCache = null;
                    _settingsSignature = signature;
                }
            }

            try
            {
                if (string.IsNullOrEmpty(folderId) || folderId == DiscoverChannelIds.Root)
                {
                    items.AddRange(BuildRoot(options));
                }
                else if (DiscoverChannelIds.TryParseCollection(folderId, out var collectionId))
                {
                    items.AddRange(await BuildCollectionAsync(options, collectionId, cancellationToken)
                        .ConfigureAwait(false));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Warn("Discover - channel folder '" + folderId + "' failed: " + e.Message);
                items.Clear();
                items.Add(MessageFolder("探索发现暂时不可用", "sa-discover-error",
                    "加载失败：" + e.Message));
            }

            return new ChannelItemResult { Items = items };
        }

        private List<ChannelItemInfo> BuildRoot(DiscoverOptions options)
        {
            var specs = DiscoverCollectionSpecParser.Parse(options.Collections);
            if (specs.Count == 0)
            {
                return new List<ChannelItemInfo>
                {
                    MessageFolder("尚未配置豆瓣合集", "sa-discover-unconfigured",
                        "在插件设置 → 探索发现频道 → 豆瓣合集 里，每行填写一个：\n" +
                        "合集标识|名称|数量\n例如：ECMNABFUI|戛纳获奖|20\n" +
                        "合集标识来自豆瓣合集页地址中的 subject_collection/<标识>。")
                };
            }

            var result = new List<ChannelItemInfo>();
            foreach (var spec in specs)
            {
                var cached = TryGetCachedCollection(spec.Id, options);
                var image = cached?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.CoverUrl))
                    ?.CoverUrl;

                result.Add(new ChannelItemInfo
                {
                    Id = DiscoverChannelIds.Collection(spec.Id),
                    Name = spec.DisplayName,
                    Type = ChannelItemType.Folder,
                    FolderType = ChannelFolderType.Container,
                    MediaType = ChannelMediaType.Video,
                    Overview = "豆瓣合集 " + spec.Id + " · 配置数量 " + spec.Count +
                               (cached != null ? " · 已缓存 " + cached.Count + " 条" : string.Empty),
                    ImageUrl = BuildImageUrl(image),
                    ProviderIds = new ProviderIdDictionary { ["DoubanCollection"] = spec.Id }
                });
            }

            return result;
        }

        private async Task<List<ChannelItemInfo>> BuildCollectionAsync(DiscoverOptions options,
            string collectionId, CancellationToken cancellationToken)
        {
            var spec = DiscoverCollectionSpecParser.Parse(options.Collections)
                .FirstOrDefault(s => string.Equals(s.Id, collectionId, StringComparison.OrdinalIgnoreCase));
            var count = spec?.Count ?? DefaultCount;

            var page = await GetCollectionAsync(collectionId, count, options, cancellationToken)
                .ConfigureAwait(false);

            if (page == null)
            {
                return new List<ChannelItemInfo>
                {
                    MessageFolder("豆瓣合集加载失败", DiscoverChannelIds.Collection(collectionId),
                        "合集 " + collectionId + " 拉取失败或暂时不可用。" +
                        "\n\nEmby 的「刷新 Internet Channel」计划任务会重新拉取。")
                };
            }

            var index = GetLibraryIndex();
            var items = page.Take(count).ToList();
            var result = new List<ChannelItemInfo>(items.Count);

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.DoubanId)) continue;

                var kind = string.Equals(item.Kind, "tv", StringComparison.OrdinalIgnoreCase)
                    ? "tv"
                    : "movie";
                var year = ParseYear(item.Year) ?? DiscoverText.YearFromSubtitle(item.Subtitle);
                var match = index.Match(item.DoubanId, item.Title, year, kind);
                var overview = DiscoverEntryProjection.BuildOverview(item.Description, item.Subtitle, match);
                if (options.SubscribeEnabled)
                {
                    overview += "\n\n在条目详情页点心形收藏即可订阅到 MoviePilot。";
                }

                result.Add(new ChannelItemInfo
                {
                    Id = DiscoverChannelIds.Entry(item.DoubanId, kind),
                    Name = item.Title,
                    Type = ChannelItemType.Media,
                    MediaType = ChannelMediaType.Video,
                    // Clip (not Trailer): Emby maps ContentType value 2 to a Photo item, which
                    // hides overview/rating and cannot be played.
                    ContentType = ChannelMediaContentType.Clip,
                    ForceUpdate = true,
                    Overview = overview,
                    Tagline = DiscoverEntryProjection.BuildTagline(match),
                    CommunityRating = item.Rating.HasValue ? (float?)item.Rating.Value : null,
                    ProductionYear = year,
                    ImageUrl = BuildImageUrl(item.CoverUrl),
                    ProviderIds = new ProviderIdDictionary
                    {
                        ["Douban"] = item.DoubanId,
                        [DiscoverMarker.ProviderKey] = "1",
                        [DiscoverMarker.KindKey] = kind
                    }
                });
            }

            if (result.Count == 0)
            {
                result.Add(MessageFolder("合集暂无条目", DiscoverChannelIds.Collection(collectionId),
                    "豆瓣合集 " + collectionId + " 返回了 0 条可展示的条目。"));
            }

            return result;
        }

        /// <summary>
        /// Emby fetches the channel item image without a Referer, which Douban's CDN rejects
        /// with 418. Point the item at the plugin's own relay so the image renders.
        /// </summary>
        private string BuildImageUrl(string coverUrl)
        {
            if (!DiscoverImageProxy.TryNormalize(coverUrl, out _)) return null;

            var port = _appHost?.HttpPort ?? 8096;
            return "http://127.0.0.1:" + port + DiscoverImageProxy.Route + "?Url=" +
                   Uri.EscapeDataString(coverUrl.Trim());
        }

        private static int? ParseYear(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var text = value.Trim();
            if (text.Length < 4) return null;
            return int.TryParse(text.Substring(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var year)
                ? (int?)year
                : null;
        }

        private async Task<List<DoubanRecommendation>> GetCollectionAsync(string collectionId, int count,
            DiscoverOptions options, CancellationToken cancellationToken)
        {
            var cached = TryGetCachedCollection(collectionId, options);
            if (cached != null) return cached;

            Interlocked.Increment(ref CollectionRequests);

            List<DoubanRecommendation> items;
            try
            {
                items = await Plugin.DoubanApi
                    .GetCollectionAsync(collectionId, count, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Warn("Discover - collection " + collectionId + " failed: " + e.Message);
                items = null;
            }

            if (items == null || items.Count == 0)
            {
                Interlocked.Increment(ref CollectionFailures);
                return null;
            }

            lock (_gate)
            {
                _collections[collectionId] = new CollectionCacheEntry
                {
                    AtUtc = DateTimeOffset.UtcNow,
                    Items = items
                };
            }

            return items;
        }

        private List<DoubanRecommendation> TryGetCachedCollection(string collectionId,
            DiscoverOptions options)
        {
            if (string.IsNullOrWhiteSpace(collectionId)) return null;

            lock (_gate)
            {
                if (!_collections.TryGetValue(collectionId, out var cached)) return null;

                var ttl = TimeSpan.FromMinutes(Math.Max(1, options?.CacheMinutes ?? 30));
                return DateTimeOffset.UtcNow - cached.AtUtc <= ttl ? cached.Items : null;
            }
        }

        private DiscoverLibraryIndex GetLibraryIndex()
        {
            lock (_gate)
            {
                if (_libraryCache != null &&
                    DateTimeOffset.UtcNow - _libraryCache.AtUtc < LibraryCacheTtl)
                {
                    return _libraryCache.Index;
                }

                var entries = new List<DiscoverLibraryEntry>();
                try
                {
                    var items = _libraryManager?.GetItemList(new InternalItemsQuery
                    {
                        Recursive = true,
                        IncludeItemTypes = new[] { "Movie", "Series" }
                    }) ?? Array.Empty<BaseItem>();

                    foreach (var item in items)
                    {
                        if (item == null) continue;

                        entries.Add(new DiscoverLibraryEntry
                        {
                            ItemId = item.InternalId,
                            Name = item.Name,
                            OriginalTitle = item.OriginalTitle,
                            Year = item.ProductionYear,
                            DoubanId = GetDoubanId(item),
                            Kind = item is Series ? "tv" : "movie"
                        });
                    }
                }
                catch (Exception e)
                {
                    _logger?.Warn("Discover - library index failed: " + e.Message);
                }

                var index = new DiscoverLibraryIndex(entries);
                _libraryCache = new LibraryCacheEntry
                {
                    AtUtc = DateTimeOffset.UtcNow,
                    Index = index
                };

                _logger?.Debug("Discover - library index rebuilt: " + index.Count + " items");
                return index;
            }
        }

        private static string GetDoubanId(BaseItem item)
        {
            try
            {
                if (item.ProviderIds != null &&
                    item.ProviderIds.TryGetValue("Douban", out var value) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
            catch
            {
                // Provider id dictionaries are plain; a malformed one must not break the channel.
            }

            return null;
        }

        /// <summary>
        /// Called by Emby when a channel item is played. Douban trailers are resolved here and
        /// streamed from Douban's CDN; nothing is downloaded or persisted.
        /// </summary>
        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesAsync(string id,
            CancellationToken cancellationToken)
        {
            if (!IsEnabled || !Options.TrailerEnabled) return Array.Empty<MediaSourceInfo>();
            if (!DiscoverChannelIds.TryParseEntry(id, out var kind, out var doubanId))
                return Array.Empty<MediaSourceInfo>();

            Interlocked.Increment(ref TrailerRequests);

            DoubanTrailer trailer;
            try
            {
                trailer = await Plugin.DoubanApi
                    .GetTrailerAsync(doubanId, kind, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Warn("Discover - trailer " + doubanId + " failed: " + e.Message);
                trailer = null;
            }

            if (trailer == null || string.IsNullOrWhiteSpace(trailer.Url))
            {
                Interlocked.Increment(ref TrailerFailures);
                return Array.Empty<MediaSourceInfo>();
            }

            return new[] { BuildTrailerSource(doubanId, trailer) };
        }

        private static MediaSourceInfo BuildTrailerSource(string doubanId, DoubanTrailer trailer)
        {
            return new MediaSourceInfo
            {
                Id = "sa-discover-trailer-" + doubanId,
                Name = string.IsNullOrWhiteSpace(trailer.Title) ? "豆瓣预告片" : trailer.Title,
                Path = trailer.Url,
                Protocol = MediaProtocol.Http,
                Container = "mp4",
                IsRemote = true,
                SupportsDirectPlay = true,
                // Douban's CDN answers 418 to Emby's proxy (no Referer/UA); clients must play
                // the URL directly instead of asking Emby to relay it.
                SupportsDirectStream = false,
                SupportsTranscoding = false,
                RunTimeTicks = DiscoverText.ParseRuntimeTicks(trailer.Runtime),
                MediaStreams = new List<MediaStream>
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Video,
                        Codec = "h264",
                        Index = 0,
                        IsDefault = true
                    },
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Codec = "aac",
                        Index = 1,
                        IsDefault = true
                    }
                }
            };
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                if (!IsEnabled) return;

                var item = e?.Item;
                var user = e?.User;
                if (item?.ProviderIds == null || user == null) return;
                if (!item.ProviderIds.TryGetValue(DiscoverMarker.ProviderKey, out var marker) ||
                    marker != "1")
                {
                    return;
                }

                var key = user.Id + ":" + item.InternalId;
                if (e.UserData?.IsFavorite != true)
                {
                    lock (_subscribeGate) _favoriteSubscribed.Remove(key);
                    return;
                }

                var options = Options;
                if (!options.SubscribeEnabled)
                {
                    lock (_subscribeGate) _favoriteSubscribed.Remove(key);
                    return;
                }

                lock (_subscribeGate)
                {
                    if (_favoriteSubscribed.ContainsKey(key)) return;
                    _favoriteSubscribed[key] = 1;
                }

                var doubanId = GetDoubanId(item);
                if (string.IsNullOrWhiteSpace(doubanId)) return;

                var kind = item.ProviderIds.TryGetValue(DiscoverMarker.KindKey, out var kindText) &&
                           string.Equals(kindText, "tv", StringComparison.OrdinalIgnoreCase)
                    ? "tv"
                    : "movie";

                var title = item.Name;
                var year = item.ProductionYear;
                var rating = item.CommunityRating.HasValue
                    ? (double?)item.CommunityRating.Value
                    : null;

                _logger?.Info("Discover - favorite -> subscribe " + doubanId + " (" + title + ")");
                _ = Task.Run(() => SubscribeAsync(options, doubanId, title, kind, year, rating, null,
                    CancellationToken.None));
            }
            catch (Exception ex)
            {
                _logger?.Warn("Discover - favorite hook failed: " + ex.Message);
            }
        }

        public async Task<DiscoverSubscribeResult> SubscribeAsync(DiscoverOptions options,
            string doubanId, string title, string kind, int? year, double? rating, string poster,
            CancellationToken cancellationToken)
        {
            if (options == null || !options.SubscribeEnabled)
            {
                return new DiscoverSubscribeResult { Success = false, Message = "收藏/订阅未开启" };
            }

            if (string.IsNullOrWhiteSpace(doubanId))
            {
                return new DiscoverSubscribeResult { Success = false, Message = "条目缺少豆瓣 ID" };
            }

            var cacheKey = doubanId;
            lock (_subscribeGate)
            {
                if (_subscribed.TryGetValue(cacheKey, out var cached) &&
                    DateTimeOffset.UtcNow - cached.AtUtc < SubscribeResultTtl)
                {
                    return cached.Result;
                }
            }

            var webhook = options.SubscribeMode == DiscoverSubscribeMode.Webhook;
            var url = webhook
                ? DiscoverSubscribe.BuildWebhookUrl(options.MoviePilotUrl, options.MoviePilotToken)
                : DiscoverSubscribe.BuildApiUrl(options.MoviePilotUrl, options.MoviePilotToken);

            if (string.IsNullOrWhiteSpace(url))
            {
                return new DiscoverSubscribeResult { Success = false, Message = "未配置 MoviePilot 地址" };
            }

            var body = webhook
                ? DiscoverSubscribe.BuildWebhookPayload(doubanId, title, kind, year, poster, rating)
                : DiscoverSubscribe.BuildApiPayload(doubanId, title, kind, year, poster, rating);

            DiscoverSubscribeResult result;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };

                using var response = await MoviePilotHttp
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false);

                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                result = DiscoverSubscribe.ParseResponse((int)response.StatusCode, text);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                result = new DiscoverSubscribeResult
                {
                    Success = false,
                    Message = "请求 MoviePilot 失败：" + e.Message
                };
            }

            result.RequestUrl = url;
            result.RequestBody = body;

            Interlocked.Increment(ref Subscribes);
            if (!result.Success) Interlocked.Increment(ref SubscribeFailures);

            _logger?.Info("Discover - subscribe " + doubanId + " (" + title + ") -> " +
                          result.StatusCode + " " +
                          (result.Success ? "ok" : "failed: " + result.Message));

            lock (_subscribeGate)
            {
                _subscribed[cacheKey] = new SubscribeCacheEntry
                {
                    AtUtc = DateTimeOffset.UtcNow,
                    Result = result
                };
            }

            return result;
        }

        /// <summary>The native "Refresh Internet Channels" task must bypass the cache.</summary>
        private void OnTaskCompleted(object sender, TaskCompletionEventArgs e)
        {
            try
            {
                var worker = e?.Task;
                var task = worker?.ScheduledTask;
                var key = task?.Key ?? string.Empty;
                var name = task?.Name ?? worker?.Name ?? string.Empty;

                if (key.IndexOf("Channel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Channel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("频道", StringComparison.Ordinal) >= 0)
                {
                    InvalidateCollections();
                    _logger?.Info("Discover - collection cache cleared after task: " + name);
                }
            }
            catch
            {
                // A task event must never throw into Emby's task runner.
            }
        }

        public void InvalidateCollections()
        {
            lock (_gate)
            {
                _collections.Clear();
                _libraryCache = null;
            }
        }

        /// <summary>
        /// Emby keys a channel entity by its name. When the configured name changes the old
        /// entity lingers, so the previous name is deleted on save. Best effort: only an item
        /// whose type is Channel and whose name matches exactly is touched.
        /// </summary>
        public void RemoveStaleChannel(string oldName)
        {
            if (string.IsNullOrWhiteSpace(oldName) ||
                string.Equals(oldName, ChannelName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var items = _libraryManager?.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = new[] { "Channel" }
                }) ?? Array.Empty<BaseItem>();

                foreach (var item in items)
                {
                    if (item == null || !string.Equals(item.Name, oldName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
                    _logger?.Info("Discover - removed stale channel entity: " + oldName);
                }
            }
            catch (Exception e)
            {
                _logger?.Warn("Discover - stale channel cleanup failed (" + oldName + "): " + e.Message);
            }
        }

        private static ChannelItemInfo MessageFolder(string name, string id, string overview)
        {
            return new ChannelItemInfo
            {
                Id = id,
                Name = name,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
                MediaType = ChannelMediaType.Video,
                Overview = overview
            };
        }

        public void Dispose()
        {
            if (_userDataManager != null) _userDataManager.UserDataSaved -= OnUserDataSaved;
            if (_taskManager != null) _taskManager.TaskCompleted -= OnTaskCompleted;
        }

        private sealed class CollectionCacheEntry
        {
            public DateTimeOffset AtUtc { get; set; }
            public List<DoubanRecommendation> Items { get; set; }
        }

        private sealed class LibraryCacheEntry
        {
            public DateTimeOffset AtUtc { get; set; }
            public DiscoverLibraryIndex Index { get; set; }
        }

        private sealed class SubscribeCacheEntry
        {
            public DateTimeOffset AtUtc { get; set; }
            public DiscoverSubscribeResult Result { get; set; }
        }
    }
}
