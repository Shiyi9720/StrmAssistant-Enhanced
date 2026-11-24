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
        // 6: external-id keys now carry provider prefixes (douban:/bgm:/tmdb:/imdb:/tvdb:), so
        // an old index lacks them; bumping the version forces a rebuild instead of serving the
        // bare-id-only keys. (5 was polyphonic pinyin readings.)
        internal const int CurrentVersion = 6;

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
        private const int MaxBigramPostings = 8000000;

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
        private readonly ConcurrentDictionary<long, int> _pendingUpdates = new ConcurrentDictionary<long, int>();

        private sealed class SearchLookup
        {
            public static readonly SearchLookup Empty = new SearchLookup
            {
                Exact = new Dictionary<string, int[]>(StringComparer.Ordinal),
                Prefix = new Dictionary<string, int[]>(StringComparer.Ordinal),
                Bigrams = null,
                Unigrams = null
            };

            public Dictionary<string, int[]> Exact;
            public Dictionary<string, int[]> Prefix;

            // Null when the library is too large to index every 2-gram within the memory cap;
            // matching then falls back to the old full scan for contains matches.
            public Dictionary<string, int[]> Bigrams;

            // Single CJK character postings, only built when the single-character search option
            // is on. Null when the option is off or the posting cap was hit.
            public Dictionary<string, int[]> Unigrams;
        }

        private sealed class SearchState
        {
            public static readonly SearchState Empty = new SearchState
            {
                Snapshot = Array.Empty<IndexedItem>(),
                Lookup = SearchLookup.Empty
            };

            public IndexedItem[] Snapshot;
            public SearchLookup Lookup;
        }

        private Dictionary<long, IndexedItem> _items = new Dictionary<long, IndexedItem>();
        private volatile SearchState _state = SearchState.Empty;

        private volatile bool _ready;
        private volatile bool _isBuilding;
        private volatile int _progressPercent;
        private volatile string _lastError;
        private DateTime? _builtAtUtc;
        private int _version;
        private int _starting;
        private int _updateScheduled;
        private int _updateVersion;

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
                ItemCount = _state.Snapshot.Length,
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

            RefreshLookup();
            Interlocked.Increment(ref _version);
            EnsureStarted(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Rebuilds only the in-memory lookup so option changes that affect candidate
        /// collection (single-character search) take effect without touching the index file.
        /// </summary>
        private void RefreshLookup()
        {
            IndexedItem[] snapshot;

            lock (_sync)
            {
                snapshot = _state?.Snapshot;
            }

            if (snapshot is null || snapshot.Length == 0) return;

            var lookup = BuildLookup(snapshot, GetOptions().EnableSingleCharacterSearch);

            lock (_sync)
            {
                if (!ReferenceEquals(_state?.Snapshot, snapshot)) return;

                _state = new SearchState
                {
                    Snapshot = snapshot,
                    Lookup = lookup
                };
            }
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
                // Serialize with an incremental update that may already be in flight: once the
                // flag is visible under _sync, that update parks its ids instead of publishing.
                lock (_sync)
                {
                    _isBuilding = true;
                }

                _progressPercent = 0;
                _lastError = null;

                _logger?.Info("ChineseSearch - Index build started");

                var options = GetOptions();
                var previous = _state.Snapshot.ToDictionary(i => i.Entry.Id);
                var entries = new List<SearchIndexEntry>();
                var aliasFetches = 0;

                // Ids are cheap; items are loaded in bounded batches so a deep SortName
                // pagination (StartIndex growing to the library size) is avoided.
                var itemIds = GetIndexItemIds(out var idsAvailable);

                if (!idsAvailable)
                {
                    // Never replace a good index with an empty one because a transient id query
                    // failed.
                    _logger?.Info("ChineseSearch - Index build aborted: item id query failed");
                    return;
                }

                var total = itemIds.Length;

                for (var offset = 0; offset < itemIds.Length; offset += PageSize)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var count = Math.Min(PageSize, itemIds.Length - offset);
                    var batchIds = new long[count];

                    for (var i = 0; i < count; i++)
                    {
                        batchIds[i] = itemIds[offset + i];
                    }

                    var page = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = batchIds });

                    if (page == null) continue;

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

                    if (total > 0)
                    {
                        _progressPercent = (int) Math.Min(90, (offset + count) * 90.0 / total);
                        progress?.Report(_progressPercent);
                    }

                    // Yield to foreground work between pages. The index is a background feature
                    // and must not starve library scans or playback.
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();

                var indexed = new List<IndexedItem>(entries.Count);

                foreach (var entry in entries)
                {
                    indexed.Add(BuildIndexedItemWithPersistedKeys(entry));
                }

                var dictionary = new Dictionary<long, IndexedItem>(indexed.Count);

                foreach (var item in indexed)
                {
                    dictionary[item.Entry.Id] = item;
                }

                // Updates received while the build was running were parked. Merge them into the
                // freshly built dictionary before publishing so the build cannot overwrite them.
                var pendingIds = DrainPendingUpdates();

                foreach (var id in pendingIds)
                {
                    // The dictionary holds the freshly built entries, so alias reuse sees the
                    // newest state rather than the snapshot taken when the build started.
                    ApplyItemToDictionary(dictionary, id, dictionary);
                }

                var snapshot = dictionary.Values.ToArray();
                var lookup = BuildLookup(snapshot, GetOptions().EnableSingleCharacterSearch);

                lock (_sync)
                {
                    _items = dictionary;
                    _state = new SearchState
                    {
                        Snapshot = snapshot,
                        Lookup = lookup
                    };
                    _builtAtUtc = DateTime.UtcNow;
                    _ready = true;
                    Interlocked.Increment(ref _version);
                }

                _progressPercent = 95;
                progress?.Report(_progressPercent);

                var finalEntries = snapshot.Select(i => i.Entry).ToList();

                await SaveIndexAsync(finalEntries, cancellationToken).ConfigureAwait(false);

                _progressPercent = 100;
                progress?.Report(_progressPercent);
                _logger?.Info("ChineseSearch - Index build complete: " + finalEntries.Count + " items");
            }
            catch (OperationCanceledException)
            {
                _logger?.Info("ChineseSearch - Index build cancelled");
            }
            catch (Exception e)
            {
                _lastError = e.Message;
                _logger?.Info("ChineseSearch - Index build failed: " + e.Message + Environment.NewLine + e.StackTrace);
            }
            finally
            {
                lock (_sync)
                {
                    _isBuilding = false;
                }

                _buildGate.Release();

                if (!_pendingUpdates.IsEmpty)
                {
                    ScheduleUpdate();
                }
            }
        }

        private long[] GetIndexItemIds(out bool success)
        {
            try
            {
                var query = new InternalItemsQuery
                {
                    Recursive = true,
                    IncludeItemTypes = IndexedItemTypes,
                    IsVirtualItem = false
                };
                query.AddToOrderByIfNeeded(ItemSortBy.SortName);

                success = true;

                return _libraryManager.GetInternalItemIds(query) ?? Array.Empty<long>();
            }
            catch (Exception e)
            {
                _logger?.Debug("ChineseSearch - Index id query failed: " + e.Message);
                success = false;
                return Array.Empty<long>();
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
                else if (key == "tmdb" || key == "tmdbid")
                {
                    canonicalKey = "tmdbid";
                }
                else if (key == "tvdb" || key == "tvdbid")
                {
                    canonicalKey = "tvdbid";
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
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("ChineseSearch - Failed to read indexed property: " + property.Name +
                               Environment.NewLine + e);
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
                    // Polyphonic characters are indexed with every reading, not just the one
                    // the phrase dictionary picks. Otherwise a valid spelling such as
                    // "fanrenxiuxianzhuan"/"frxxz" for 凡人修仙传 (indexed as ...chuan/frxxc)
                    // never matches: initials deliberately have no fuzzy fallback.
                    foreach (var variant in SearchTextHelper.GetPinyinVariants(pinyinSource))
                    {
                        AddKey(variant.Pinyin, SearchKeyKind.Pinyin, true);

                        if (!string.IsNullOrEmpty(variant.Initials) &&
                            variant.Initials != variant.Pinyin)
                        {
                            AddKey(variant.Initials, SearchKeyKind.Initials, true);
                        }
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
                // Bare id plus provider-prefixed aliases (douban1291546, bgm123456, tmdb550...).
                // A user typing "douban:1291546" compacts to the same key, so the prefix works
                // without a separate query parser.
                foreach (var key in ProviderSearchKeys.KeysFor(providerId.Key, providerId.Value))
                {
                    AddKey(key, SearchKeyKind.ExternalId, false);
                }
            }

            return new IndexedItem
            {
                Entry = entry,
                Keys = keys.ToArray()
            };
        }

        private static IndexedItem BuildIndexedItemWithPersistedKeys(SearchIndexEntry entry)
        {
            var item = BuildIndexedItem(entry);

            entry.Keys = item.Keys
                .Select(key => new SearchIndexKey
                {
                    Key = key.Key,
                    Kind = key.Kind,
                    Flags = key.Flags
                })
                .ToList();

            return item;
        }

        private static IndexedItem IndexedItemFromEntry(SearchIndexEntry entry)
        {
            var storedKeys = entry.Keys;

            if (storedKeys == null || storedKeys.Count == 0)
            {
                // Index files written before keys were persisted (or an entry that genuinely
                // has no keys) still load; only this entry pays the conversion cost.
                return BuildIndexedItem(entry);
            }

            return new IndexedItem
            {
                Entry = entry,
                Keys = storedKeys
                    .Where(key => key != null && !string.IsNullOrEmpty(key.Key))
                    .Select(key => new IndexedKey
                    {
                        Key = key.Key,
                        Kind = key.Kind,
                        Flags = key.Flags
                    })
                    .ToArray()
            };
        }

        private bool ApplyItemToDictionary(Dictionary<long, IndexedItem> dictionary, long id,
            Dictionary<long, IndexedItem> previous)
        {
            var item = _libraryManager.GetItemById(id);

            if (item == null || item.IsVirtualItem || !IndexedItemTypes.Contains(item.GetType().Name))
            {
                return dictionary.Remove(id);
            }

            var entry = CreateEntry(item);

            if (previous.TryGetValue(id, out var old) &&
                string.Equals(old.Entry.TmdbId, entry.TmdbId, StringComparison.Ordinal))
            {
                entry.Aliases = old.Entry.Aliases;
                entry.AliasesFetched = old.Entry.AliasesFetched;
            }

            dictionary[id] = BuildIndexedItemWithPersistedKeys(entry);
            return true;
        }

        private List<long> DrainPendingUpdates()
        {
            var ids = new List<long>();
            var collection = (ICollection<KeyValuePair<long, int>>) _pendingUpdates;

            foreach (var pair in _pendingUpdates)
            {
                // Remove the exact version we observed so an update that arrived after the
                // enumeration is not dropped.
                if (collection.Remove(pair))
                {
                    ids.Add(pair.Key);
                }
            }

            return ids;
        }

        private void RequeuePendingUpdates(List<long> ids)
        {
            foreach (var id in ids)
            {
                _pendingUpdates[id] = Interlocked.Increment(ref _updateVersion);
            }
        }

        private static SearchLookup BuildLookup(IndexedItem[] snapshot, bool includeUnigrams)
        {
            var exact = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var prefix = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var bigrams = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var unigrams = includeUnigrams
                ? new Dictionary<string, List<int>>(StringComparer.Ordinal)
                : null;
            var bigramPostings = 0L;
            var unigramPostings = 0L;
            var bigramsEnabled = true;
            var unigramsEnabled = includeUnigrams;

            for (var index = 0; index < snapshot.Length; index++)
            {
                var keys = snapshot[index].Keys ?? Array.Empty<IndexedKey>();

                foreach (var key in keys)
                {
                    var text = key.Key;

                    if (string.IsNullOrEmpty(text)) continue;

                    AddPosting(exact, text, index);
                    AddPosting(prefix, text.Substring(0, 1), index);

                    if (text.Length >= 2) AddPosting(prefix, text.Substring(0, 2), index);

                    if (unigramsEnabled && IsTitleKeyKind(key.Kind))
                    {
                        foreach (var character in text)
                        {
                            if (!SearchTextHelper.IsCjk(character)) continue;

                            var gram = character.ToString();

                            if (!unigrams.TryGetValue(gram, out var unigramPosting))
                            {
                                unigramPosting = new List<int>();
                                unigrams[gram] = unigramPosting;
                            }

                            if (unigramPosting.Count == 0 || unigramPosting[unigramPosting.Count - 1] != index)
                            {
                                unigramPosting.Add(index);
                                unigramPostings++;
                            }

                            if (unigramPostings <= SingleCharacterSearchPolicy.MaxUnigramPostings) continue;

                            // Above the cap the unigram layer costs more than it saves; drop it and
                            // let single-character queries fall back to exact/prefix only.
                            unigramsEnabled = false;
                            unigrams = null;
                            break;
                        }
                    }

                    if (!bigramsEnabled) continue;

                    for (var i = 0; i + 1 < text.Length; i++)
                    {
                        var gram = text.Substring(i, 2);

                        if (!bigrams.TryGetValue(gram, out var posting))
                        {
                            posting = new List<int>();
                            bigrams[gram] = posting;
                        }

                        if (posting.Count > 0 && posting[posting.Count - 1] == index) continue;

                        posting.Add(index);
                        bigramPostings++;

                        if (bigramPostings <= MaxBigramPostings) continue;

                        // Above the cap the index would cost more memory than the scan it saves.
                        bigramsEnabled = false;
                        bigrams = null;
                        break;
                    }
                }
            }

            return new SearchLookup
            {
                Exact = ToArrays(exact),
                Prefix = ToArrays(prefix),
                Bigrams = bigramsEnabled ? ToArrays(bigrams) : null,
                Unigrams = unigramsEnabled ? ToArrays(unigrams) : null
            };
        }

        private static bool IsTitleKeyKind(byte kind)
        {
            var searchKeyKind = (SearchKeyKind) kind;

            return searchKeyKind == SearchKeyKind.Name ||
                   searchKeyKind == SearchKeyKind.OriginalTitle ||
                   searchKeyKind == SearchKeyKind.SortName ||
                   searchKeyKind == SearchKeyKind.Alias ||
                   searchKeyKind == SearchKeyKind.Actor;
        }

        private static void AddPosting(Dictionary<string, List<int>> map, string key, int index)
        {
            if (!map.TryGetValue(key, out var posting))
            {
                posting = new List<int>();
                map[key] = posting;
            }

            if (posting.Count == 0 || posting[posting.Count - 1] != index)
            {
                posting.Add(index);
            }
        }

        private static Dictionary<string, int[]> ToArrays(Dictionary<string, List<int>> source)
        {
            var result = new Dictionary<string, int[]>(source.Count, StringComparer.Ordinal);

            foreach (var pair in source)
            {
                result[pair.Key] = pair.Value.ToArray();
            }

            return result;
        }

        private static string QueryPrefixBucket(string text)
        {
            return text.Length >= 2 ? text.Substring(0, 2) : text;
        }

        public IReadOnlyList<IndexMatch> Match(string query)
        {
            return Match(query, CancellationToken.None);
        }

        public IReadOnlyList<IndexMatch> Match(string query, CancellationToken cancellationToken)
        {
            var options = GetOptions();

            if (!options.EnableChineseSearch) return Array.Empty<IndexMatch>();

            var state = _state;
            var snapshot = state.Snapshot;

            if (snapshot.Length == 0) return Array.Empty<IndexMatch>();

            var variants = BuildQueryVariants(query, options);

            if (variants.Count == 0) return Array.Empty<IndexMatch>();

            if (cancellationToken.IsCancellationRequested) return Array.Empty<IndexMatch>();

            var strong = new List<IndexMatch>();
            var weak = new List<IndexMatch>();
            var fuzzyChecks = 0;
            var queryHasChinese = variants.Any(v => v.HasChinese);
            var smartRanking = options.SmartRanking;
            var singleCharContains = options.EnableSingleCharacterSearch;
            var cancelled = false;

            void Accept(IndexMatch best)
            {
                if (best == null) return;

                if (best.Tier <= TierPrefix)
                {
                    strong.Add(best);
                }
                else if (weak.Count < MaxWeakCandidates)
                {
                    weak.Add(best);
                }
            }

            // Exact/prefix/contains candidates come from the in-memory lookup, so only these
            // items need the full tier evaluation.
            var candidates = CollectCandidates(state.Lookup, variants, singleCharContains);

            foreach (var index in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                Accept(FindBest(snapshot[index], variants, queryHasChinese, smartRanking,
                    options.PinyinSearch, options.AliasSearch, singleCharContains, ref fuzzyChecks));
            }

            var containsScanNeeded = state.Lookup.Bigrams == null && variants.Any(v => v.Text.Length >= 2);
            var fuzzyScanNeeded = fuzzyChecks < MaxFuzzyChecks &&
                                  variants.Any(v => v.AllowFuzzy && v.Text.Length >= 3);

            if (!cancelled && (containsScanNeeded || fuzzyScanNeeded))
            {
                if (containsScanNeeded)
                {
                    // Without a bigram index, contains matches still require the full scan.
                    for (var index = 0; index < snapshot.Length; index++)
                    {
                        if (candidates.Contains(index)) continue;

                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        Accept(FindBest(snapshot[index], variants, queryHasChinese, smartRanking,
                            options.PinyinSearch, options.AliasSearch, singleCharContains,
                            ref fuzzyChecks));
                    }
                }
                else
                {
                    // Every contains match is already in the candidate set; the remaining items
                    // can only contribute the weak fuzzy tier.
                    for (var index = 0; index < snapshot.Length; index++)
                    {
                        if (candidates.Contains(index)) continue;

                        if (cancellationToken.IsCancellationRequested)
                        {
                            cancelled = true;
                            break;
                        }

                        Accept(FindFuzzy(snapshot[index], variants, queryHasChinese, smartRanking,
                            options.PinyinSearch, options.AliasSearch, ref fuzzyChecks));
                    }
                }
            }

            if (cancelled)
            {
                _logger?.Debug("ChineseSearch - Match cancelled or timed out, returning partial results");
            }

            strong.AddRange(weak);

            return strong;
        }

        private static HashSet<int> CollectCandidates(SearchLookup lookup, List<QueryVariant> variants,
            bool allowSingleCharContains)
        {
            var candidates = new HashSet<int>();

            void AddAll(int[] values)
            {
                if (values == null) return;

                foreach (var value in values)
                {
                    candidates.Add(value);
                }
            }

            foreach (var variant in variants)
            {
                var text = variant.Text;

                if (string.IsNullOrEmpty(text)) continue;

                if (lookup.Exact.TryGetValue(text, out var exact)) AddAll(exact);

                if (lookup.Prefix.TryGetValue(QueryPrefixBucket(text), out var prefix)) AddAll(prefix);

                if (text.Length == 1)
                {
                    if (allowSingleCharContains && lookup.Unigrams != null &&
                        lookup.Unigrams.TryGetValue(text, out var unigram) &&
                        SingleCharacterSearchPolicy.MayUsePosting(true, text, variant.HasChinese,
                            unigram.Length))
                    {
                        AddAll(unigram);
                    }

                    continue;
                }

                if (lookup.Bigrams == null || text.Length < 2) continue;

                int[] shortest = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var missing = false;

                for (var i = 0; i + 1 < text.Length; i++)
                {
                    var gram = text.Substring(i, 2);

                    if (!seen.Add(gram)) continue;

                    if (!lookup.Bigrams.TryGetValue(gram, out var posting))
                    {
                        missing = true;
                        break;
                    }

                    if (shortest == null || posting.Length < shortest.Length) shortest = posting;
                }

                if (!missing) AddAll(shortest);
            }

            return candidates;
        }

        private static IndexMatch FindBest(IndexedItem item, List<QueryVariant> variants,
            bool queryHasChinese, bool smartRanking, bool allowPinyin, bool allowAlias,
            bool allowSingleCharContains, ref int fuzzyChecks)
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

                    var tier = GetTier(key, variant, allowSingleCharContains, ref fuzzyChecks);

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

                    // No later key can beat an exact name match with the best language rank.
                    if (tier == TierExact && languageRank == 0 && best.KindPriority == 0)
                    {
                        return best;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// Evaluates only the fuzzy tier. Used for items that are known not to match exactly,
        /// by prefix or by substring, so the full key loop does not repeat those checks.
        /// </summary>
        private static IndexMatch FindFuzzy(IndexedItem item, List<QueryVariant> variants,
            bool queryHasChinese, bool smartRanking, bool allowPinyin, bool allowAlias, ref int fuzzyChecks)
        {
            IndexMatch best = null;

            foreach (var variant in variants)
            {
                if (!variant.AllowFuzzy || variant.Text.Length < 3) continue;

                foreach (var key in item.Keys)
                {
                    if (string.IsNullOrEmpty(key.Key)) continue;

                    var kind = (SearchKeyKind)key.Kind;

                    if (kind == SearchKeyKind.Initials || kind == SearchKeyKind.Year ||
                        kind == SearchKeyKind.ExternalId)
                    {
                        continue;
                    }

                    if (!allowPinyin && kind == SearchKeyKind.Pinyin) continue;

                    if (!allowAlias && (kind == SearchKeyKind.OriginalTitle ||
                                        kind == SearchKeyKind.Alias || kind == SearchKeyKind.Year))
                    {
                        continue;
                    }

                    var tier = GetFuzzyTier(key, variant, ref fuzzyChecks);

                    if (tier < 0) continue;

                    var sourceChinese = (key.Flags & KeyFlags.Chinese) != 0;
                    var languageRank = GetLanguageRank(queryHasChinese, sourceChinese, kind, smartRanking);
                    var kindPriority = GetKindPriority(kind);

                    if (best != null)
                    {
                        var comparison = CompareTier(tier, languageRank, kindPriority,
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
                        KindPriority = kindPriority
                    };
                }
            }

            return best;
        }

        private static int GetFuzzyTier(IndexedKey key, QueryVariant variant, ref int fuzzyChecks)
        {
            if (fuzzyChecks >= MaxFuzzyChecks) return -1;

            if (variant.Text.Length >= 3 && key.Key.Length <= variant.Text.Length * 3 &&
                SearchTextHelper.IsSubsequence(variant.Text, key.Key))
            {
                return TierFuzzy;
            }

            if (variant.Text.Length >= 3 && key.Key.Length > 0 && key.Key[0] == variant.Text[0])
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

        private static int GetTier(IndexedKey key, QueryVariant variant, bool allowSingleCharContains,
            ref int fuzzyChecks)
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

            var allowsContains = variant.Text.Length >= 2 ||
                                 SingleCharacterSearchPolicy.AllowsContains(
                                     allowSingleCharContains, variant.Text, variant.HasChinese);

            if (allowsContains && key.Key.Length > variant.Text.Length &&
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

                var indexed = file.Items.Select(IndexedItemFromEntry).ToArray();
                var dictionary = new Dictionary<long, IndexedItem>(indexed.Length);

                foreach (var item in indexed)
                {
                    dictionary[item.Entry.Id] = item;
                }

                // Keys were persisted with the index, so loading no longer repeats the
                // pinyin/simplified/traditional conversion for every title.
                var lookup = BuildLookup(indexed, GetOptions().EnableSingleCharacterSearch);

                lock (_sync)
                {
                    _items = dictionary;
                    _state = new SearchState
                    {
                        Snapshot = indexed,
                        Lookup = lookup
                    };
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
            if (item == null || !IsEnabled) return;
            if (!_ready && !_isBuilding) return;

            _pendingUpdates[item.InternalId] = Interlocked.Increment(ref _updateVersion);
            ScheduleUpdate();
        }

        public void NotifyItemRemoved(long itemId)
        {
            if (!IsEnabled) return;
            if (!_ready && !_isBuilding) return;

            _pendingUpdates[itemId] = Interlocked.Increment(ref _updateVersion);
            ScheduleUpdate();
        }

        private void ScheduleUpdate()
        {
            // While a full build runs, updates are only parked in _pendingUpdates. The build
            // merges them before publishing and schedules another pass in its finally block.
            if (_isBuilding) return;
            if (Interlocked.Exchange(ref _updateScheduled, 1) == 1) return;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000).ConfigureAwait(false);

                    if (_isBuilding) return;

                    var ids = DrainPendingUpdates();

                    if (ids.Count == 0) return;

                    var changed = false;

                    lock (_sync)
                    {
                        if (_isBuilding)
                        {
                            // The build owns the index right now; hand these ids back so its
                            // merge step or the follow-up pass sees them.
                            RequeuePendingUpdates(ids);
                            return;
                        }

                        var dictionary = new Dictionary<long, IndexedItem>(_items);
                        var previous = _state.Snapshot.ToDictionary(i => i.Entry.Id);

                        foreach (var id in ids)
                        {
                            changed |= ApplyItemToDictionary(dictionary, id, previous);
                        }

                        if (!changed) return;

                        var snapshot = dictionary.Values.ToArray();
                        var lookup = BuildLookup(snapshot, GetOptions().EnableSingleCharacterSearch);

                        _items = dictionary;
                        _state = new SearchState
                        {
                            Snapshot = snapshot,
                            Lookup = lookup
                        };
                        Interlocked.Increment(ref _version);
                    }
                }
                catch (Exception e)
                {
                    _logger?.Debug("ChineseSearch - Incremental index update failed: " + e.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref _updateScheduled, 0);

                    if (!_pendingUpdates.IsEmpty && !_isBuilding)
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

        /// <summary>
        /// The keys computed by <see cref="SearchIndexService.BuildIndexedItem"/>. Persisting
        /// them means a restart does not re-run the pinyin/simplified/traditional conversion for
        /// every title, actor and alias.
        /// </summary>
        public List<SearchIndexKey> Keys { get; set; }
    }

    public sealed class SearchIndexKey
    {
        public string Key { get; set; }
        public byte Kind { get; set; }
        public byte Flags { get; set; }
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
