using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Stores Douban ratings in a standalone JSON file. Ratings are intentionally kept out of
    /// PluginOptions so that a growing item list never bloats the plugin configuration, and a
    /// future UI can read the same file without another Douban request.
    /// The in-memory table is shared by every store instance in the process: the passive
    /// supplement path and the scheduled prebuild path must see (and never overwrite) each
    /// other's writes, so they all read and write the same dictionary and the same file.
    /// Writes are dirty flagged and coalesced: <see cref="Save"/> never serializes on the caller's
    /// thread, the snapshot is taken under the lock while the file I/O happens outside it, and the
    /// new file is swapped in atomically so a reader never sees a half written file. Rebuilding a
    /// rating prebuild therefore costs one write per throttle window instead of one write per item.
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

        /// <summary>
        /// Writes out the ratings that are still dirty. A rating burst relies on the scheduled
        /// flush; this is the last chance to persist before the process goes away.
        /// </summary>
        public static void Flush()
        {
            StoreState state;

            lock (StateSync)
            {
                state = _state;
            }

            state?.PersistIfNeeded(true);
        }

        private sealed class StoreState
        {
            private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(10);

            private readonly IJsonSerializer _jsonSerializer;
            private readonly string _storePath;
            private readonly object _sync = new object();

            // Serializes snapshot plus write so an older snapshot can never overwrite a newer one.
            private readonly object _persistSync = new object();

            private readonly Dictionary<string, DoubanRatingCacheEntry> _entries =
                new Dictionary<string, DoubanRatingCacheEntry>(StringComparer.Ordinal);

            private readonly Timer _flushTimer;
            private bool _dirty;
            private DateTime _lastPersistUtc = DateTime.MinValue;
            private int _flushScheduled;

            public StoreState(IJsonSerializer jsonSerializer, string storePath)
            {
                _jsonSerializer = jsonSerializer;
                _storePath = storePath;
                _flushTimer = new Timer(OnFlushTimer, null, Timeout.Infinite, Timeout.Infinite);
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

                    _dirty = true;
                }

                PersistIfNeeded();
                ScheduleFlush();
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

            // The first change after a window is written straight away; a burst of saves is
            // coalesced into one write per window and the trailing one is covered by the timer.
            public void PersistIfNeeded(bool force = false)
            {
                Dictionary<string, DoubanRatingCacheEntry> snapshot;

                lock (_persistSync)
                {
                    lock (_sync)
                    {
                        if (!_dirty) return;
                        if (!force && DateTime.UtcNow - _lastPersistUtc < PersistInterval) return;

                        snapshot = new Dictionary<string, DoubanRatingCacheEntry>(_entries, StringComparer.Ordinal);
                        _dirty = false;
                        _lastPersistUtc = DateTime.UtcNow;
                    }

                    try
                    {
                        WriteAtomically(new DoubanRatingCacheFile
                        {
                            UpdatedAtUtc = DateTime.UtcNow,
                            Items = snapshot
                        });
                    }
                    catch (Exception e)
                    {
                        Plugin.Instance.Logger?.Debug("DoubanRatings - Persist failed: " + e.Message);

                        // Keep the data dirty so the next save or the shutdown flush retries it.
                        lock (_sync)
                        {
                            _dirty = true;
                        }
                    }
                }
            }

            private void ScheduleFlush()
            {
                lock (_sync)
                {
                    if (!_dirty) return;
                }

                if (Interlocked.CompareExchange(ref _flushScheduled, 1, 0) != 0) return;

                _flushTimer.Change(PersistInterval, Timeout.InfiniteTimeSpan);
            }

            private void OnFlushTimer(object state)
            {
                try
                {
                    Interlocked.Exchange(ref _flushScheduled, 0);
                    PersistIfNeeded(true);
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger?.Debug("DoubanRatings - Scheduled flush failed: " + e.Message);
                }
            }

            // Serializing into a sibling temp file and replacing the target keeps the JSON shape and
            // the file name, and leaves either the old or the new file in place, never a partial one.
            private void WriteAtomically(DoubanRatingCacheFile file)
            {
                var tempPath = _storePath + ".tmp";

                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                               FileShare.None))
                    {
                        _jsonSerializer.SerializeToStream(file, stream);
                    }

                    try
                    {
                        if (File.Exists(_storePath))
                        {
                            File.Replace(tempPath, _storePath, null);
                        }
                        else
                        {
                            File.Move(tempPath, _storePath);
                        }
                    }
                    catch (Exception e)
                    {
                        // The swap renames over an existing file, which some network mounts refuse;
                        // fall back to a plain overwrite so the ratings still reach the file.
                        Plugin.Instance.Logger?.Debug(
                            "DoubanRatings - Atomic replace failed, falling back: " + e.Message);
                        File.Copy(tempPath, _storePath, true);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempPath)) File.Delete(tempPath);
                    }
                    catch (Exception e)
                    {
                        Plugin.Instance?.Logger?.Debug("DoubanRatingStore - Failed to delete temporary file: " +
                                                       tempPath + Environment.NewLine + e);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Flushes the pending rating writes on server shutdown. Emby resolves every concrete
    /// <see cref="IServerEntryPoint"/> in the plugin assembly and disposes it while the application
    /// host shuts down, which is the teardown hook available to a plugin.
    /// </summary>
    public sealed class DoubanRatingShutdownEntryPoint : IServerEntryPoint
    {
        public void Run()
        {
        }

        public void Dispose()
        {
            DoubanRatingStore.Flush();
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
