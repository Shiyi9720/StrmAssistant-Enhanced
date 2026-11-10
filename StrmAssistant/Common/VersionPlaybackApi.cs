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
    ///
    /// When <see cref="Options.VersionPlaybackOptions.PersistPerVersionResume"/> is enabled the
    /// store is keyed by (item id, media source id) instead: a resume point is kept for each
    /// version, switching versions converts the position once by runtime ratio, and the finished
    /// state stays shared. The option is OFF by default and the previous behaviour is preserved
    /// byte for byte in that case.
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
        private bool _migratedOnLoad;

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

            // Legacy records were normalized in memory by Load; write the migrated shape once.
            if (_migratedOnLoad && IsPerVersionResumeEnabled()) Persist(true);

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
            return IsPerVersionResumeEnabled()
                ? GetPerVersionPlayback(userId, item)
                : GetLegacyPlayback(userId, item);
        }

        private List<VersionPlaybackInfo> GetLegacyPlayback(long userId, BaseItem item)
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

        /// <summary>
        /// Reads the resume point that belongs to each concrete version. An exact record wins and
        /// is returned untouched (a). When the target version has no record the most recent
        /// unfinished record in the same content group is converted once by runtime ratio and
        /// persisted as the target version's own record (b). The finished state is still shared
        /// across versions (c). Legacy records that carry no media source id are treated as a
        /// wildcard, completed in place and written back (d).
        /// </summary>
        private List<VersionPlaybackInfo> GetPerVersionPlayback(long userId, BaseItem item)
        {
            var itemKey = GetItemKey(item);
            var nameKey = GetNameKey(item);
            if (string.IsNullOrEmpty(itemKey) && string.IsNullOrEmpty(nameKey))
                return new List<VersionPlaybackInfo>();

            var sources = GetCurrentMediaSources(item);
            var result = new List<VersionPlaybackInfo>();
            var used = new HashSet<VersionPlaybackRecord>();
            var changed = false;

            lock (_sync)
            {
                var records = _records.Values
                    .Where(r => r.UserId == userId &&
                                (MatchesKey(r.ItemKey, itemKey) || MatchesKey(r.NameKey, nameKey) ||
                                 MatchesKey(r.ItemKey, item.InternalId.ToString(CultureInfo.InvariantCulture))) &&
                                (r.PositionTicks > 0 || r.Played))
                    .OrderByDescending(r => r.LastPlayedDate)
                    .ToList();

                if (sources is null || sources.Count == 0)
                {
                    return records.Select(ToVersionPlaybackInfo).ToList();
                }

                // The most recent state wins: if the user just finished one version, every version
                // is reported as played; if the newest event is unfinished progress, conversion is
                // used.
                var sharedPlayed = records.FirstOrDefault()?.Played == true;

                // Resolve exact per-version records first so a record is never reused for another
                // source and so conversions can only draw from genuinely other versions.
                var exact = new Dictionary<MediaSourceInfo, VersionPlaybackRecord>();

                foreach (var source in sources.Where(s => s != null))
                {
                    var record = records.FirstOrDefault(r => !used.Contains(r) &&
                        (IsSameVersion(r, source) || IsLooseVersionMatch(r, source)));

                    if (record is null) continue;

                    used.Add(record);
                    exact[source] = record;
                }

                foreach (var source in sources)
                {
                    if (source is null) continue;

                    if (exact.TryGetValue(source, out var record))
                    {
                        if (MigrateRecord(record, source)) changed = true;

                        result.Add(new VersionPlaybackInfo
                        {
                            MediaSourceId = !string.IsNullOrEmpty(source.Id) ? source.Id : record.MediaSourceId,
                            ItemId = !string.IsNullOrEmpty(source.ItemId) ? source.ItemId : record.MediaSourceItemId,
                            PositionTicks = record.PositionTicks,
                            Played = record.Played
                        });

                        _logger?.Debug(
                            $"VersionPlayback - resume hit: source={source.Id}, item={RecordItemId(record)}, " +
                            $"position={record.PositionTicks}, played={record.Played}, hit=true");
                        continue;
                    }

                    if (sharedPlayed)
                    {
                        result.Add(new VersionPlaybackInfo
                        {
                            MediaSourceId = source.Id,
                            ItemId = source.ItemId,
                            PositionTicks = 0,
                            Played = true
                        });

                        _logger?.Debug(
                            $"VersionPlayback - resume hit: source={source.Id}, played shared across versions, " +
                            "position=0, hit=false");
                        continue;
                    }

                    var candidate = records.FirstOrDefault(r =>
                        !used.Contains(r) && !r.Played && r.PositionTicks > 0 &&
                        !sources.Any(s => s != null &&
                                          (IsSameVersion(r, s) || IsLooseVersionMatch(r, s))));

                    if (candidate is null)
                    {
                        _logger?.Debug(
                            $"VersionPlayback - resume miss: source={source.Id}, no record for this version, hit=false");
                        continue;
                    }

                    used.Add(candidate);

                    var sourceRuntime = ResolveRuntime(candidate, sources);
                    var targetRuntime = source.RunTimeTicks.GetValueOrDefault();

                    if (targetRuntime <= 0) targetRuntime = item.RunTimeTicks.GetValueOrDefault();

                    var converted = ResumePositionMath.ConvertPosition(candidate.PositionTicks,
                        sourceRuntime, targetRuntime);
                    var itemId = !string.IsNullOrEmpty(source.ItemId)
                        ? source.ItemId
                        : item.InternalId.ToString(CultureInfo.InvariantCulture);
                    var mediaSourceId = !string.IsNullOrEmpty(source.Id) ? source.Id : null;
                    var convertedRecord = CreateRecord(userId, itemKey, nameKey, itemId, mediaSourceId,
                        source, converted, targetRuntime, false, DateTime.UtcNow);

                    _records[PerVersionKey(userId, itemId, mediaSourceId)] = convertedRecord;
                    changed = true;

                    result.Add(new VersionPlaybackInfo
                    {
                        MediaSourceId = mediaSourceId,
                        ItemId = itemId,
                        PositionTicks = converted,
                        Played = false
                    });

                    _logger?.Debug(
                        $"VersionPlayback - resume converted: from={candidate.MediaSourceId}, to={mediaSourceId}, " +
                        $"position={candidate.PositionTicks}->{converted}, runtime={sourceRuntime}->{targetRuntime}, " +
                        "hit=false");
                }
            }

            if (changed) Persist(true);

            return result;
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

            var perVersion = IsPerVersionResumeEnabled();
            var mediaSourceItemId = e.MediaSource?.ItemId;
            var mediaSourceId = !string.IsNullOrEmpty(e.MediaSourceId) || !perVersion
                ? e.MediaSourceId
                : e.MediaSource?.Id;

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
            var itemId = !string.IsNullOrEmpty(mediaSourceItemId)
                ? mediaSourceItemId
                : movie.InternalId.ToString(CultureInfo.InvariantCulture);
            var runtimeTicks = e.MediaSource?.RunTimeTicks ?? movie.RunTimeTicks ?? 0;
            var versionKey = GetVersionKey(e.MediaSource);
            var versionSignature = GetVersionSignature(e.MediaSource);

            lock (_sync)
            {
                foreach (var user in e.Users)
                {
                    var key = perVersion
                        ? PerVersionKey(user.InternalId, itemId, mediaSourceId)
                        : RecordKey(user.InternalId, itemKey, mediaSourceItemId, mediaSourceId);

                    if (!_records.TryGetValue(key, out var record) && perVersion)
                    {
                        // Reuse a legacy record for the same concrete version and move it to the
                        // per-version key, so switching versions does not accumulate duplicates.
                        var existing = _records.Values
                            .Where(r => r.UserId == user.InternalId &&
                                        IsSameRecordVersion(r, itemId, mediaSourceId))
                            .OrderByDescending(r => r.LastPlayedDate)
                            .FirstOrDefault();

                        if (existing != null)
                        {
                            var oldKey = _records.FirstOrDefault(kv => ReferenceEquals(kv.Value, existing)).Key;
                            if (oldKey != null) _records.Remove(oldKey);
                            _records[key] = existing;
                            record = existing;
                        }
                    }

                    if (record is null)
                    {
                        record = CreateRecord(user.InternalId, itemKey, nameKey, itemId, mediaSourceId,
                            e.MediaSource, 0, runtimeTicks, false, now);
                        _records[key] = record;
                    }

                    record.PositionTicks = positionTicks;
                    record.Played = playedToCompletion;
                    record.LastPlayedDate = now;

                    if (string.IsNullOrEmpty(record.ItemKey)) record.ItemKey = itemKey ?? nameKey;
                    if (string.IsNullOrEmpty(record.NameKey)) record.NameKey = nameKey;
                    if (!string.IsNullOrEmpty(itemId)) record.ItemId = itemId;
                    if (string.IsNullOrEmpty(record.MediaSourceId)) record.MediaSourceId = mediaSourceId;
                    if (string.IsNullOrEmpty(record.MediaSourceItemId)) record.MediaSourceItemId = mediaSourceItemId;
                    if (runtimeTicks > 0) record.RunTimeTicks = runtimeTicks;
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

        private static bool IsPerVersionResumeEnabled()
        {
            try
            {
                return Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions
                    ?.VersionPlaybackOptions?.PersistPerVersionResume ?? false;
            }
            catch
            {
                return false;
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

        /// <summary>
        /// Matches a record to the concrete version it belongs to. A missing media source id (old
        /// format) is treated as a wildcard for the same item; a missing item id is matched by
        /// source id only. Records that carry no ids at all are matched loosely by file identity.
        /// </summary>
        private static bool IsSameVersion(VersionPlaybackRecord record, MediaSourceInfo source)
        {
            // The media source id is the precise discriminator: merged versions may all report the
            // same owning item id to the client, so an item-id match is only used for old records
            // that have no media source id yet.
            if (!string.IsNullOrEmpty(record.MediaSourceId))
                return MatchesKey(record.MediaSourceId, source.Id);

            var recordItemId = RecordItemId(record);

            return !string.IsNullOrEmpty(recordItemId) && MatchesKey(recordItemId, source.ItemId);
        }

        private static bool IsLooseVersionMatch(VersionPlaybackRecord record, MediaSourceInfo source)
        {
            if (!string.IsNullOrEmpty(record.MediaSourceId) || !string.IsNullOrEmpty(RecordItemId(record)))
                return false;

            return MatchesKey(record.VersionKey, GetVersionKey(source)) ||
                   MatchesKey(record.VersionSignature, GetVersionSignature(source));
        }

        private static bool IsSameRecordVersion(VersionPlaybackRecord record, string itemId,
            string mediaSourceId)
        {
            if (record.UserId <= 0) return false;

            var recordItemId = RecordItemId(record);

            if (!string.IsNullOrEmpty(recordItemId) && !string.IsNullOrEmpty(itemId) &&
                !MatchesKey(recordItemId, itemId))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(record.MediaSourceId) && !string.IsNullOrEmpty(mediaSourceId) &&
                !MatchesKey(record.MediaSourceId, mediaSourceId))
            {
                return false;
            }

            return !string.IsNullOrEmpty(recordItemId) || !string.IsNullOrEmpty(record.MediaSourceId) ||
                   !string.IsNullOrEmpty(record.VersionKey) || !string.IsNullOrEmpty(record.VersionSignature);
        }

        private static string RecordItemId(VersionPlaybackRecord record)
        {
            return !string.IsNullOrEmpty(record.ItemId) ? record.ItemId : record.MediaSourceItemId;
        }

        private static bool MigrateRecord(VersionPlaybackRecord record, MediaSourceInfo source)
        {
            var changed = false;

            if (string.IsNullOrEmpty(record.ItemId) && !string.IsNullOrEmpty(source.ItemId))
            {
                record.ItemId = source.ItemId;
                changed = true;
            }

            if (string.IsNullOrEmpty(record.MediaSourceItemId) && !string.IsNullOrEmpty(source.ItemId))
            {
                record.MediaSourceItemId = source.ItemId;
                changed = true;
            }

            if (string.IsNullOrEmpty(record.MediaSourceId) && !string.IsNullOrEmpty(source.Id))
            {
                record.MediaSourceId = source.Id;
                changed = true;
            }

            if (record.RunTimeTicks <= 0 && source.RunTimeTicks > 0)
            {
                record.RunTimeTicks = source.RunTimeTicks.Value;
                changed = true;
            }

            if (string.IsNullOrEmpty(record.VersionKey) && !string.IsNullOrEmpty(GetVersionKey(source)))
            {
                record.VersionKey = GetVersionKey(source);
                changed = true;
            }

            if (string.IsNullOrEmpty(record.VersionSignature) &&
                !string.IsNullOrEmpty(GetVersionSignature(source)))
            {
                record.VersionSignature = GetVersionSignature(source);
                changed = true;
            }

            return changed;
        }

        private static long ResolveRuntime(VersionPlaybackRecord record, List<MediaSourceInfo> sources)
        {
            if (record.RunTimeTicks > 0) return record.RunTimeTicks;

            var source = sources.FirstOrDefault(s => s != null && IsSameVersion(record, s)) ??
                         sources.FirstOrDefault(s => s != null && IsLooseVersionMatch(record, s));

            return source?.RunTimeTicks.GetValueOrDefault() ?? 0;
        }

        private static VersionPlaybackRecord CreateRecord(long userId, string itemKey, string nameKey,
            string itemId, string mediaSourceId, MediaSourceInfo source, long positionTicks,
            long runtimeTicks, bool played, DateTime lastPlayedDate)
        {
            return new VersionPlaybackRecord
            {
                UserId = userId,
                ItemKey = string.IsNullOrEmpty(itemKey) ? nameKey : itemKey,
                NameKey = nameKey,
                ItemId = itemId,
                MediaSourceId = mediaSourceId,
                MediaSourceItemId = source?.ItemId,
                VersionKey = GetVersionKey(source),
                VersionSignature = GetVersionSignature(source),
                PositionTicks = positionTicks,
                Played = played,
                RunTimeTicks = runtimeTicks > 0 ? runtimeTicks : source?.RunTimeTicks.GetValueOrDefault() ?? 0,
                LastPlayedDate = lastPlayedDate
            };
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

        private static string PerVersionKey(long userId, string itemId, string mediaSourceId)
        {
            return "pv|" + userId + "|" + itemId + "|" + mediaSourceId;
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

                    // Old format has no ItemId column. MediaSourceItemId already carries the same
                    // id, so promote it in memory when the feature is on; the read/write paths
                    // persist the new shape. When the option is OFF the stored shape is untouched.
                    if (IsPerVersionResumeEnabled() && string.IsNullOrEmpty(record.ItemId) &&
                        !string.IsNullOrEmpty(record.MediaSourceItemId))
                    {
                        record.ItemId = record.MediaSourceItemId;
                        _migratedOnLoad = true;
                    }

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

            /// <summary>Concrete item id of the version that produced this record.</summary>
            public string ItemId { get; set; }

            public string MediaSourceId { get; set; }

            /// <summary>Kept for old records; mirrors <see cref="ItemId"/> on write.</summary>
            public string MediaSourceItemId { get; set; }

            public string VersionKey { get; set; }
            public string VersionSignature { get; set; }
            public long PositionTicks { get; set; }
            public bool Played { get; set; }

            /// <summary>Runtime of the version, used for the one-time cross-version conversion.</summary>
            public long RunTimeTicks { get; set; }

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
