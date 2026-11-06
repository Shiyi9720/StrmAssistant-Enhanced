using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Options;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Builds and serves the inverted index used by Chinese search. Titles are reduced to compact
    /// keys (lower case letters/digits/CJK), pinyin, pinyin initials, simplified/traditional
    /// variants, original/sort titles, title+year combinations, main cast names and the external
    /// ids (Douban/Bangumi/IMDb) used by the Chinese metadata sources. The index is persisted to
    /// the plugin data folder and is rebuilt in the background so that startup is never blocked.
    /// </summary>
    public sealed class SearchIndexService
    {
        internal const int CurrentVersion = 3;

        public const int TierExact = 0;
        public const int TierPrefix = 1;
        public const int TierContains = 2;
        public const int TierFuzzy = 3;

        private const int PageSize = 500;
        private const int MaxAliasFetchPerBuild = 1000;
        private const int MaxAliasCount = 8;
        private const int MaxActorCount = 8;
        private const int MaxWeakCandidates = 4000;
        private const int MaxFuzzyChecks = 20000;

        private static readonly string[] IndexedItemTypes =
        {
            "Movie", "Series", "Episode", "BoxSet", "MusicAlbum", "MusicVideo", "Video", "Audio",
            "Playlist", "Trailer"
        };

        private static readonly ConcurrentDictionary<Type, PropertyInfo> AlternateTitleProperties =
            new ConcurrentDictionary<Type, PropertyInfo>();

        private readonly ILibraryManager _libraryManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _buildGate = new SemaphoreSlim(1, 1);
        private readonly object _sync = new object();
        private readonly ConcurrentDictionary<long, byte> _pendingUpdates = new ConcurrentDictionary<long, byte>();

        private Dictionary<long, IndexedItem> _items = new Dictionary<long, IndexedItem>();
        private volatile IndexedItem[] _snapshot = Array.Empty<IndexedItem>();

        private volatile bool _ready;
        private volatile bool _isBuilding;
        private volatile int _progressPercent;
        private volatile string _lastError;
        private DateTime? _builtAtUtc;
        private int _version;
        private int _starting;
        private int _updateScheduled;

        public SearchIndexService(ILibraryManager libraryManager, IJsonSerializer jsonSerializer,
            IHttpClient httpClient)
        {
            _libraryManager = libraryManager;
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;
            _logger = Plugin.Instance.Logger;
        }

        public bool IsEnabled => GetOptions().EnableChineseSearch;

        public bool IsReady => _ready;

        public int Version => _version;

        public SearchIndexStatus GetStatus()
        {
            return new SearchIndexStatus
            {
                Enabled = IsEnabled,
                Ready = _ready,
                IsBuilding = _isBuilding,
                ItemCount = _snapshot.Length,
                BuiltAtUtc = _builtAtUtc,
                Progress = _progressPercent,
                LastError = _lastError
            };
        }

        /// <summary>
        /// Called after plugin load. The first attempt is delayed so that a large index build or
        /// load never competes with the server's own startup work.
        /// </summary>
        public void Initialize()
        {
            EnsureStarted(TimeSpan.FromSeconds(15));
        }

        public void OnOptionsSaved()
        {
            if (!IsEnabled)
            {
                return;
            }

            Interlocked.Increment(ref _version);
            EnsureStarted(TimeSpan.FromSeconds(2));
        }

        private void EnsureStarted(TimeSpan delay)
        {
            if (!IsEnabled || _ready || _isBuilding) return;
            if (Interlocked.Exchange(ref _starting, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay).ConfigureAwait(false);

                    if (!IsEnabled || _ready) return;

                    if (!TryLoad())
                    {
                        await BuildAsync(null, CancellationToken.None).ConfigureAwait(false);
                    }
                    else if (_builtAtUtc.HasValue &&
                             DateTime.UtcNow - _builtAtUtc.Value > TimeSpan.FromHours(24))
                    {
                        _logger?.Info("ChineseSearch - Index is older than 24 hours, rebuilding in background");
                        await BuildAsync(null, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception e)
                {
                    _logger?.Debug("ChineseSearch - Initialize failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _starting, 0);
                }
            });
        }

        public async Task BuildAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!IsEnabled) return;
            if (!await _buildGate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;

            try
            {
                _isBuilding = true;
                _progressPercent = 0;
                _lastError = null;

                _logger?.Info("ChineseSearch - Index build started");

                var options = GetOptions();
                var previous = _snapshot.ToDictionary(i => i.Entry.Id);
                var entries = new List<SearchIndexEntry>();
                var aliasFetches = 0;

                var total = GetTotalItemCount();

                var startIndex = 0;

                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var pageQuery = new InternalItemsQuery
                    {
                        Recursive = true,
                        IncludeItemTypes = IndexedItemTypes,
                        IsVirtualItem = false,
                        Limit = PageSize,
                        StartIndex = startIndex,
                        EnableTotalRecordCount = false
                    };
                    pageQuery.AddToOrderByIfNeeded(ItemSortBy.SortName);
                    var page = _libraryManager.GetItemList(pageQuery);

                    if (page == null || page.Length == 0) break;

                    foreach (var item in page)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var entry = CreateEntry(item);

                        if (previous.TryGetValue(entry.Id, out var old) &&
                            string.Equals(old.Entry.TmdbId, entry.TmdbId, StringComparison.Ordinal))
                        {
                            entry.Aliases = old.Entry.Aliases;
                            entry.AliasesFetched = old.Entry.AliasesFetched;
                        }

                        if (options.TmdbAliasSearch && !entry.AliasesFetched &&
                            !string.IsNullOrEmpty(entry.TmdbId) && aliasFetches < MaxAliasFetchPerBuild)
                        {
                            aliasFetches++;
                            await FetchTmdbAliasesAsync(entry, cancellationToken).ConfigureAwait(false);
                            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                        }

                        entries.Add(entry);
                    }

                    startIndex += page.Length;

                    if (total > 0)
                    {
                        _progressPercent = (int) Math.Min(90, startIndex * 90.0 / total);
                        progress?.Report(_progressPercent);
                    }

                    if (page.Length < PageSize) break;

                    // Yield to foreground work between pages. The index is a background feature
                    // and must not starve library scans or playback.
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var indexed = entries.Select(BuildIndexedItem).ToArray();

                lock (_sync)
                {
                    var dictionary = new Dictionary<long, IndexedItem>(indexed.Length);

                    foreach (var item in indexed)
                    {
                        dictionary[item.Entry.Id] = item;
                    }

                    _items = dictionary;
                    _snapshot = indexed;
                    _builtAtUtc = DateTime.UtcNow;
                    _ready = true;
                    Interlocked.Increment(ref _version);
                }

                _progressPercent = 95;
                progress?.Report(_progressPercent);

                await SaveIndexAsync(entries, cancellationToken).ConfigureAwait(false);

                _progressPercent = 100;
                progress?.Report(_progressPercent);
                _logger?.Info("ChineseSearch - Index build complete: " + entries.Count + " items");
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("ChineseSearch - Index build cancelled");
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                _logger?.Info("ChineseSearch - Index build failed: " + e.Message);
                _logger?.Debug(e.StackTrace);
            }
            finally
            {
                _isBuilding = false;
                _buildGate.Release();
            }
        }

        private int GetTotalItemCount()
        {
            try
            {
                return _libraryManager.GetItemsResult(new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = IndexedItemTypes,
                    IsVirtualItem = false,
                    Limit = 0,
                    EnableTotalRecordCount = true
                }).TotalRecordCount;
            }
            catch (Exception e)
            {
                _logger?.Debug("ChineseSearch - Count query failed: " + e.Message);
                return 0;
            }
        }

        private SearchIndexEntry CreateEntry(BaseItem item)
        {
            var entry = new SearchIndexEntry
            {
                Id = item.InternalId,
                Name = item.Name ?? string.Empty,
                SortName = item.SortName ?? string.Empty,
                OriginalTitle = item.OriginalTitle ?? string.Empty,
                Type = item.GetType().Name,
                Year = item.ProductionYear,
                Rating = item.CommunityRating,
                TmdbId = item.ProviderIds != null && item.ProviderIds.TryGetValue("Tmdb", out var tmdb)
                    ? tmdb
                    : null,
                People = GetActorNames(item),
                ProviderIds = GetSearchableProviderIds(item),
                Aliases = GetAlternateTitles(item).Take(MaxAliasCount).ToList()
            };

            return entry;
        }

        private List<string> GetActorNames(BaseItem item)
        {
            if (!item.SupportsPeople) return new List<string>();

            try
            {
                return _libraryManager.GetItemPeople(item)
                    .Where(person => person != null && person.Type == PersonType.Actor &&
                                     !string.IsNullOrWhiteSpace(person.Name))
                    .Select(person => person.Name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(MaxActorCount)
                    .ToList();
            }
            catch (Exception e)
            {
                _logger?.Debug("ChineseSearch - Actor lookup failed: " + e.Message);
                return new List<string>();
            }
        }

        private static Dictionary<string, string> GetSearchableProviderIds(BaseItem item)
        {
            var providerIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (item.ProviderIds == null) return providerIds;

            foreach (var pair in item.ProviderIds)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;

                var key = SearchTextHelper.Compact(pair.Key);
                string canonicalKey = null;

                if (key == "douban" || key == "doubanid")
                {
                    canonicalKey = "doubanid";
                }
                else if (key == "bgm" || key == "bgmid" || key == "bangumi" || key == "bangumiid")
                {
                    canonicalKey = "bgmid";
                }
                else if (key == "imdb" || key == "imdbid")
                {
                    canonicalKey = "imdbid";
                }

                if (canonicalKey != null)
                {
                    providerIds[canonicalKey] = pair.Value.Trim();
                }
            }

            return providerIds;
        }

        private static IEnumerable<string> GetAlternateTitles(BaseItem item)
        {
            var property = AlternateTitleProperties.GetOrAdd(item.GetType(), type =>
            {
                for (var current = type; current != null; current = current.BaseType)
                {
                    var found = current.GetProperty("AlternateTitles",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly);

                    if (found != null) return found;
                }

                return null;
            });

            if (property == null) yield break;

            object value;

            try
            {
                value = property.GetValue(item);
            }
            catch
            {
                yield break;
            }

            if (!(value is System.Collections.IEnumerable enumerable)) yield break;

            foreach (var element in enumerable)
            {
                if (element is string text)
                {
                    if (!string.IsNullOrWhiteSpace(text)) yield return text.Trim();
                    continue;
                }

                if (element == null) continue;

                var elementType = element.GetType();
                var textProperty = elementType.GetProperty("Name") ??
                                   elementType.GetProperty("Title") ??
                                   elementType.GetProperty("AlternateTitle");

                if (textProperty?.GetValue(element) is string elementText && !string.IsNullOrWhiteSpace(elementText))
                {
                    yield return elementText.Trim();
                }
            }
        }

        internal static IndexedItem BuildIndexedItem(SearchIndexEntry entry)
        {
            var keys = new List<IndexedKey>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void AddKey(string key, SearchKeyKind kind, bool chinese)
            {
                if (string.IsNullOrEmpty(key)) return;

                var identity = ((byte)kind).ToString() + ":" + key;

                if (!seen.Add(identity)) return;

                keys.Add(new IndexedKey
                {
                    Key = key,
                    Kind = (byte)kind,
                    Flags = chinese ? KeyFlags.Chinese : (byte)0
                });
            }

            void AddTitle(string title, SearchKeyKind kind, bool includeYear = true)
            {
                if (string.IsNullOrWhiteSpace(title)) return;

                var chinese = SearchTextHelper.HasChinese(title);
                var variants = new HashSet<string>(StringComparer.Ordinal)
                {
                    SearchTextHelper.Compact(title),
                    SearchTextHelper.Compact(SearchTextHelper.ToSimplified(title)),
                    SearchTextHelper.Compact(SearchTextHelper.ToTraditional(title))
                };

                foreach (var variant in variants)
                {
                    AddKey(variant, kind, chinese);
                }

                var pinyinSources = new HashSet<string>(StringComparer.Ordinal)
                {
                    title,
                    SearchTextHelper.ExtractChinese(title)
                };

                foreach (var pinyinSource in pinyinSources)
                {
                    var pinyin = SearchTextHelper.ToPinyin(pinyinSource);

                    if (string.IsNullOrEmpty(pinyin)) continue;

                    AddKey(pinyin, SearchKeyKind.Pinyin, true);

                    var initials = SearchTextHelper.ToPinyinInitials(pinyinSource);

                    if (!string.IsNullOrEmpty(initials) && initials != pinyin)
                    {
                        AddKey(initials, SearchKeyKind.Initials, true);
                    }
                }

                if (includeYear && entry.Year.HasValue)
                {
                    AddKey(SearchTextHelper.Compact(title) + entry.Year.Value, SearchKeyKind.Year, chinese);
                }
            }

            AddTitle(entry.Name, SearchKeyKind.Name);

            if (!string.IsNullOrEmpty(entry.OriginalTitle) &&
                !string.Equals(entry.OriginalTitle, entry.Name, StringComparison.Ordinal))
            {
                AddTitle(entry.OriginalTitle, SearchKeyKind.OriginalTitle);
            }

            if (!string.IsNullOrEmpty(entry.SortName) &&
                !string.Equals(entry.SortName, entry.Name, StringComparison.Ordinal))
            {
                AddTitle(entry.SortName, SearchKeyKind.SortName);
            }

            foreach (var alias in entry.Aliases ?? Enumerable.Empty<string>())
            {
                AddTitle(alias, SearchKeyKind.Alias);
            }

            foreach (var actor in entry.People ?? Enumerable.Empty<string>())
            {
                AddTitle(actor, SearchKeyKind.Actor, false);
            }

            foreach (var providerId in entry.ProviderIds ?? Enumerable.Empty<KeyValuePair<string, string>>())
            {
                AddKey(SearchTextHelper.Compact(providerId.Value), SearchKeyKind.ExternalId, false);
            }

            return new IndexedItem
            {
                Entry = entry,
                Keys = keys.ToArray()
            };
        }

        public IReadOnlyList<IndexMatch> Match(string query)
        {
            var options = GetOptions();

            if (!options.EnableChineseSearch) return Array.Empty<IndexMatch>();

            var snapshot = _snapshot;

            if (snapshot.Length == 0) return Array.Empty<IndexMatch>();

            var variants = BuildQueryVariants(query, options);

            if (variants.Count == 0) return Array.Empty<IndexMatch>();

            var strong = new List<IndexMatch>();
            var weak = new List<IndexMatch>();
            var fuzzyChecks = 0;
            var queryHasChinese = variants.Any(v => v.HasChinese);
            var smartRanking = options.SmartRanking;

            foreach (var item in snapshot)
            {
                var best = FindBest(item, variants, queryHasChinese, smartRanking, options.PinyinSearch,
                    options.AliasSearch, ref fuzzyChecks);
                if (best == null) continue;

                if (best.Tier <= TierPrefix)
                {
                    strong.Add(best);
                }
                else if (weak.Count < MaxWeakCandidates)
                {
                    weak.Add(best);
                }
            }

            strong.AddRange(weak);

            return strong;
        }

        private static IndexMatch FindBest(IndexedItem item, List<QueryVariant> variants,
            bool queryHasChinese, bool smartRanking, bool allowPinyin, bool allowAlias, ref int fuzzyChecks)
        {
            IndexMatch best = null;

            foreach (var variant in variants)
            {
                foreach (var key in item.Keys)
                {
                    if (string.IsNullOrEmpty(key.Key)) continue;

                    var kind = (SearchKeyKind)key.Kind;

                    if (!allowPinyin && (kind == SearchKeyKind.Pinyin || kind == SearchKeyKind.Initials))
                    {
                        continue;
                    }

                    if (!allowAlias && (kind == SearchKeyKind.OriginalTitle ||
                                        kind == SearchKeyKind.Alias || kind == SearchKeyKind.Year))
                    {
                        continue;
                    }

                    var tier = GetTier(key, variant, ref fuzzyChecks);

                    if (tier < 0) continue;

                    var sourceChinese = (key.Flags & KeyFlags.Chinese) != 0;
                    var languageRank = GetLanguageRank(queryHasChinese, sourceChinese, kind, smartRanking);

                    if (best != null)
                    {
                        var comparison = CompareTier(tier, languageRank, GetKindPriority(kind),
                            best.Tier, best.LanguageRank, best.KindPriority);

                        if (comparison >= 0) continue;
                    }

                    best = new IndexMatch
                    {
                        Item = item,
                        Tier = tier,
                        Kind = kind,
                        SourceChinese = sourceChinese,
                        LanguageRank = languageRank,
                        KindPriority = GetKindPriority(kind)
                    };
                }
            }

            return best;
        }

        private static int GetTier(IndexedKey key, QueryVariant variant, ref int fuzzyChecks)
        {
            var kind = (SearchKeyKind)key.Kind;

            if (kind == SearchKeyKind.Year)
            {
                return string.Equals(key.Key, variant.Text, StringComparison.Ordinal) ? TierExact : -1;
            }

            if (kind == SearchKeyKind.ExternalId)
            {
                return string.Equals(key.Key, variant.Text, StringComparison.Ordinal) ? TierExact : -1;
            }

            if (string.Equals(key.Key, variant.Text, StringComparison.Ordinal)) return TierExact;

            if (key.Key.Length > variant.Text.Length && key.Key.StartsWith(variant.Text, StringComparison.Ordinal))
            {
                return TierPrefix;
            }

            if (variant.Text.Length >= 2 && key.Key.Length > variant.Text.Length &&
                key.Key.IndexOf(variant.Text, StringComparison.Ordinal) > 0)
            {
                if (kind != SearchKeyKind.Initials)
                {
                    return TierContains;
                }
            }

            if (!variant.AllowFuzzy || variant.Text.Length < 2) return -1;

            if (fuzzyChecks >= MaxFuzzyChecks) return -1;

            if (kind != SearchKeyKind.Initials && variant.Text.Length >= 3 &&
                key.Key.Length <= variant.Text.Length * 3 &&
                SearchTextHelper.IsSubsequence(variant.Text, key.Key))
            {
                return TierFuzzy;
            }

            if (kind != SearchKeyKind.Initials && variant.Text.Length >= 3 && key.Key[0] == variant.Text[0])
            {
                fuzzyChecks++;

                var maxDistance = variant.Text.Length >= 7 ? 2 : 1;

                if (SearchTextHelper.IsSimilar(variant.Text, key.Key, maxDistance))
                {
                    return TierFuzzy;
                }
            }

            return -1;
        }

        private static int GetLanguageRank(bool queryHasChinese, bool sourceChinese, SearchKeyKind kind,
            bool smartRanking)
        {
            if (!smartRanking) return 0;

            if (queryHasChinese)
            {
                return sourceChinese ? 0 : 2;
            }

            return kind == SearchKeyKind.Pinyin || kind == SearchKeyKind.Initials ? 1 : 0;
        }

        internal static int GetKindPriority(SearchKeyKind kind)
        {
            switch (kind)
            {
                case SearchKeyKind.Name: return 0;
                case SearchKeyKind.OriginalTitle: return 1;
                case SearchKeyKind.SortName: return 2;
                case SearchKeyKind.Alias: return 3;
                case SearchKeyKind.Actor: return 4;
                case SearchKeyKind.Pinyin: return 5;
                case SearchKeyKind.Initials: return 6;
                case SearchKeyKind.ExternalId: return 7;
                case SearchKeyKind.Year: return 8;
                default: return 9;
            }
        }

        internal static int CompareTier(int tierA, int languageA, int kindA, int tierB, int languageB, int kindB)
        {
            if (tierA != tierB) return tierA < tierB ? -1 : 1;
            if (languageA != languageB) return languageA < languageB ? -1 : 1;
            if (kindA != kindB) return kindA < kindB ? -1 : 1;
            return 0;
        }

        private static List<QueryVariant> BuildQueryVariants(string query, SearchOptions options)
        {
            var variants = new List<QueryVariant>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            void Add(string value, bool hasChinese)
            {
                if (string.IsNullOrEmpty(value) || !seen.Add(value)) return;

                variants.Add(new QueryVariant
                {
                    Text = value,
                    HasChinese = hasChinese,
                    AllowFuzzy = true
                });
            }

            var raw = query?.Trim() ?? string.Empty;
            var compact = SearchTextHelper.Compact(raw);

            if (compact.Length == 0) return variants;

            Add(compact, SearchTextHelper.HasChinese(raw));

            if (options.ConversionSearch)
            {
                Add(SearchTextHelper.Compact(SearchTextHelper.ToSimplified(raw)),
                    SearchTextHelper.HasChinese(SearchTextHelper.ToSimplified(raw)));
                Add(SearchTextHelper.Compact(SearchTextHelper.ToTraditional(raw)),
                    SearchTextHelper.HasChinese(SearchTextHelper.ToTraditional(raw)));
            }

            return variants;
        }

        private static SearchOptions GetOptions()
        {
            return Plugin.Instance?.GetPluginOptions()?.SearchOptions ?? new SearchOptions();
        }

        private bool TryLoad()
        {
            var path = IndexFilePath;

            if (!File.Exists(path)) return false;

            try
            {
                var file = _jsonSerializer.DeserializeFromFile<SearchIndexFile>(path);

                if (file?.Items == null || file.Version != CurrentVersion || file.Items.Count == 0)
                {
                    return false;
                }

                var indexed = file.Items.Select(BuildIndexedItem).ToArray();

                lock (_sync)
                {
                    var dictionary = new Dictionary<long, IndexedItem>(indexed.Length);

                    foreach (var item in indexed)
                    {
                        dictionary[item.Entry.Id] = item;
                    }

                    _items = dictionary;
                    _snapshot = indexed;
                    _builtAtUtc = file.BuiltAtUtc;
                    _ready = true;
                    Interlocked.Increment(ref _version);
                }

                _logger?.Info("ChineseSearch - Index loaded: " + indexed.Length + " items");
                return true;
            }
            catch (Exception e)
            {
                _logger?.Info("ChineseSearch - Failed to load index: " + e.Message);
                return false;
            }
        }

        private async Task SaveIndexAsync(List<SearchIndexEntry> entries, CancellationToken cancellationToken)
        {
            await Task.Run(() =>
            {
                try
                {
                    var folder = Path.GetDirectoryName(IndexFilePath);

                    if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    var file = new SearchIndexFile
                    {
                        Version = CurrentVersion,
                        BuiltAtUtc = _builtAtUtc ?? DateTime.UtcNow,
                        Items = entries
                    };

                    var temporaryPath = IndexFilePath + ".tmp";
                    _jsonSerializer.SerializeToFile(file, temporaryPath);

                    if (File.Exists(IndexFilePath)) File.Delete(IndexFilePath);
                    File.Move(temporaryPath, IndexFilePath);
                }
                catch (Exception e)
                {
                    _logger?.Debug("ChineseSearch - Failed to save index: " + e.Message);
                }
            }, cancellationToken).ConfigureAwait(false);
        }

        private string IndexFilePath
        {
            get
            {
                var folder = Plugin.Instance.DataFolderPath;

                if (string.IsNullOrEmpty(folder))
                {
                    folder = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath,
                        "StrmAssistant");
                }

                return Path.Combine(folder, "search-index.json");
            }
        }

        private async Task FetchTmdbAliasesAsync(SearchIndexEntry entry, CancellationToken cancellationToken)
        {
            entry.AliasesFetched = true;

            try
            {
                if (string.IsNullOrEmpty(entry.TmdbId)) return;

                string kind;

                if (string.Equals(entry.Type, nameof(Movie), StringComparison.Ordinal))
                {
                    kind = "movie";
                }
                else if (string.Equals(entry.Type, nameof(Series), StringComparison.Ordinal))
                {
                    kind = "tv";
                }
                else
                {
                    return;
                }

                var apiKey = MovieDbApi.GetEffectiveApiKey();

                if (string.IsNullOrEmpty(apiKey)) return;

                var root = MovieDbApi.GetEffectiveApiRoot().TrimEnd('/');
                var url = root + "/3/" + kind + "/" + entry.TmdbId +
                          "/alternative_titles?api_key=" + Uri.EscapeDataString(apiKey);

                var options = new HttpRequestOptions
                {
                    Url = url,
                    CancellationToken = cancellationToken,
                    AcceptHeader = "application/json",
                    BufferContent = true,
                    UserAgent = Plugin.Instance.UserAgent
                };

                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.OK) return;

                await using var stream = response.Content;

                var result = _jsonSerializer.DeserializeFromStream<TmdbAlternativeTitles>(stream);

                if (result?.Titles == null) return;

                var aliases = new HashSet<string>(entry.Aliases ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var title in result.Titles)
                {
                    if (string.IsNullOrWhiteSpace(title?.Title)) continue;

                    aliases.Add(title.Title.Trim());

                    if (aliases.Count >= MaxAliasCount) break;
                }

                entry.Aliases = aliases.Take(MaxAliasCount).ToList();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                entry.AliasesFetched = false;
                _logger?.Debug("ChineseSearch - TMDB alias lookup failed: " + e.Message);
            }
        }

        public void NotifyItemChanged(BaseItem item)
        {
            if (item == null || !_ready || !IsEnabled) return;

            _pendingUpdates[item.InternalId] = 0;
            ScheduleUpdate();
        }

        public void NotifyItemRemoved(long itemId)
        {
            if (!_ready || !IsEnabled) return;

            _pendingUpdates[itemId] = 0;
            ScheduleUpdate();
        }

        private void ScheduleUpdate()
        {
            if (Interlocked.Exchange(ref _updateScheduled, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000).ConfigureAwait(false);

                    var ids = _pendingUpdates.Keys.ToArray();

                    foreach (var id in ids)
                    {
                        _pendingUpdates.TryRemove(id, out _);
                    }

                    if (ids.Length == 0) return;

                    var changed = false;

                    lock (_sync)
                    {
                        var dictionary = new Dictionary<long, IndexedItem>(_items);
                        var previous = _snapshot.ToDictionary(i => i.Entry.Id);

                        foreach (var id in ids)
                        {
                            var item = _libraryManager.GetItemById(id);

                            if (item == null || item.IsVirtualItem ||
                                !IndexedItemTypes.Contains(item.GetType().Name))
                            {
                                changed |= dictionary.Remove(id);
                                continue;
                            }

                            var entry = CreateEntry(item);

                            if (previous.TryGetValue(id, out var old) &&
                                string.Equals(old.Entry.TmdbId, entry.TmdbId, StringComparison.Ordinal))
                            {
                                entry.Aliases = old.Entry.Aliases;
                                entry.AliasesFetched = old.Entry.AliasesFetched;
                            }

                            dictionary[id] = BuildIndexedItem(entry);
                            changed = true;
                        }

                        if (changed)
                        {
                            _items = dictionary;
                            _snapshot = dictionary.Values.ToArray();
                            Interlocked.Increment(ref _version);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger?.Debug("ChineseSearch - Incremental index update failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _updateScheduled, 0);

                    if (!_pendingUpdates.IsEmpty)
                    {
                        ScheduleUpdate();
                    }
                }
            });
        }
    }

    public sealed class SearchIndexStatus
    {
        public bool Enabled { get; set; }
        public bool Ready { get; set; }
        public bool IsBuilding { get; set; }
        public int ItemCount { get; set; }
        public DateTime? BuiltAtUtc { get; set; }
        public double Progress { get; set; }
        public string LastError { get; set; }
    }

    public sealed class SearchIndexFile
    {
        public int Version { get; set; }
        public DateTime BuiltAtUtc { get; set; }
        public List<SearchIndexEntry> Items { get; set; } = new List<SearchIndexEntry>();
    }

    public sealed class SearchIndexEntry
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string SortName { get; set; }
        public string OriginalTitle { get; set; }
        public string Type { get; set; }
        public int? Year { get; set; }
        public float? Rating { get; set; }
        public string TmdbId { get; set; }
        public bool AliasesFetched { get; set; }
        public List<string> Aliases { get; set; } = new List<string>();
        public List<string> People { get; set; } = new List<string>();
        public Dictionary<string, string> ProviderIds { get; set; } = new Dictionary<string, string>();
    }

    internal sealed class IndexedItem
    {
        public SearchIndexEntry Entry { get; set; }
        public IndexedKey[] Keys { get; set; }
    }

    internal struct IndexedKey
    {
        public string Key;
        public byte Kind;
        public byte Flags;
    }

    internal static class KeyFlags
    {
        public const byte Chinese = 1;
    }

    internal enum SearchKeyKind : byte
    {
        Name = 0,
        OriginalTitle = 1,
        SortName = 2,
        Alias = 3,
        Pinyin = 4,
        Initials = 5,
        Year = 6,
        Actor = 7,
        ExternalId = 8
    }

    internal sealed class QueryVariant
    {
        public string Text;
        public bool HasChinese;
        public bool AllowFuzzy;
    }

    public sealed class IndexMatch
    {
        internal IndexedItem Item { get; set; }
        public int Tier { get; set; }
        internal SearchKeyKind Kind { get; set; }
        public bool SourceChinese { get; set; }
        internal int LanguageRank { get; set; }
        internal int KindPriority { get; set; }
        public long ItemId => Item.Entry.Id;
        public string Name => Item.Entry.Name;
        public int? Year => Item.Entry.Year;
        public float? Rating => Item.Entry.Rating;
        public string Type => Item.Entry.Type;
    }

    internal sealed class TmdbAlternativeTitles
    {
        public List<TmdbAlternativeTitle> Titles { get; set; }
    }

    internal sealed class TmdbAlternativeTitle
    {
        public string Title { get; set; }
    }
}
