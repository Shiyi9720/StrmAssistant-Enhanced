using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby stores playback position on <see cref="UserItemData"/> only once per item, keyed by
    /// provider id (see <see cref="Video.GetUserDataKey"/>). All versions of a merged movie share
    /// that key, so the built-in user data cannot tell versions apart. This class keeps an extra
    /// per-version position store on the server side, fed by session playback events.
    /// </summary>
    public class VersionPlaybackApi
    {
        private const string StoreFileName = "StrmAssistant.VersionPlayback.json";
        private static readonly TimeSpan Retention = TimeSpan.FromDays(180);
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);

        private readonly ILogger _logger;
        private readonly ISessionManager _sessionManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _storePath;
        private readonly object _sync = new object();
        private readonly Dictionary<string, VersionPlaybackRecord> _records =
            new Dictionary<string, VersionPlaybackRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _multiVersionPlayback =
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastPersistUtc = DateTime.MinValue;
        private bool _initialized;

        public VersionPlaybackApi(ISessionManager sessionManager, IJsonSerializer jsonSerializer)
        {
            _logger = Plugin.Instance.Logger;
            _sessionManager = sessionManager;
            _jsonSerializer = jsonSerializer;
            _storePath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StoreFileName);

            Load();
        }

        public void Initialize()
        {
            if (_initialized) return;

            _sessionManager.PlaybackStart += OnPlaybackStart;
            _sessionManager.PlaybackProgress += OnPlaybackProgress;
            _sessionManager.PlaybackStopped += OnPlaybackStopped;

            _initialized = true;
        }

        public void Dispose()
        {
            if (!_initialized) return;

            _sessionManager.PlaybackStart -= OnPlaybackStart;
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;

            Persist(true);
            _initialized = false;
        }

        public List<VersionPlaybackInfo> GetVersionPlayback(long userId, BaseItem item)
        {
            var itemKey = GetItemKey(item);
            if (string.IsNullOrEmpty(itemKey)) return new List<VersionPlaybackInfo>();

            lock (_sync)
            {
                return _records.Values
                    .Where(r => r.UserId == userId &&
                                string.Equals(r.ItemKey, itemKey, StringComparison.OrdinalIgnoreCase) &&
                                (r.PositionTicks > 0 || r.Played))
                    .Select(r => new VersionPlaybackInfo
                    {
                        MediaSourceId = r.MediaSourceId,
                        ItemId = r.MediaSourceItemId,
                        PositionTicks = r.PositionTicks,
                        Played = r.Played
                    })
                    .ToList();
            }
        }

        private void OnPlaybackStart(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                Track(e, false);
            }
            catch (Exception ex)
            {
                _logger?.Debug("VersionPlayback - PlaybackStart failed: " + ex.Message);
            }
        }

        private void OnPlaybackProgress(object sender, PlaybackProgressEventArgs e)
        {
            try
            {
                Track(e, false);
            }
            catch (Exception ex)
            {
                _logger?.Debug("VersionPlayback - PlaybackProgress failed: " + ex.Message);
            }
        }

        private void OnPlaybackStopped(object sender, PlaybackStopEventArgs e)
        {
            try
            {
                Track(e, true);
            }
            catch (Exception ex)
            {
                _logger?.Debug("VersionPlayback - PlaybackStopped failed: " + ex.Message);
            }
        }

        private void Track(PlaybackProgressEventArgs e, bool stopped)
        {
            if (!(e.Item is Movie movie)) return;
            if (e.Users is null || e.Users.Count == 0) return;

            var stopArgs = e as PlaybackStopEventArgs;
            var playedToCompletion = stopped && stopArgs != null && stopArgs.PlayedToCompletion;

            if (e.PlaybackPositionTicks is null && !playedToCompletion) return;

            var itemKey = GetItemKey(movie);
            if (string.IsNullOrEmpty(itemKey)) return;

            var mediaSourceItemId = e.MediaSource?.ItemId;
            var mediaSourceId = e.MediaSourceId;

            if (string.IsNullOrEmpty(mediaSourceId) && string.IsNullOrEmpty(mediaSourceItemId)) return;

            var sessionKey = GetSessionKey(e, mediaSourceItemId, mediaSourceId);

            if (!_multiVersionPlayback.TryGetValue(sessionKey, out var isMultiVersion))
            {
                isMultiVersion = movie.GetAlternateVersionIds().Count > 0;
                _multiVersionPlayback[sessionKey] = isMultiVersion;
            }

            if (_multiVersionPlayback.Count > 200) TrimSessionCache();

            if (!isMultiVersion) return;

            var positionTicks = e.PlaybackPositionTicks ?? 0;

            if (playedToCompletion)
            {
                positionTicks = 0;
            }
            else if (positionTicks <= 0)
            {
                return;
            }

            var now = DateTime.UtcNow;

            lock (_sync)
            {
                foreach (var user in e.Users)
                {
                    var key = RecordKey(user.InternalId, itemKey, mediaSourceItemId, mediaSourceId);

                    if (!_records.TryGetValue(key, out var record))
                    {
                        record = new VersionPlaybackRecord
                        {
                            UserId = user.InternalId,
                            ItemKey = itemKey,
                            MediaSourceId = mediaSourceId,
                            MediaSourceItemId = mediaSourceItemId
                        };
                        _records[key] = record;
                    }

                    record.PositionTicks = positionTicks;
                    record.Played = playedToCompletion;
                    record.LastPlayedDate = now;

                    if (string.IsNullOrEmpty(record.MediaSourceId)) record.MediaSourceId = mediaSourceId;
                    if (string.IsNullOrEmpty(record.MediaSourceItemId)) record.MediaSourceItemId = mediaSourceItemId;
                }
            }

            Persist(stopped);

            if (stopped)
            {
                _multiVersionPlayback.TryRemove(sessionKey, out _);
            }
        }

        private void TrimSessionCache()
        {
            foreach (var key in _multiVersionPlayback.Keys.Take(100).ToList())
            {
                _multiVersionPlayback.TryRemove(key, out _);
            }
        }

        private static string GetSessionKey(PlaybackProgressEventArgs e, string mediaSourceItemId,
            string mediaSourceId)
        {
            return (e.PlaySessionId ?? string.Empty) + "|" + (e.Session?.Id ?? string.Empty) + "|" +
                   (e.Item?.InternalId.ToString() ?? string.Empty) + "|" +
                   (mediaSourceItemId ?? mediaSourceId);
        }

        private static string GetItemKey(BaseItem item)
        {
            if (item is Video video)
            {
                return Video.GetUserDataKey(video) ?? item.InternalId.ToString();
            }

            return item.InternalId.ToString();
        }

        private static string RecordKey(long userId, string itemKey, string mediaSourceItemId,
            string mediaSourceId)
        {
            return userId + "|" + itemKey + "|" +
                   (string.IsNullOrEmpty(mediaSourceItemId) ? mediaSourceId : mediaSourceItemId);
        }

        private void Persist(bool force)
        {
            lock (_sync)
            {
                if (!force && DateTime.UtcNow - _lastPersistUtc < PersistInterval) return;

                try
                {
                    var cutoff = DateTime.UtcNow - Retention;
                    foreach (var key in _records.Where(r => r.Value.LastPlayedDate < cutoff)
                                 .Select(r => r.Key).ToList())
                    {
                        _records.Remove(key);
                    }

                    var store = new VersionPlaybackStore
                    {
                        Records = _records.Values.ToList()
                    };

                    _jsonSerializer.SerializeToFile(store, _storePath);
                    _lastPersistUtc = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    _logger?.Debug("VersionPlayback - Persist failed: " + ex.Message);
                }
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_storePath)) return;

                var store = _jsonSerializer.DeserializeFromFile<VersionPlaybackStore>(_storePath);

                if (store?.Records is null) return;

                var cutoff = DateTime.UtcNow - Retention;

                foreach (var record in store.Records)
                {
                    if (record is null || string.IsNullOrEmpty(record.ItemKey) ||
                        record.LastPlayedDate < cutoff) continue;

                    var key = RecordKey(record.UserId, record.ItemKey, record.MediaSourceItemId,
                        record.MediaSourceId);

                    _records[key] = record;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug("VersionPlayback - Load failed: " + ex.Message);
            }
        }

        private class VersionPlaybackStore
        {
            public List<VersionPlaybackRecord> Records { get; set; }
        }

        private class VersionPlaybackRecord
        {
            public long UserId { get; set; }
            public string ItemKey { get; set; }
            public string MediaSourceId { get; set; }
            public string MediaSourceItemId { get; set; }
            public long PositionTicks { get; set; }
            public bool Played { get; set; }
            public DateTime LastPlayedDate { get; set; }
        }
    }

    public class VersionPlaybackInfo
    {
        public string MediaSourceId { get; set; }
        public string ItemId { get; set; }
        public long PositionTicks { get; set; }
        public bool Played { get; set; }
    }

    public class VersionPlaybackResponse
    {
        public List<VersionPlaybackInfo> Items { get; set; } = new List<VersionPlaybackInfo>();
    }
}
