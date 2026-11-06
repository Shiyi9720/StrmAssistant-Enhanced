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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
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
        private static readonly LruCache CelebrityCache = new LruCache(200);
        private static readonly LruCache SeasonCache = new LruCache(100);
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
        private readonly DoubanRatingStore _ratingStore;

        public DoubanApi(IProviderManager providerManager, IFileSystem fileSystem, ILibraryManager libraryManager,
            IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = Plugin.Instance.Logger;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
            _httpClient = ProxyApi.Wrap(httpClient);
            _ratingStore = new DoubanRatingStore(jsonSerializer);
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
                var needsCast = options.EnableDoubanCast && item.SupportsPeople;
                var needsSeason = options.EnableDoubanSeason && item is Series;

                if (!needsOverview && !needsPoster && !needsCast && !needsSeason) return;

                var detail = await FindDetailAsync(item, cancellationToken).ConfigureAwait(false);

                if (detail is null) return;

                await SaveDoubanMetadataAsync(item, detail).ConfigureAwait(false);

                if (needsCast)
                {
                    await SaveCastAsync(item, detail, cancellationToken).ConfigureAwait(false);
                }

                if (needsSeason)
                {
                    await SaveSeasonMetadataAsync(item, detail, cancellationToken).ConfigureAwait(false);
                }

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

        private async Task SaveDoubanMetadataAsync(BaseItem item, DoubanDetail detail)
        {
            var changed = false;
            var doubanId = detail.id?.Trim();

            if (!string.IsNullOrEmpty(doubanId) &&
                !string.Equals(item.GetProviderId("Douban"), doubanId, StringComparison.Ordinal))
            {
                item.SetProviderId("Douban", doubanId);
                changed = true;
            }

            var rating = detail.rating?.value;

            if (rating.HasValue && rating.Value > 0)
            {
                var ratingText = rating.Value.ToString("0.0#", CultureInfo.InvariantCulture);

                if (!string.Equals(item.GetProviderId("DoubanRating"), ratingText, StringComparison.Ordinal))
                {
                    item.SetProviderId("DoubanRating", ratingText);
                    changed = true;
                }
            }

            var ratingCount = detail.rating?.count;

            if (ratingCount.HasValue && ratingCount.Value > 0 &&
                string.IsNullOrEmpty(item.GetProviderId("DoubanRatingCount")))
            {
                item.SetProviderId("DoubanRatingCount",
                    ratingCount.Value.ToString(CultureInfo.InvariantCulture));
                changed = true;
            }

            var detailYear = ParseYear(detail.year, null);
            var premiereDate = ParsePubDate(detail.pubdate, detailYear);

            if (SetMissingPremiereDate(item, premiereDate)) changed = true;
            if (SetMissingProductionYear(item, detailYear ?? premiereDate?.Year)) changed = true;

            if (changed)
            {
                try
                {
                    await _providerManager.SaveMetadata(item, ItemUpdateType.MetadataEdit).ConfigureAwait(false);
                    _logger?.Info("DoubanSupplement - Provider ids updated: " + item.Name);
                }
                catch (Exception e)
                {
                    _logger?.Debug("DoubanSupplement - Failed to save provider ids: " + e.Message);
                }
            }

            _ratingStore.Save(item.InternalId, doubanId, item.Name, item.GetType().Name, rating,
                detail.rating?.count);
        }

        private async Task SaveCastAsync(BaseItem item, DoubanDetail detail,
            CancellationToken cancellationToken)
        {
            try
            {
                List<PersonInfo> existing;

                try
                {
                    existing = _libraryManager.GetItemPeople(item)?
                        .Where(person => person != null)
                        .ToList() ?? new List<PersonInfo>();
                }
                catch (Exception e)
                {
                    _logger?.Debug("DoubanSupplement - Failed to read people: " + e.Message);
                    existing = new List<PersonInfo>();
                }

                var subjectType = item is Movie ? "movie" : "tv";
                var celebrities = await GetCelebritiesAsync(detail.id, subjectType, cancellationToken)
                    .ConfigureAwait(false);

                var actors = celebrities?.actors != null && celebrities.actors.Count > 0
                    ? celebrities.actors
                    : detail.actors;
                var directors = celebrities?.directors != null && celebrities.directors.Count > 0
                    ? celebrities.directors
                    : detail.directors;

                var existingNames = new HashSet<string>(
                    existing.Where(person => !string.IsNullOrWhiteSpace(person.Name))
                        .Select(person => person.Name.Trim()),
                    StringComparer.OrdinalIgnoreCase);
                var additions = new List<PersonInfo>();

                foreach (var actor in (actors ?? new List<DoubanPerson>())
                             .Where(person => person != null && !string.IsNullOrWhiteSpace(person.name))
                             .Take(10))
                {
                    var name = actor.name.Trim();

                    if (!existingNames.Add(name)) continue;

                    additions.Add(new PersonInfo
                    {
                        Name = name,
                        Role = CleanRole(actor.character),
                        Type = PersonType.Actor
                    });
                }

                foreach (var director in (directors ?? new List<DoubanPerson>())
                             .Where(person => person != null && !string.IsNullOrWhiteSpace(person.name))
                             .Take(10))
                {
                    var name = director.name.Trim();

                    if (!existingNames.Add(name)) continue;

                    additions.Add(new PersonInfo
                    {
                        Name = name,
                        Role = CleanRole(director.character),
                        Type = PersonType.Director
                    });
                }

                if (additions.Count == 0) return;

                _libraryManager.UpdatePeople(item, existing.Concat(additions).ToList());
                item.UpdateToRepository(ItemUpdateType.MetadataEdit);

                _logger?.Info("DoubanSupplement - Cast updated: " + item.Name + " (+" +
                              additions.Count + ")");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Cast update failed: " + e.Message);
            }
        }

        private async Task<DoubanCelebrities> GetCelebritiesAsync(string subjectId, string subjectType,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(subjectId)) return null;

            var cacheKey = "celebrities:" + subjectType + ":" + subjectId;

            if (CelebrityCache.TryGetFromCache(cacheKey, out DoubanCelebrities cached)) return cached;

            var result = await GetJsonAsync<DoubanCelebrities>(
                    DoubanApiBase + "/" + subjectType + "/" + subjectId + "/celebrities", cancellationToken)
                .ConfigureAwait(false);

            if (result != null) CelebrityCache.AddOrUpdateCache(cacheKey, result);

            return result;
        }

        private async Task<List<DoubanSeason>> GetSeasonsAsync(string subjectId, string subjectType,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(subjectId)) return null;

            var cacheKey = "seasons:" + subjectType + ":" + subjectId;

            if (SeasonCache.TryGetFromCache(cacheKey, out List<DoubanSeason> cached)) return cached;

            var result = await GetJsonAsync<List<DoubanSeason>>(
                    DoubanApiBase + "/" + subjectType + "/" + subjectId + "/seasons", cancellationToken)
                .ConfigureAwait(false);

            if (result != null) SeasonCache.AddOrUpdateCache(cacheKey, result);

            return result;
        }

        private async Task SaveSeasonMetadataAsync(BaseItem item, DoubanDetail detail,
            CancellationToken cancellationToken)
        {
            if (!(item is Series series)) return;

            try
            {
                var seasons = GetLocalSeasons(series);

                if (seasons.Count == 0) return;

                var doubanSeasons = detail.seasons?.Where(season => season != null).ToList();

                if (doubanSeasons is null || doubanSeasons.Count == 0)
                {
                    doubanSeasons = await GetSeasonsAsync(detail.id, "tv", cancellationToken)
                        .ConfigureAwait(false);
                }

                var doubanEpisodes = detail.episodes?.Where(episode => episode != null).ToList();
                var changedAny = false;

                foreach (var season in seasons)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var index = season.IndexNumber;

                    if (!index.HasValue || index.Value <= 0) continue;

                    var seasonNumber = index.Value;
                    var match = doubanSeasons?.FirstOrDefault(candidate =>
                        GetSeasonNumber(candidate) == seasonNumber);

                    if (match is null && doubanSeasons?.Count == 1 &&
                        doubanSeasons[0].title?.IndexOf("季", StringComparison.Ordinal) >= 0)
                    {
                        match = doubanSeasons[0];
                    }

                    var changed = false;

                    if (match != null)
                    {
                        var matchYear = ParseYear(match.year, null);
                        var matchDate = ParsePubDate(match.pubdate, matchYear);

                        if (SetMissingPremiereDate(season, matchDate)) changed = true;
                        if (SetMissingProductionYear(season, matchYear ?? matchDate?.Year)) changed = true;

                        if (string.IsNullOrWhiteSpace(season.Overview) &&
                            !string.IsNullOrWhiteSpace(match.intro))
                        {
                            season.Overview = match.intro.Trim();
                            changed = true;
                        }
                    }
                    else if (seasonNumber == 1)
                    {
                        var seriesYear = ParseYear(detail.year, null);
                        var seriesDate = ParsePubDate(detail.pubdate, seriesYear);

                        if (SetMissingPremiereDate(season, seriesDate)) changed = true;
                        if (SetMissingProductionYear(season, seriesYear ?? seriesDate?.Year)) changed = true;
                    }

                    if (doubanEpisodes != null && doubanEpisodes.Count > 0 &&
                        SupplementEpisodes(season, seasonNumber, doubanEpisodes, cancellationToken))
                    {
                        changed = true;
                    }

                    if (!changed) continue;

                    season.UpdateToRepository(ItemUpdateType.MetadataEdit);
                    changedAny = true;
                }

                if (changedAny)
                {
                    _logger?.Info("DoubanSupplement - Season metadata updated: " + series.Name);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Season update failed: " + e.Message);
            }
        }

        private bool SupplementEpisodes(Season season, int seasonIndex,
            List<DoubanEpisode> doubanEpisodes, CancellationToken cancellationToken)
        {
            var episodes = GetLocalEpisodes(season);

            if (episodes.Count == 0) return false;

            var changedAny = false;

            foreach (var episode in episodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var number = episode.IndexNumber;

                if (!number.HasValue || number.Value <= 0) continue;

                var match = doubanEpisodes.FirstOrDefault(candidate =>
                {
                    if (GetEpisodeNumber(candidate) != number.Value) return false;

                    var candidateSeason = GetEpisodeSeason(candidate);
                    return candidateSeason == seasonIndex ||
                           (!candidateSeason.HasValue && seasonIndex == 1);
                });

                if (match is null) continue;

                var changed = false;

                if (string.IsNullOrWhiteSpace(episode.Name) && !string.IsNullOrWhiteSpace(match.title))
                {
                    episode.Name = match.title.Trim();
                    changed = true;
                }

                if (string.IsNullOrWhiteSpace(episode.Overview) && !string.IsNullOrWhiteSpace(match.intro))
                {
                    episode.Overview = match.intro.Trim();
                    changed = true;
                }

                var episodeYear = ParseYear(match.year, null);
                var episodeDate = ParsePubDate(match.pubdate, episodeYear);

                if (SetMissingPremiereDate(episode, episodeDate)) changed = true;
                if (SetMissingProductionYear(episode, episodeYear ?? episodeDate?.Year)) changed = true;

                if (!changed) continue;

                episode.UpdateToRepository(ItemUpdateType.MetadataEdit);
                changedAny = true;
            }

            return changedAny;
        }

        private List<Season> GetLocalSeasons(Series series)
        {
            try
            {
                return _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(Season) },
                        ParentWithPresentationUniqueKeyFromItemId = series.InternalId
                    })
                    .OfType<Season>()
                    .OrderBy(season => season.IndexNumber ?? int.MaxValue)
                    .ToList();
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Failed to read seasons: " + e.Message);
                return new List<Season>();
            }
        }

        private List<Episode> GetLocalEpisodes(Season season)
        {
            try
            {
                return _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { nameof(Episode) },
                        ParentIds = new[] { season.InternalId }
                    })
                    .OfType<Episode>()
                    .OrderBy(episode => episode.IndexNumber ?? int.MaxValue)
                    .ToList();
            }
            catch (Exception e)
            {
                _logger?.Debug("DoubanSupplement - Failed to read episodes: " + e.Message);
                return new List<Episode>();
            }
        }

        private static string CleanRole(string character)
        {
            if (string.IsNullOrWhiteSpace(character)) return null;

            var role = character.Trim();

            if (role.StartsWith("饰演", StringComparison.Ordinal))
            {
                role = role.Substring(2).Trim();
            }
            else if (role.StartsWith("饰", StringComparison.Ordinal))
            {
                role = role.Substring(1).Trim();
            }

            return string.IsNullOrWhiteSpace(role) ? null : role;
        }

        private static DateTimeOffset? ParsePubDate(IEnumerable<string> pubdate, int? preferredYear = null)
        {
            var values = pubdate?.Where(date => !string.IsNullOrWhiteSpace(date)).ToList();

            if (values is null || values.Count == 0) return null;

            var parsed = new List<DateTimeOffset>();

            foreach (var value in values)
            {
                var match = Regex.Match(value, @"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})");

                if (!match.Success) continue;

                var normalized = match.Groups[1].Value + "-" + match.Groups[2].Value + "-" +
                                 match.Groups[3].Value;

                if (DateTime.TryParseExact(normalized, "yyyy-M-d", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                {
                    parsed.Add(new DateTimeOffset(date, TimeSpan.Zero));
                }
            }

            if (parsed.Count == 0) return null;

            if (preferredYear.HasValue)
            {
                var preferred = parsed.FirstOrDefault(date => date.Year == preferredYear.Value);

                return preferred != default ? preferred : (DateTimeOffset?)null;
            }

            return parsed[0];
        }

        private static int? ParseYear(string year, DateTimeOffset? date)
        {
            if (int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            return date?.Year;
        }

        private static bool SetMissingPremiereDate(BaseItem item, DateTimeOffset? date)
        {
            if (item is null || !date.HasValue) return false;

            if (item.PremiereDate.HasValue && item.PremiereDate.Value != DateTimeOffset.MinValue)
            {
                return false;
            }

            item.PremiereDate = date;
            return true;
        }

        private static bool SetMissingProductionYear(BaseItem item, int? year)
        {
            if (item is null || !year.HasValue || year.Value <= 0) return false;

            if (item.ProductionYear.HasValue && item.ProductionYear.Value > 0) return false;

            item.ProductionYear = year.Value;
            return true;
        }

        private static int? GetSeasonNumber(DoubanSeason season)
        {
            if (season is null) return null;
            if (season.season_number.HasValue) return season.season_number;
            if (season.season.HasValue) return season.season;
            if (string.IsNullOrWhiteSpace(season.title)) return null;

            var match = Regex.Match(season.title, @"第\s*([0-9]+|[一二三四五六七八九十]+)\s*季");

            if (!match.Success)
            {
                match = Regex.Match(season.title, @"(?:Season|S)\s*([0-9]+)",
                    RegexOptions.IgnoreCase);
            }

            return match.Success ? ParseSeasonNumber(match.Groups[1].Value) : (int?)null;
        }

        private static int? ParseSeasonNumber(string value)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                return parsed;
            }

            var numerals = new Dictionary<char, int>
            {
                ['一'] = 1,
                ['二'] = 2,
                ['三'] = 3,
                ['四'] = 4,
                ['五'] = 5,
                ['六'] = 6,
                ['七'] = 7,
                ['八'] = 8,
                ['九'] = 9,
                ['十'] = 10
            };

            if (value.Length == 1 && numerals.TryGetValue(value[0], out var single)) return single;

            if (value.Length == 2)
            {
                if (value[1] == '十' && numerals.TryGetValue(value[0], out var tens)) return tens * 10;
                if (value[0] == '十' && numerals.TryGetValue(value[1], out var ones)) return 10 + ones;
            }

            if (value.Length == 3 && value[1] == '十' &&
                numerals.TryGetValue(value[0], out var tensDigit) &&
                numerals.TryGetValue(value[2], out var onesDigit))
            {
                return tensDigit * 10 + onesDigit;
            }

            return null;
        }

        private static int? GetEpisodeNumber(DoubanEpisode episode)
        {
            return episode.episode_number ?? episode.episode;
        }

        private static int? GetEpisodeSeason(DoubanEpisode episode)
        {
            return episode.season_number;
        }

        public DoubanRatingCacheEntry GetCachedRating(long itemId)
        {
            return _ratingStore.Get(itemId);
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
            public DoubanRating rating { get; set; }
            public List<DoubanPerson> actors { get; set; }
            public List<DoubanPerson> directors { get; set; }
            public List<string> pubdate { get; set; }
            public string year { get; set; }
            public int? episodes_count { get; set; }
            public List<DoubanSeason> seasons { get; set; }
            public List<DoubanEpisode> episodes { get; set; }
        }

        private class DoubanCelebrities
        {
            public List<DoubanPerson> actors { get; set; }
            public List<DoubanPerson> directors { get; set; }
        }

        private class DoubanPerson
        {
            public string id { get; set; }
            public string name { get; set; }
            public string latin_name { get; set; }
            public string character { get; set; }
        }

        private class DoubanSeason
        {
            public string id { get; set; }
            public string title { get; set; }
            public int? season { get; set; }
            public int? season_number { get; set; }
            public int? episodes_count { get; set; }
            public int? episode_count { get; set; }
            public List<string> pubdate { get; set; }
            public string year { get; set; }
            public string intro { get; set; }
            public string cover_url { get; set; }
        }

        private class DoubanEpisode
        {
            public string id { get; set; }
            public string title { get; set; }
            public string intro { get; set; }
            public int? episode { get; set; }
            public int? episode_number { get; set; }
            public int? season_number { get; set; }
            public List<string> pubdate { get; set; }
            public string year { get; set; }
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
            public int? count { get; set; }
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
