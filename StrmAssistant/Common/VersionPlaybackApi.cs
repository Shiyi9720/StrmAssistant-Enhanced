using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Emby stores playback position on <see cref="UserItemData"/> only once per item, keyed by
    /// provider id (see <see cref="Video.GetUserDataKey"/>). All versions of a merged movie share
    /// that key, so the built-in user data cannot tell versions apart. This class keeps an extra
    /// per-version position store on the server side, fed by session playback events. Records are
    /// grouped by provider/name content identity and then matched back to a concrete media source
    /// by source id, file name stem, or resolution/source/codec signature, which also covers the
    /// same movie stored separately in multiple libraries.
    /// </summary>
    public class VersionPlaybackApi
    {
        private const string StoreFileName = "StrmAssistant.VersionPlayback.json";
        private static readonly TimeSpan Retention = TimeSpan.FromDays(180);
        private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);

        private ILogger _logger => Plugin.Instance?.Logger;
        private readonly ISessionManager _sessionManager;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _storePath;
        private readonly object _sync = new object();
        private readonly Dictionary<string, VersionPlaybackRecord> _records =
            new Dictionary<string, VersionPlaybackRecord>(StringComparer.OrdinalIgnoreCase);

        private DateTime _lastPersistUtc = DateTime.MinValue;
        private bool _initialized;

        public VersionPlaybackApi(ISessionManager sessionManager, IJsonSerializer jsonSerializer)
        {
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
            var nameKey = GetNameKey(item);
            if (string.IsNullOrEmpty(itemKey) && string.IsNullOrEmpty(nameKey))
                return new List<VersionPlaybackInfo>();

            List<VersionPlaybackRecord> records;

            lock (_sync)
            {
                records = _records.Values
                    .Where(r => r.UserId == userId &&
                                (MatchesKey(r.ItemKey, itemKey) || MatchesKey(r.NameKey, nameKey) ||
                                 MatchesKey(r.ItemKey, item.InternalId.ToString(CultureInfo.InvariantCulture))) &&
                                (r.PositionTicks > 0 || r.Played))
                    .OrderByDescending(r => r.LastPlayedDate)
                    .ToList();
            }

            var sources = GetCurrentMediaSources(item);

            if (sources is null || sources.Count == 0)
            {
                return records.Select(ToVersionPlaybackInfo).ToList();
            }

            return MapRecordsToSources(records, sources);
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
            var nameKey = GetNameKey(movie);
            if (string.IsNullOrEmpty(itemKey) && string.IsNullOrEmpty(nameKey)) return;

            var mediaSourceItemId = e.MediaSource?.ItemId;
            var mediaSourceId = e.MediaSourceId;

            if (string.IsNullOrEmpty(mediaSourceId) && string.IsNullOrEmpty(mediaSourceItemId)) return;

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
            var versionKey = GetVersionKey(e.MediaSource);
            var versionSignature = GetVersionSignature(e.MediaSource);

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
                            ItemKey = string.IsNullOrEmpty(itemKey) ? nameKey : itemKey,
                            NameKey = nameKey,
                            MediaSourceId = mediaSourceId,
                            MediaSourceItemId = mediaSourceItemId
                        };
                        _records[key] = record;
                    }

                    record.PositionTicks = positionTicks;
                    record.Played = playedToCompletion;
                    record.LastPlayedDate = now;

                    if (string.IsNullOrEmpty(record.ItemKey)) record.ItemKey = itemKey ?? nameKey;
                    if (string.IsNullOrEmpty(record.NameKey)) record.NameKey = nameKey;
                    if (string.IsNullOrEmpty(record.MediaSourceId)) record.MediaSourceId = mediaSourceId;
                    if (string.IsNullOrEmpty(record.MediaSourceItemId)) record.MediaSourceItemId = mediaSourceItemId;
                    if (!string.IsNullOrEmpty(versionKey)) record.VersionKey = versionKey;
                    if (!string.IsNullOrEmpty(versionSignature)) record.VersionSignature = versionSignature;
                }
            }

            Persist(stopped);
        }

        internal static string GetItemKey(BaseItem item)
        {
            if (item is Video video)
            {
                return Video.GetUserDataKey(video) ?? GetNameKey(item);
            }

            return GetNameKey(item) ?? item.InternalId.ToString(CultureInfo.InvariantCulture);
        }

        internal static string GetNameKey(BaseItem item)
        {
            var name = NormalizeIdentity(item?.Name);
            if (string.IsNullOrEmpty(name)) return null;

            return "name|" + name + "|" +
                   (item.ProductionYear ?? 0).ToString(CultureInfo.InvariantCulture);
        }

        internal static bool MatchesKey(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                   string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        internal static List<MediaSourceInfo> GetCurrentMediaSources(BaseItem item)
        {
            try
            {
                return item is Video video ? video.GetMediaSources(true, false, null) : null;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.Logger?.Debug("VersionPlayback - GetMediaSources failed: " + ex.Message);
                return null;
            }
        }

        private static List<VersionPlaybackInfo> MapRecordsToSources(List<VersionPlaybackRecord> records,
            List<MediaSourceInfo> sources)
        {
            var result = new List<VersionPlaybackInfo>();
            var used = new HashSet<VersionPlaybackRecord>();

            foreach (var source in sources)
            {
                if (source is null) continue;

                var record = FindMatchingRecord(records, used,
                                 r => MatchesSourceIds(r, source)) ??
                             FindMatchingRecord(records, used,
                                 r => MatchesKey(r.VersionKey, GetVersionKey(source))) ??
                             FindMatchingRecord(records, used,
                                 r => MatchesKey(r.VersionSignature, GetVersionSignature(source)));

                if (record is null) continue;

                used.Add(record);
                result.Add(new VersionPlaybackInfo
                {
                    MediaSourceId = !string.IsNullOrEmpty(source.Id) ? source.Id : record.MediaSourceId,
                    ItemId = !string.IsNullOrEmpty(source.ItemId) ? source.ItemId : record.MediaSourceItemId,
                    PositionTicks = record.PositionTicks,
                    Played = record.Played
                });
            }

            return result;
        }

        private static VersionPlaybackRecord FindMatchingRecord(List<VersionPlaybackRecord> records,
            HashSet<VersionPlaybackRecord> used, Func<VersionPlaybackRecord, bool> predicate)
        {
            return records.FirstOrDefault(r => !used.Contains(r) && predicate(r));
        }

        private static bool MatchesSourceIds(VersionPlaybackRecord record, MediaSourceInfo source)
        {
            return MatchesKey(record.MediaSourceId, source.Id) ||
                   MatchesKey(record.MediaSourceItemId, source.ItemId);
        }

        private static VersionPlaybackInfo ToVersionPlaybackInfo(VersionPlaybackRecord record)
        {
            return new VersionPlaybackInfo
            {
                MediaSourceId = record.MediaSourceId,
                ItemId = record.MediaSourceItemId,
                PositionTicks = record.PositionTicks,
                Played = record.Played
            };
        }

        internal static string GetVersionKey(MediaSourceInfo source)
        {
            var fileName = GetSourceFileName(source);
            var stem = RemoveExtension(fileName);
            var normalized = NormalizeIdentity(stem);
            return string.IsNullOrEmpty(normalized) ? null : normalized;
        }

        internal static string GetVersionSignature(MediaSourceInfo source)
        {
            if (source is null) return null;

            var video = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStreamType.Video);
            var fileName = (GetSourceFileName(source) ?? string.Empty).ToLowerInvariant();

            var resolution = GetResolutionLabel(video?.Height, fileName);
            var codec = NormalizeCodec(video?.Codec);
            var mediaSource = GetSourceLabel(fileName);

            if (string.IsNullOrEmpty(resolution) && string.IsNullOrEmpty(codec) &&
                string.IsNullOrEmpty(mediaSource))
            {
                return null;
            }

            return resolution + "|" + codec + "|" + mediaSource;
        }

        private static string GetResolutionLabel(int? height, string fileName)
        {
            if (height.HasValue && height.Value > 0)
            {
                if (height.Value >= 2000) return "2160p";
                if (height.Value >= 1000) return "1080p";
                if (height.Value >= 700) return "720p";
                if (height.Value >= 500) return "576p";
                return "480p";
            }

            if (ContainsToken(fileName, "2160p", "4k", "uhd")) return "2160p";
            if (ContainsToken(fileName, "1080p")) return "1080p";
            if (ContainsToken(fileName, "720p")) return "720p";
            if (ContainsToken(fileName, "576p")) return "576p";
            if (ContainsToken(fileName, "480p")) return "480p";

            return null;
        }

        private static string NormalizeCodec(string codec)
        {
            var value = NormalizeIdentity(codec);
            if (string.IsNullOrEmpty(value)) return null;
            if (value == "h265" || value == "hevc") return "hevc";
            if (value == "h264" || value == "avc") return "h264";
            if (value == "av1") return "av1";
            if (value == "vp9") return "vp9";
            return value;
        }

        private static string GetSourceLabel(string fileName)
        {
            if (ContainsToken(fileName, "remux")) return "remux";
            if (ContainsToken(fileName, "webdl", "web-dl")) return "webdl";
            if (ContainsToken(fileName, "webrip")) return "webrip";
            if (ContainsToken(fileName, "bluray", "blu-ray", "bdrip", "brrip")) return "bluray";
            if (ContainsToken(fileName, "hdtv")) return "hdtv";
            if (ContainsToken(fileName, "dvdrip", "dvd")) return "dvd";
            return null;
        }

        private static bool ContainsToken(string value, params string[] tokens)
        {
            return !string.IsNullOrEmpty(value) &&
                   tokens.Any(token => value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetSourceFileName(MediaSourceInfo source)
        {
            if (source is null) return null;

            var path = source.Path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var queryMarker = path.LastIndexOf("?/", StringComparison.Ordinal);
                var isUrl = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                if (queryMarker >= 0)
                {
                    path = path.Substring(queryMarker + 2);
                }
                else if (isUrl && !string.IsNullOrWhiteSpace(source.Name))
                {
                    return source.Name;
                }
                else
                {
                    var queryIndex = path.IndexOf('?');
                    if (queryIndex >= 0) path = path.Substring(0, queryIndex);

                    var slash = path.LastIndexOfAny(new[] { '/', '\\' });
                    if (slash >= 0) path = path.Substring(slash + 1);
                }

                try
                {
                    path = Uri.UnescapeDataString(path);
                }
                catch (UriFormatException)
                {
                }

                if (!string.IsNullOrWhiteSpace(path)) return path;
            }

            return source.Name;
        }

        private static string RemoveExtension(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return fileName;

            var dot = fileName.LastIndexOf('.');
            if (dot <= 0 || dot == fileName.Length - 1) return fileName;

            var extension = fileName.Substring(dot + 1);
            if (extension.Length > 8 || extension.Any(char.IsWhiteSpace)) return fileName;

            if (KnownVideoExtensions.Contains(extension)) return fileName.Substring(0, dot);

            return fileName;
        }

        private static readonly HashSet<string> KnownVideoExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mkv", "mp4", "avi", "ts", "m2ts", "iso", "strm", "mov", "wmv", "flv", "webm",
                "rmvb", "m4v", "mpg", "mpeg"
            };

        private static string NormalizeIdentity(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            var builder = new StringBuilder(value.Length);
            var pendingSpace = false;

            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    if (pendingSpace && builder.Length > 0) builder.Append(' ');
                    builder.Append(char.ToLowerInvariant(character));
                    pendingSpace = false;
                }
                else
                {
                    pendingSpace = true;
                }
            }

            return builder.ToString();
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
            public string NameKey { get; set; }
            public string MediaSourceId { get; set; }
            public string MediaSourceItemId { get; set; }
            public string VersionKey { get; set; }
            public string VersionSignature { get; set; }
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
