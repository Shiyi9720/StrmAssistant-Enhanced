using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Stores Douban ratings in a standalone JSON file. Ratings are intentionally kept out of
    /// PluginOptions so that a growing item list never bloats the plugin configuration, and a
    /// future UI can read the same file without another Douban request.
    /// The in-memory table is shared by every store instance in the process: the passive
    /// supplement path and the scheduled prebuild path must see (and never overwrite) each
    /// other's writes, so they all read and write the same dictionary and the same file.
    /// </summary>
    public sealed class DoubanRatingStore
    {
        private const string StoreFileName = "StrmAssistant.DoubanRatings.json";

        private static readonly object StateSync = new object();
        private static StoreState _state;

        private readonly StoreState _store;

        public DoubanRatingStore(IJsonSerializer jsonSerializer)
        {
            lock (StateSync)
            {
                if (_state is null)
                {
                    _state = new StoreState(jsonSerializer,
                        Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StoreFileName));
                }

                _store = _state;
            }
        }

        public void Save(long itemId, string doubanId, string name, string itemType, double? rating,
            int? ratingCount)
        {
            _store.Save(itemId, doubanId, name, itemType, rating, ratingCount);
        }

        public DoubanRatingCacheEntry Get(long itemId)
        {
            return _store.Get(itemId);
        }

        private sealed class StoreState
        {
            private readonly IJsonSerializer _jsonSerializer;
            private readonly string _storePath;
            private readonly object _sync = new object();
            private readonly Dictionary<string, DoubanRatingCacheEntry> _entries =
                new Dictionary<string, DoubanRatingCacheEntry>(StringComparer.Ordinal);

            public StoreState(IJsonSerializer jsonSerializer, string storePath)
            {
                _jsonSerializer = jsonSerializer;
                _storePath = storePath;
                Load();
            }

            public void Save(long itemId, string doubanId, string name, string itemType, double? rating,
                int? ratingCount)
            {
                if (itemId <= 0 || !rating.HasValue || rating.Value <= 0) return;

                lock (_sync)
                {
                    _entries[itemId.ToString(CultureInfo.InvariantCulture)] = new DoubanRatingCacheEntry
                    {
                        ItemId = itemId,
                        DoubanId = doubanId,
                        Name = name,
                        ItemType = itemType,
                        Rating = rating,
                        RatingCount = ratingCount,
                        UpdatedAtUtc = DateTime.UtcNow
                    };

                    Persist();
                }
            }

            public DoubanRatingCacheEntry Get(long itemId)
            {
                lock (_sync)
                {
                    return _entries.TryGetValue(itemId.ToString(CultureInfo.InvariantCulture), out var entry)
                        ? entry
                        : null;
                }
            }

            private void Load()
            {
                try
                {
                    if (!File.Exists(_storePath)) return;

                    var file = _jsonSerializer.DeserializeFromFile<DoubanRatingCacheFile>(_storePath);

                    if (file?.Items is null) return;

                    foreach (var pair in file.Items)
                    {
                        if (pair.Value != null) _entries[pair.Key] = pair.Value;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger?.Debug("DoubanRatings - Load failed: " + e.Message);
                }
            }

            private void Persist()
            {
                try
                {
                    _jsonSerializer.SerializeToFile(new DoubanRatingCacheFile
                    {
                        UpdatedAtUtc = DateTime.UtcNow,
                        Items = new Dictionary<string, DoubanRatingCacheEntry>(_entries, StringComparer.Ordinal)
                    }, _storePath);
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger?.Debug("DoubanRatings - Persist failed: " + e.Message);
                }
            }
        }
    }

    public sealed class DoubanRatingCacheEntry
    {
        public long ItemId { get; set; }
        public string DoubanId { get; set; }
        public string Name { get; set; }
        public string ItemType { get; set; }
        public double? Rating { get; set; }
        public int? RatingCount { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    internal sealed class DoubanRatingCacheFile
    {
        public DateTime UpdatedAtUtc { get; set; }
        public Dictionary<string, DoubanRatingCacheEntry> Items { get; set; } =
            new Dictionary<string, DoubanRatingCacheEntry>(StringComparer.Ordinal);
    }
}
