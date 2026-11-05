using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Serves search results from the Chinese index. Index matches (pinyin, initials,
    /// simplified/traditional variants, original titles, aliases and title+year) are merged with
    /// Emby's native search results, ranked, then re-resolved through ILibraryManager with the
    /// requesting user attached so that the result set never contains items the user cannot see.
    /// </summary>
    public class SearchService : BaseApiService
    {
        private const int MaxCandidates = 800;
        private const int ResolveChunkSize = 150;
        private const int NativeResultLimit = 300;
        private const int CacheLimit = 64;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(20);

        private static readonly ItemFields[] DefaultFields =
        {
            ItemFields.ProductionYear,
            ItemFields.PremiereDate,
            ItemFields.DateCreated,
            ItemFields.OriginalTitle,
            ItemFields.SortName,
            ItemFields.Overview,
            ItemFields.Genres,
            ItemFields.CommunityRating,
            ItemFields.OfficialRating,
            ItemFields.RunTimeTicks,
            ItemFields.EndDate,
            ItemFields.Status,
            ItemFields.ProviderIds,
            ItemFields.PrimaryImageAspectRatio
        };

        private readonly ILibraryManager _libraryManager;
        private readonly IDtoService _dtoService;
        private readonly ConcurrentDictionary<string, CachedSearch> _cache =
            new ConcurrentDictionary<string, CachedSearch>(StringComparer.Ordinal);

        public SearchService(ILibraryManager libraryManager, IDtoService dtoService)
        {
            _libraryManager = libraryManager;
            _dtoService = dtoService;
        }

        public object Get(SearchItems request)
        {
            var response = new SearchItemsResponse { Available = true };

            var options = Plugin.Instance.GetPluginOptions().SearchOptions;
            var indexService = Plugin.SearchIndexService;

            if (!options.EnableChineseSearch || indexService == null || !indexService.IsReady)
            {
                // Tell the injected script to call Emby's own search instead.
                response.Available = false;
                return response;
            }

            var user = GetUserForRequest(null);

            if (user == null)
            {
                response.Available = false;
                return response;
            }

            var query = string.IsNullOrWhiteSpace(request.Q) ? request.SearchTerm : request.Q;

            if (string.IsNullOrWhiteSpace(query))
            {
                return response;
            }

            var includeTypes = SplitTypes(request.IncludeItemTypes);
            var excludeTypes = SplitTypes(request.ExcludeItemTypes);
            var limit = Clamp(request.Limit ?? 30, 1, 200);
            var startIndex = Math.Max(0, request.StartIndex ?? 0);

            var cacheKey = string.Join("|",
                user.InternalId.ToString(CultureInfo.InvariantCulture),
                SearchTextHelper.Compact(query),
                indexService.Version.ToString(CultureInfo.InvariantCulture),
                options.SmartRanking ? "1" : "0",
                string.Join(",", includeTypes),
                string.Join(",", excludeTypes));

            var pool = GetCachedPool(cacheKey) ??
                       BuildPool(cacheKey, user, query, includeTypes, excludeTypes, options);

            var page = pool.Skip(startIndex).Take(limit).ToArray();
            var dtos = _dtoService.GetBaseItemDtos(page, BuildDtoOptions(request), user);

            response.Items = dtos;
            response.TotalRecordCount = pool.Count;

            return response;
        }

        private List<BaseItem> BuildPool(string cacheKey, User user, string query,
            string[] includeTypes, string[] excludeTypes, Options.SearchOptions options)
        {
            var queryHasChinese = SearchTextHelper.HasChinese(query);
            var candidates = new Dictionary<long, Candidate>();

            try
            {
                foreach (var match in Plugin.SearchIndexService.Match(query))
                {
                    if (!PassesPreFilter(match.Type, includeTypes, excludeTypes)) continue;

                    if (candidates.ContainsKey(match.ItemId)) continue;

                    candidates[match.ItemId] = new Candidate
                    {
                        Id = match.ItemId,
                        Name = match.Name,
                        TypeName = match.Type,
                        Year = match.Year,
                        Rating = match.Rating,
                        Tier = match.Tier,
                        LanguageRank = match.LanguageRank,
                        KindPriority = match.KindPriority
                    };
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("ChineseSearch - Index match failed: " + e.Message);
            }

            try
            {
                var nativeQuery = new InternalItemsQuery
                {
                    User = user,
                    SearchTerm = query,
                    Recursive = true,
                    IsVirtualItem = false,
                    Limit = NativeResultLimit,
                    EnableTotalRecordCount = false
                };

                if (includeTypes.Length > 0) nativeQuery.IncludeItemTypes = includeTypes;
                if (excludeTypes.Length > 0) nativeQuery.ExcludeItemTypes = excludeTypes;

                foreach (var item in _libraryManager.GetItemList(nativeQuery))
                {
                    if (candidates.ContainsKey(item.InternalId)) continue;

                    candidates[item.InternalId] = new Candidate
                    {
                        Id = item.InternalId,
                        Name = item.Name,
                        TypeName = item.GetType().Name,
                        Year = item.ProductionYear,
                        Rating = item.CommunityRating,
                        Tier = SearchIndexService.TierContains,
                        LanguageRank = GetLanguageRank(queryHasChinese, item.Name, options.SmartRanking),
                        KindPriority = SearchIndexService.GetKindPriority(SearchKeyKind.Name)
                    };
                }
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Debug("ChineseSearch - Native search failed: " + e.Message);
            }

            if (candidates.Count == 0)
            {
                return StorePool(cacheKey, new List<BaseItem>());
            }

            var ordered = candidates.Values.ToList();
            ordered.Sort((a, b) => CompareCandidates(a, b, options.SmartRanking));

            var visible = ResolveVisible(user, ordered);

            if (includeTypes.Length > 0 || excludeTypes.Length > 0)
            {
                visible = visible
                    .Where(item => PassesPreFilter(item.GetType().Name, includeTypes, excludeTypes))
                    .ToList();
            }

            return StorePool(cacheKey, visible);
        }

        private List<BaseItem> ResolveVisible(User user, List<Candidate> ordered)
        {
            var result = new List<BaseItem>(Math.Min(ordered.Count, MaxCandidates));

            for (var offset = 0; offset < ordered.Count && result.Count < MaxCandidates;
                 offset += ResolveChunkSize)
            {
                var chunk = ordered.GetRange(offset, Math.Min(ResolveChunkSize, ordered.Count - offset));
                var ids = chunk.Select(c => c.Id).ToArray();

                BaseItem[] items;

                try
                {
                    var query = new InternalItemsQuery
                    {
                        User = user,
                        Recursive = true,
                        ItemIds = ids,
                        EnableTotalRecordCount = false
                    };

                    items = _libraryManager.GetItemList(query);
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger.Debug("ChineseSearch - Visibility query failed: " + e.Message);
                    continue;
                }

                var byId = new Dictionary<long, BaseItem>(items.Length);

                foreach (var item in items)
                {
                    byId[item.InternalId] = item;
                }

                foreach (var candidate in chunk)
                {
                    if (byId.TryGetValue(candidate.Id, out var item))
                    {
                        result.Add(item);
                    }
                }
            }

            return result;
        }

        private static int CompareCandidates(Candidate a, Candidate b, bool smartRanking)
        {
            var comparison = SearchIndexService.CompareTier(a.Tier, a.LanguageRank, a.KindPriority,
                b.Tier, b.LanguageRank, b.KindPriority);

            if (comparison != 0) return comparison;

            if (!smartRanking) return a.Id.CompareTo(b.Id);

            var rating = (b.Rating ?? 0).CompareTo(a.Rating ?? 0);

            if (rating != 0) return rating;

            var year = (b.Year ?? 0).CompareTo(a.Year ?? 0);

            if (year != 0) return year;

            var name = string.Compare(a.Name ?? string.Empty, b.Name ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            return name != 0 ? name : a.Id.CompareTo(b.Id);
        }

        private static int GetLanguageRank(bool queryHasChinese, string name, bool smartRanking)
        {
            if (!smartRanking) return 0;

            if (queryHasChinese)
            {
                return SearchTextHelper.HasChinese(name) ? 0 : 2;
            }

            return 0;
        }

        private static bool PassesPreFilter(string typeName, string[] includeTypes, string[] excludeTypes)
        {
            if (string.IsNullOrEmpty(typeName)) return includeTypes.Length == 0;

            if (excludeTypes.Any(t => string.Equals(t, typeName, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (includeTypes.Length == 0) return true;

            // "Video" is Emby's group for playable media; the concrete item type is more precise.
            if (includeTypes.Any(t => string.Equals(t, "Video", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            return includeTypes.Any(t => string.Equals(t, typeName, StringComparison.OrdinalIgnoreCase));
        }

        private static DtoOptions BuildDtoOptions(SearchItems request)
        {
            var fields = ParseFields(request.Fields);

            return new DtoOptions
            {
                Fields = fields,
                ImageTypeLimit = Clamp(request.ImageTypeLimit ?? 1, 0, 10),
                EnableImages = request.EnableImages ?? true,
                EnableUserData = request.EnableUserData ?? true
            };
        }

        private static ItemFields[] ParseFields(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return DefaultFields;

            var fields = new List<ItemFields>();

            foreach (var part in value.Split(','))
            {
                if (Enum.TryParse(part.Trim(), true, out ItemFields field) && !fields.Contains(field))
                {
                    fields.Add(field);
                }
            }

            return fields.Count > 0 ? fields.ToArray() : DefaultFields;
        }

        private static string[] SplitTypes(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

            return value.Split(',')
                .Select(i => i.Trim())
                .Where(i => i.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private List<BaseItem> GetCachedPool(string key)
        {
            if (!_cache.TryGetValue(key, out var entry)) return null;

            if (DateTime.UtcNow - entry.CreatedUtc <= CacheLifetime) return entry.Items;

            _cache.TryRemove(key, out _);

            return null;
        }

        private List<BaseItem> StorePool(string key, List<BaseItem> items)
        {
            if (_cache.Count > CacheLimit)
            {
                foreach (var entry in _cache.OrderBy(i => i.Value.CreatedUtc).Take(_cache.Count - CacheLimit))
                {
                    _cache.TryRemove(entry.Key, out _);
                }
            }

            _cache[key] = new CachedSearch
            {
                CreatedUtc = DateTime.UtcNow,
                Items = items
            };

            return items;
        }

        private sealed class CachedSearch
        {
            public DateTime CreatedUtc { get; set; }
            public List<BaseItem> Items { get; set; }
        }

        private sealed class Candidate
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public string TypeName { get; set; }
            public int? Year { get; set; }
            public float? Rating { get; set; }
            public int Tier { get; set; }
            public int LanguageRank { get; set; }
            public int KindPriority { get; set; }
        }
    }
}
