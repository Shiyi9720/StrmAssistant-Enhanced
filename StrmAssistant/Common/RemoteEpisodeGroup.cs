using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Fetches TMDB episode groups and feeds the same season/episode mapping hook as
    /// <see cref="LocalEpisodeGroup"/>. Local episodegroup.json files always take precedence,
    /// remote groups are the second source, and when neither has a mapping the provider keeps
    /// its native order.
    /// </summary>
    internal static class RemoteEpisodeGroup
    {
        private const string CacheFolderName = "TmdbEpisodeGroupCache";
        private const int CacheVersion = 1;
        private const int RequestTimeoutMs = 15000;

        private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static readonly Regex SeriesRequestRegex =
            new Regex(@"^/3/tv/(\d+)(?:/|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // The name of a TMDB group usually identifies the local season it represents. The
        // fallback (first group = season 1, second = season 2) matches how libraries are laid
        // out when an episode group is used instead of the default air-date order.
        private static readonly Regex SeasonNameRegex = new Regex(
            @"(?:season|series|staffel|temporada|saison|stagione|seizoen|säsong|sesong|сезон)\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SpecialSeasonRegex = new Regex(
            @"(special|specials|特别|特別|スペシャル|스페셜)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly ConcurrentDictionary<string, CacheEntry> MemoryCache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> SeriesGates =
            new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> ScopeCache =
            new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, DateTime> Failures =
            new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static ILibraryManager _libraryManager;
        private static IJsonSerializer _jsonSerializer;
        private static IHttpClient _httpClient;
        private static ILogger _logger;

        public static void Initialize(ILibraryManager libraryManager, IJsonSerializer jsonSerializer,
            IHttpClient httpClient, ILogger logger)
        {
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
            _httpClient = ProxyApi.Wrap(httpClient);
            _logger = logger;
        }

        public static bool IsEnabled => GetOptions()?.MovieDbEpisodeGroup == true;

        /// <summary>
        /// Called from the patched TMDB HTTP client before the provider request is prepared.
        /// Only a TV series request can need episode group data; everything else returns
        /// immediately.
        /// </summary>
        public static async Task EnsureLoadedAsync(string url, CancellationToken cancellationToken)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(url)) return;

            var seriesId = GetSeriesId(url);

            if (seriesId is null) return;

            try
            {
                await EnsureEntryAsync(seriesId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The provider request was cancelled; the caller decides what to do next.
            }
            catch (Exception e)
            {
                _logger?.Warn("TMDB episode group - Prefetch for series {0} failed: {1}",
                    seriesId, e.Message);
            }
        }

        /// <summary>
        /// Synchronous lookup used by the shared mapping hook. It never performs network I/O;
        /// when no fresh group data is available the caller falls back to the native order.
        /// </summary>
        public static LocalEpisodeGroupMapping TryMapRequest(string url)
        {
            if (!IsEnabled || string.IsNullOrWhiteSpace(url)) return null;

            try
            {
                var seriesId = GetSeriesId(url);

                if (seriesId is null) return null;

                var entry = GetEntryForMapping(seriesId);

                if (entry is null) return null;

                return LocalEpisodeGroup.TryMapUrl(url, _ => entry.Document, "remote");
            }
            catch (Exception e)
            {
                _logger?.Debug("TMDB episode group - Request mapping failed: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Synchronous reverse lookup for override image names. Missing or stale data simply
        /// returns false, leaving the caller on the native season/episode numbers.
        /// </summary>
        public static bool TryGetTmdbNumbers(string seriesId, int localSeason, int? localEpisode,
            out int tmdbSeason, out int? tmdbEpisode)
        {
            tmdbSeason = localSeason;
            tmdbEpisode = localEpisode;

            if (!IsEnabled || string.IsNullOrWhiteSpace(seriesId)) return false;

            try
            {
                var entry = GetEntryForMapping(seriesId);

                return entry != null &&
                       LocalEpisodeGroup.TryGetTmdbNumbers(entry.Document, localSeason, localEpisode,
                           out tmdbSeason, out tmdbEpisode);
            }
            catch (Exception e)
            {
                _logger?.Debug("TMDB episode group - Reverse mapping failed: " + e.Message);
                return false;
            }
        }

        private static async Task<CacheEntry> EnsureEntryAsync(string seriesId,
            CancellationToken cancellationToken)
        {
            var entry = GetFreshEntry(seriesId);

            if (entry != null) return entry;

            var gate = SeriesGates.GetOrAdd(seriesId, _ => new SemaphoreSlim(1, 1));

            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                entry = GetFreshEntry(seriesId);

                if (entry != null) return entry;

                if (Failures.TryGetValue(seriesId, out var retryAtUtc) && retryAtUtc > DateTime.UtcNow)
                {
                    return null;
                }

                if (IsOutOfScopeFast(seriesId)) return null;

                var series = ResolveSeries(seriesId);

                if (!IsInScope(seriesId, series)) return null;

                var diskEntry = LoadDiskEntry(seriesId);

                if (diskEntry != null)
                {
                    MemoryCache[seriesId] = diskEntry;

                    if (diskEntry.IsFresh && diskEntry.MatchesSource) return diskEntry;
                }

                var fetched = await FetchAsync(seriesId, series, cancellationToken).ConfigureAwait(false);

                if (fetched != null)
                {
                    MemoryCache[seriesId] = fetched;
                    Failures.TryRemove(seriesId, out _);
                    SaveDiskEntry(seriesId, fetched);
                    return fetched;
                }

                Failures[seriesId] = DateTime.UtcNow.Add(FailureCooldown);
                return null;
            }
            finally
            {
                gate.Release();
            }
        }

        private static async Task<CacheEntry> FetchAsync(string seriesId, Series series,
            CancellationToken cancellationToken)
        {
            var apiRoot = MovieDbApi.GetEffectiveApiRoot().TrimEnd('/');
            var apiKey = MovieDbApi.GetEffectiveApiKey();

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger?.Warn(
                    "TMDB episode group - No TMDB API key is available for series {0}; native scraping is used.",
                    seriesId);
                return null;
            }

            var language = ResolveLanguage(series);

            var listUrl = BuildUrl(apiRoot, "/3/tv/" + seriesId + "/episode_groups", apiKey, language);
            var listBody = await GetJsonAsync(listUrl, language, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(listBody))
            {
                _logger?.Warn(
                    "TMDB episode group - Unable to read the episode group list for series {0}; native scraping is used.",
                    seriesId);
                return null;
            }

            EpisodeGroupListResponse list;

            try
            {
                list = _jsonSerializer.DeserializeFromString<EpisodeGroupListResponse>(listBody);
            }
            catch (Exception e)
            {
                _logger?.Warn(
                    "TMDB episode group - Invalid episode group list for series {0}; native scraping is used. {1}",
                    seriesId, e.Message);
                return null;
            }

            var candidates = list?.results?
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.id))
                .ToList();

            if (candidates is null || candidates.Count == 0)
            {
                _logger?.Warn(
                    "TMDB episode group - Series {0} has no episode groups; native scraping is used.",
                    seriesId);
                return null;
            }

            if (Plugin.Instance?.GetPluginOptions()?.AboutOptions.DebugMode == true)
            {
                foreach (var candidate in candidates)
                {
                    _logger?.Debug(
                        "TMDB episode group candidate - Series {0}: {1} (id={2}, type={3}, groups={4}, episodes={5})",
                        seriesId, candidate.name, candidate.id, candidate.type, candidate.group_count,
                        candidate.episode_count);
                }
            }

            var selected = SelectGroup(seriesId, candidates);

            _logger?.Info(
                "TMDB episode group selected - Series {0}: {1} (id={2}, type={3})",
                seriesId, selected.name, selected.id, selected.type);

            var detailUrl = BuildUrl(apiRoot, "/3/tv/episode_group/" + selected.id, apiKey, language);
            var detailBody = await GetJsonAsync(detailUrl, language, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(detailBody))
            {
                _logger?.Warn(
                    "TMDB episode group - Unable to read group {0} for series {1}; native scraping is used.",
                    selected.id, seriesId);
                return null;
            }

            EpisodeGroupDocument rawDocument;

            try
            {
                rawDocument = _jsonSerializer.DeserializeFromString<EpisodeGroupDocument>(detailBody);
            }
            catch (Exception e)
            {
                _logger?.Warn(
                    "TMDB episode group - Invalid group {0} for series {1}; native scraping is used. {2}",
                    selected.id, seriesId, e.Message);
                return null;
            }

            var document = NormalizeDocument(rawDocument);

            if (document is null || document.groups.Count == 0)
            {
                _logger?.Warn(
                    "TMDB episode group - Group {0} for series {1} has no usable seasons; native scraping is used.",
                    selected.id, seriesId);
                return null;
            }

            var episodeCount = document.groups.Sum(group => group.episodes?.Count ?? 0);

            _logger?.Info(
                "TMDB episode group loaded - Series {0}: {1} (id={2}, type={3}), {4} seasons, {5} episodes",
                seriesId, selected.name, selected.id, selected.type, document.groups.Count, episodeCount);

            return new CacheEntry
            {
                Version = CacheVersion,
                SourceKey = GetSourceKey(),
                CachedAtUtc = DateTime.UtcNow,
                GroupId = selected.id,
                GroupName = selected.name,
                GroupType = selected.type,
                Document = document
            };
        }

        /// <summary>
        /// Converts the raw TMDB group document into the same convention as a local
        /// episodegroup.json file: <c>groups[].order</c> is the library season number and
        /// <c>episodes[].order</c> is zero-based inside the group.
        /// </summary>
        private static EpisodeGroupDocument NormalizeDocument(EpisodeGroupDocument raw)
        {
            if (raw?.groups is null || raw.groups.Count == 0) return null;

            var firstIsSpecials = IsSpecialSeason(raw.groups[0]?.name);
            var baseOffset = firstIsSpecials ? 0 : 1;

            var document = new EpisodeGroupDocument
            {
                id = raw.id,
                name = raw.name,
                description = raw.description,
                groups = new List<EpisodeGroupGroup>()
            };

            var usedSeasons = new HashSet<int>();

            for (var index = 0; index < raw.groups.Count; index++)
            {
                var group = raw.groups[index];

                if (group?.episodes is null) continue;

                var localSeason = ResolveLocalSeason(group, index, baseOffset);

                if (!usedSeasons.Add(localSeason))
                {
                    _logger?.Debug(
                        "TMDB episode group - Duplicate local season {0} in group {1}; the later group is ignored.",
                        localSeason, raw.id);
                    continue;
                }

                var episodes = new List<EpisodeGroupEpisode>();

                // TMDB normally returns the episodes in order, but sorting by the response's
                // own order field keeps the mapping stable when that is not the case.
                foreach (var episode in group.episodes.Where(item => item != null)
                             .OrderBy(item => item.order))
                {
                    episodes.Add(new EpisodeGroupEpisode
                    {
                        season_number = episode.season_number,
                        episode_number = episode.episode_number,
                        order = episodes.Count
                    });
                }

                document.groups.Add(new EpisodeGroupGroup
                {
                    name = group.name,
                    overview = group.overview,
                    air_date = group.air_date,
                    order = localSeason,
                    episodes = episodes
                });
            }

            document.groups.Sort((left, right) => left.order.CompareTo(right.order));

            return document;
        }

        /// <summary>
        /// Applies the user preference first: an explicit group id/name wins, then the preferred
        /// TMDB group type, and only when neither is configured (or matches nothing) the built-in
        /// season-shaped ranking is used. The TMDB list order is kept as the tie breaker.
        /// </summary>
        private static EpisodeGroupSummary SelectGroup(string seriesId,
            List<EpisodeGroupSummary> candidates)
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;

            var preferredGroup = options?.EpisodeGroupPreferredGroup?.Trim();

            if (!string.IsNullOrEmpty(preferredGroup))
            {
                var explicitMatch = candidates.FirstOrDefault(item =>
                    string.Equals(item.id, preferredGroup, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item.name, preferredGroup, StringComparison.OrdinalIgnoreCase));

                if (explicitMatch != null)
                {
                    _logger?.Info(
                        "TMDB episode group - Series {0} uses the configured group preference '{1}'",
                        seriesId, preferredGroup);
                    return explicitMatch;
                }

                _logger?.Debug(
                    "TMDB episode group - Series {0}: configured group '{1}' was not found; falling back to the type preference.",
                    seriesId, preferredGroup);
            }

            var preferredType = options?.EpisodeGroupPreferredType ??
                                MetadataEnhanceOptions.EpisodeGroupTypeOption.Auto;

            if (preferredType != MetadataEnhanceOptions.EpisodeGroupTypeOption.Auto)
            {
                var typedMatch = candidates.FirstOrDefault(item => item.type == (int)preferredType);

                if (typedMatch != null)
                {
                    _logger?.Info(
                        "TMDB episode group - Series {0} uses the preferred type {1}",
                        seriesId, preferredType);
                    return typedMatch;
                }

                _logger?.Debug(
                    "TMDB episode group - Series {0}: no group of the preferred type {1}; falling back to the automatic ranking.",
                    seriesId, preferredType);
            }

            return candidates.OrderBy(item => GetGroupSelectionRank(item.type)).First();
        }

        /// <summary>
        /// TMDB episode group types: 1 original air date, 2 absolute, 3 DVD, 4 digital,
        /// 5 story arc, 6 production, 7 TV. Season-shaped types win; story arcs and
        /// production orders are the last resort.
        /// </summary>
        private static int GetGroupSelectionRank(int type)
        {
            switch (type)
            {
                case 1:
                case 3:
                case 4:
                case 7:
                    return 0;
                case 2:
                    return 1;
                case 5:
                case 6:
                    return 2;
                default:
                    return 1;
            }
        }

        private static int ResolveLocalSeason(EpisodeGroupGroup group, int index, int baseOffset)
        {
            if (IsSpecialSeason(group?.name)) return 0;

            if (!string.IsNullOrWhiteSpace(group?.name))
            {
                var match = SeasonNameRegex.Match(group.name);

                if (match.Success && int.TryParse(match.Groups[1].Value, out var season))
                {
                    return season;
                }
            }

            return index + baseOffset;
        }

        private static bool IsSpecialSeason(string groupName)
        {
            return !string.IsNullOrWhiteSpace(groupName) && SpecialSeasonRegex.IsMatch(groupName);
        }

        private static async Task<string> GetJsonAsync(string url, string language,
            CancellationToken cancellationToken)
        {
            if (_httpClient is null || _jsonSerializer is null) return null;

            var cacheKey = MovieDbCache.Instance.GetKey(url);

            if (!string.IsNullOrEmpty(cacheKey) &&
                MovieDbCache.Instance.TryGet(cacheKey, false, out var cached, out _))
            {
                _logger?.Debug("TMDB episode group - Cache hit: {0}", MovieDbCache.SanitizeUrl(url));
                return cached.Body;
            }

            try
            {
                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    UserAgent = Plugin.Instance?.UserAgent,
                    BufferContent = true,
                    TimeoutMs = RequestTimeoutMs,
                    ThrowOnErrorResponse = false,
                    LogRequest = false,
                    LogResponse = false,
                    LogErrors = false
                };

                using (var response = await _httpClient.GetResponse(options).ConfigureAwait(false))
                {
                    if (response?.Content is null)
                    {
                        _logger?.Warn("TMDB episode group - Empty response: {0}",
                            MovieDbCache.SanitizeUrl(url));
                        return null;
                    }

                    string body;

                    using (var reader = new StreamReader(response.Content, Encoding.UTF8))
                    {
                        body = await reader.ReadToEndAsync().ConfigureAwait(false);
                    }

                    var statusCode = (int)response.StatusCode;

                    if (statusCode < 200 || statusCode >= 300)
                    {
                        _logger?.Warn("TMDB episode group - Request returned HTTP {0}: {1}",
                            statusCode, MovieDbCache.SanitizeUrl(url));
                        return null;
                    }

                    if (string.IsNullOrWhiteSpace(body)) return null;

                    if (!string.IsNullOrEmpty(cacheKey))
                    {
                        MovieDbCache.Instance.Store(cacheKey, url, body, response.ContentType,
                            new MovieDbCacheMeta
                            {
                                source = "episode_group",
                                request = "episode_group",
                                url = MovieDbCache.SanitizeUrl(url),
                                language = language,
                                capturedAtUtc = DateTime.UtcNow
                            });
                    }

                    return body;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                _logger?.Warn("TMDB episode group - Request failed: {0} - {1}",
                    MovieDbCache.SanitizeUrl(url), e.Message);
                return null;
            }
        }

        private static string ResolveLanguage(Series series)
        {
            string language = null;

            try
            {
                if (series != null)
                {
                    language = Plugin.MetadataApi?.GetPreferredMetadataLanguage(series);
                }

                if (string.IsNullOrEmpty(language))
                {
                    language = Plugin.MetadataApi?.GetServerPreferredMetadataLanguage();
                }
            }
            catch (Exception e)
            {
                _logger?.Debug("TMDB episode group - Language resolution failed: " + e.Message);
            }

            if (string.IsNullOrWhiteSpace(language)) return null;

            language = language.Trim();

            if (!LanguageUtility.IsChineseLanguageCode(language)) return language;

            var order = GetOptions()?.GetEffectiveChineseMetadataOrder() ??
                        ChineseMetadataOrderOption.LibraryDefault;

            switch (order)
            {
                case ChineseMetadataOrderOption.TraditionalFirst:
                    return LanguageUtility.IsTraditionalChineseLanguageCode(language)
                        ? MovieDbApi.NormalizeChineseLanguage(language)
                        : "zh-TW";
                case ChineseMetadataOrderOption.SimplifiedFirst:
                    return LanguageUtility.IsSimplifiedChineseLanguageCode(language)
                        ? MovieDbApi.NormalizeChineseLanguage(language)
                        : "zh-CN";
                default:
                    return MovieDbApi.NormalizeChineseLanguage(language);
            }
        }

        private static string BuildUrl(string apiRoot, string path, string apiKey, string language)
        {
            var url = apiRoot.TrimEnd('/') + path + "?api_key=" + Uri.EscapeDataString(apiKey);

            if (!string.IsNullOrEmpty(language))
            {
                url += "&language=" + Uri.EscapeDataString(language);
            }

            return url;
        }

        private static bool IsOutOfScopeFast(string seriesId)
        {
            var scope = GetOptions()?.EpisodeGroupLibraryScope;

            if (string.IsNullOrWhiteSpace(scope)) return false;

            return ScopeCache.TryGetValue(seriesId + "|" + scope, out var inScope) && !inScope;
        }

        private static bool IsInScope(string seriesId, Series series)
        {
            var scope = GetOptions()?.EpisodeGroupLibraryScope;

            if (string.IsNullOrWhiteSpace(scope)) return true;

            var cacheKey = seriesId + "|" + scope;

            if (ScopeCache.TryGetValue(cacheKey, out var cached)) return cached;

            var result = false;

            if (series != null)
            {
                long? libraryId = null;

                try
                {
                    libraryId = _libraryManager?.GetCollectionFolders(series)?.FirstOrDefault()?.InternalId;
                }
                catch (Exception e)
                {
                    _logger?.Debug("TMDB episode group - Library lookup failed: " + e.Message);
                }

                if (!libraryId.HasValue)
                {
                    try
                    {
                        libraryId = series.GetTopParent()?.InternalId;
                    }
                    catch (Exception e)
                    {
                        _logger?.Debug("TMDB episode group - Top parent lookup failed: " + e.Message);
                    }
                }

                if (libraryId.HasValue)
                {
                    var libraryIdText = libraryId.Value.ToString();
                    result = scope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(id => string.Equals(id.Trim(), libraryIdText, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (!result)
            {
                _logger?.Debug(
                    "TMDB episode group - Series {0} is outside the configured library scope; native scraping is used.",
                    seriesId);
            }

            ScopeCache[cacheKey] = result;
            return result;
        }

        private static Series ResolveSeries(string seriesId)
        {
            try
            {
                if (_libraryManager is null) return null;

                return _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    Limit = 1,
                    IncludeItemTypes = new[] { nameof(Series) },
                    AnyProviderIdEquals = new[]
                    {
                        new KeyValuePair<string, string>(MetadataProviders.Tmdb.ToString(), seriesId),
                        new KeyValuePair<string, string>("Tmdb", seriesId)
                    }
                }).OfType<Series>().FirstOrDefault();
            }
            catch (Exception e)
            {
                _logger?.Debug("TMDB episode group - Series lookup failed: " + e.Message);
                return null;
            }
        }

        private static CacheEntry GetFreshEntry(string seriesId)
        {
            return MemoryCache.TryGetValue(seriesId, out var entry) && entry.IsFresh && entry.MatchesSource &&
                   entry.Document?.groups?.Count > 0
                ? entry
                : null;
        }

        private static CacheEntry GetEntryForMapping(string seriesId)
        {
            if (!MemoryCache.TryGetValue(seriesId, out var entry))
            {
                entry = LoadDiskEntry(seriesId);

                if (entry != null) MemoryCache[seriesId] = entry;
            }

            return entry != null && entry.IsFresh && entry.MatchesSource &&
                   entry.Document?.groups?.Count > 0
                ? entry
                : null;
        }

        private static string GetSeriesId(string url)
        {
            var path = GetPath(url);

            if (string.IsNullOrEmpty(path)) return null;

            var match = SeriesRequestRegex.Match(path);

            return match.Success ? match.Groups[1].Value : null;
        }

        private static string GetPath(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.AbsolutePath;

            var queryIndex = url.IndexOf('?');
            return queryIndex >= 0 ? url.Substring(0, queryIndex) : url;
        }

        private static string GetSourceKey()
        {
            try
            {
                // The selected group is part of the cache identity: changing the group preference
                // must invalidate cached documents instead of serving the previous selection for
                // the rest of the TTL.
                return MovieDbApi.GetEffectiveApiRoot().TrimEnd('/') + "|" + GetSelectionKey();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string GetSelectionKey()
        {
            var options = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
            var type = options?.EpisodeGroupPreferredType ??
                       MetadataEnhanceOptions.EpisodeGroupTypeOption.Auto;
            var group = options?.EpisodeGroupPreferredGroup?.Trim() ?? string.Empty;

            return (int)type + ":" + group;
        }

        private static string GetCacheDirectory()
        {
            try
            {
                var dataFolder = Plugin.Instance?.DataFolderPath;

                if (string.IsNullOrEmpty(dataFolder))
                {
                    dataFolder = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                        "StrmAssistant");
                }

                return Path.Combine(dataFolder, CacheFolderName);
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "StrmAssistant", CacheFolderName);
            }
        }

        private static CacheEntry LoadDiskEntry(string seriesId)
        {
            try
            {
                if (_jsonSerializer is null) return null;

                var path = Path.Combine(GetCacheDirectory(), seriesId + ".json");

                if (!File.Exists(path)) return null;

                var entry = _jsonSerializer.DeserializeFromFile<CacheEntry>(path);

                return entry?.Document?.groups != null ? entry : null;
            }
            catch (Exception e)
            {
                _logger?.Debug("TMDB episode group - Cache read failed: " + e.Message);
                return null;
            }
        }

        private static void SaveDiskEntry(string seriesId, CacheEntry entry)
        {
            string tempPath = null;

            try
            {
                if (_jsonSerializer is null) return;

                var directory = GetCacheDirectory();
                Directory.CreateDirectory(directory);

                var path = Path.Combine(directory, seriesId + ".json");
                tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(tempPath, _jsonSerializer.SerializeToString(entry), Utf8NoBom);

                if (File.Exists(path)) File.Delete(path);
                File.Move(tempPath, path);
                tempPath = null;
            }
            catch (Exception e)
            {
                _logger?.Debug("TMDB episode group - Cache write failed: " + e.Message);
            }
            finally
            {
                if (tempPath != null)
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best effort cleanup only.
                    }
                }
            }
        }

        private static MetadataEnhanceOptions GetOptions()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
            }
            catch
            {
                return null;
            }
        }

        private sealed class EpisodeGroupListResponse
        {
            public List<EpisodeGroupSummary> results { get; set; }
        }

        private sealed class EpisodeGroupSummary
        {
            public string id { get; set; }
            public string name { get; set; }
            public string description { get; set; }
            public int type { get; set; }
            public int episode_count { get; set; }
            public int group_count { get; set; }
        }

        private sealed class CacheEntry
        {
            public int Version { get; set; }
            public string SourceKey { get; set; }
            public DateTime CachedAtUtc { get; set; }
            public string GroupId { get; set; }
            public string GroupName { get; set; }
            public int GroupType { get; set; }
            public EpisodeGroupDocument Document { get; set; }

            public bool IsFresh =>
                Version == CacheVersion && CachedAtUtc.Add(CacheTtl) > DateTime.UtcNow;

            public bool MatchesSource =>
                string.Equals(SourceKey, GetSourceKey(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
