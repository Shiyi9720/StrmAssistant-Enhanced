using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Keeps the favorite flag of a movie separate for every concrete item. Emby stores user data
    /// (including <see cref="UserItemData.IsFavorite"/>) under the provider based user data key of
    /// a video, so all copies of the same movie share one favorite. This store records the
    /// favorite per item and reuses the identity helpers of <see cref="VersionPlaybackApi"/>
    /// (provider/name+year group and the source id / file name stem / version signature tiers) to
    /// decide whether an existing record belongs to the item being queried.
    ///
    /// A record is only allowed to take an item over when it belongs to the same concrete item or
    /// to a source of the same library. A different library is deliberately treated as too low
    /// confidence: the same file name and signature are common for duplicate copies, and matching
    /// them would recreate the very cross-library leak this store exists to prevent.
    /// </summary>
    public class VersionFavoriteApi
    {
        private const string StoreFileName = "StrmAssistant.VersionFavorite.json";

        private ILogger _logger => Plugin.Instance?.Logger;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly string _storePath;
        private readonly object _sync = new object();
        private readonly Dictionary<string, VersionFavoriteRecord> _records =
            new Dictionary<string, VersionFavoriteRecord>(StringComparer.OrdinalIgnoreCase);

        public VersionFavoriteApi(IJsonSerializer jsonSerializer)
        {
            _jsonSerializer = jsonSerializer;
            _storePath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginConfigurationsPath, StoreFileName);

            Load();
        }

        internal static bool IsEnabled
        {
            get
            {
                try
                {
                    return Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions
                        ?.VersionFavoriteOptions?.EnableCrossLibraryFavoriteSeparation ?? false;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Returns the favorite state for one concrete item, or null when the plugin has no
        /// opinion (the item has never been part of a managed group, so Emby's shared value is
        /// left untouched). When the movie group is managed but this item has no record of its
        /// own, false is returned so a favorite in another library does not show up here.
        /// </summary>
        public bool? GetFavorite(long userId, BaseItem item, long libraryId)
        {
            if (!(item is Movie)) return null;

            var itemKey = VersionPlaybackApi.GetItemKey(item);
            var nameKey = VersionPlaybackApi.GetNameKey(item);
            if (string.IsNullOrEmpty(itemKey) && string.IsNullOrEmpty(nameKey)) return null;

            List<VersionFavoriteRecord> groupRecords;

            lock (_sync)
            {
                groupRecords = _records.Values
                    .Where(r => r.UserId == userId && MatchesGroup(r, itemKey, nameKey, item.InternalId))
                    .ToList();

                if (groupRecords.Count == 0) return null;

                var exact = groupRecords.FirstOrDefault(r => r.ItemId == item.InternalId);
                if (exact != null) return exact.IsFavorite;
            }

            var versionMatch = FindSameLibraryVersionMatch(groupRecords, item, libraryId);
            if (versionMatch != null) return versionMatch.IsFavorite;

            // The group is managed by the plugin, but this concrete item is not favorite there.
            return false;
        }

        /// <summary>
        /// Records the favorite for one item and returns the value Emby's shared user data should
        /// carry (true while any item in the same movie group is favorite).
        /// </summary>
        public bool SetFavorite(long userId, BaseItem item, bool isFavorite, long libraryId)
        {
            var itemKey = VersionPlaybackApi.GetItemKey(item);
            var nameKey = VersionPlaybackApi.GetNameKey(item);
            if (string.IsNullOrEmpty(itemKey) && string.IsNullOrEmpty(nameKey)) return isFavorite;

            var now = DateTime.UtcNow;

            lock (_sync)
            {
                var key = RecordKey(userId, item.InternalId);

                if (!_records.TryGetValue(key, out var record))
                {
                    record = new VersionFavoriteRecord
                    {
                        UserId = userId,
                        ItemId = item.InternalId
                    };
                    _records[key] = record;
                }

                record.ItemKey = itemKey ?? record.ItemKey;
                record.NameKey = nameKey ?? record.NameKey;
                record.LibraryId = libraryId;
                record.IsFavorite = isFavorite;
                record.LastModifiedDate = now;

                var source = VersionPlaybackApi.GetCurrentMediaSources(item)?.FirstOrDefault();
                if (source != null)
                {
                    if (!string.IsNullOrEmpty(source.Id)) record.MediaSourceId = source.Id;
                    if (!string.IsNullOrEmpty(source.ItemId)) record.MediaSourceItemId = source.ItemId;

                    var versionKey = VersionPlaybackApi.GetVersionKey(source);
                    if (!string.IsNullOrEmpty(versionKey)) record.VersionKey = versionKey;

                    var signature = VersionPlaybackApi.GetVersionSignature(source);
                    if (!string.IsNullOrEmpty(signature)) record.VersionSignature = signature;
                }

                var sharedFavorite = _records.Values.Any(r =>
                    r.UserId == userId && r.IsFavorite && MatchesGroup(r, itemKey, nameKey, item.InternalId));

                PersistLocked();

                return sharedFavorite;
            }
        }

        private static VersionFavoriteRecord FindSameLibraryVersionMatch(
            List<VersionFavoriteRecord> records, BaseItem item, long libraryId)
        {
            if (libraryId == 0) return null;

            var candidates = records.Where(r => r.LibraryId == libraryId).ToList();
            if (candidates.Count == 0) return null;

            var sources = VersionPlaybackApi.GetCurrentMediaSources(item);
            if (sources is null || sources.Count == 0) return null;

            foreach (var source in sources)
            {
                if (source is null) continue;

                var match = candidates.FirstOrDefault(r => MatchesSourceIds(r, source)) ??
                            candidates.FirstOrDefault(r => VersionPlaybackApi.MatchesKey(
                                r.VersionKey, VersionPlaybackApi.GetVersionKey(source))) ??
                            candidates.FirstOrDefault(r => VersionPlaybackApi.MatchesKey(
                                r.VersionSignature, VersionPlaybackApi.GetVersionSignature(source)));

                if (match != null) return match;
            }

            return null;
        }

        private static bool MatchesSourceIds(VersionFavoriteRecord record, MediaSourceInfo source)
        {
            return VersionPlaybackApi.MatchesKey(record.MediaSourceId, source.Id) ||
                   VersionPlaybackApi.MatchesKey(record.MediaSourceItemId, source.ItemId);
        }

        private static bool MatchesGroup(VersionFavoriteRecord record, string itemKey, string nameKey,
            long itemId)
        {
            return record.ItemId == itemId ||
                   VersionPlaybackApi.MatchesKey(record.ItemKey, itemKey) ||
                   VersionPlaybackApi.MatchesKey(record.NameKey, nameKey);
        }

        private static string RecordKey(long userId, long itemId)
        {
            return userId + "|" + itemId;
        }

        private void PersistLocked()
        {
            try
            {
                var store = new VersionFavoriteStore
                {
                    Records = _records.Values.ToList()
                };

                _jsonSerializer.SerializeToFile(store, _storePath);
            }
            catch (Exception ex)
            {
                _logger?.Debug("VersionFavorite - Persist failed: " + ex.Message);
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_storePath)) return;

                var store = _jsonSerializer.DeserializeFromFile<VersionFavoriteStore>(_storePath);

                if (store?.Records is null) return;

                foreach (var record in store.Records)
                {
                    if (record is null || record.ItemId == 0 ||
                        (string.IsNullOrEmpty(record.ItemKey) && string.IsNullOrEmpty(record.NameKey)))
                    {
                        continue;
                    }

                    _records[RecordKey(record.UserId, record.ItemId)] = record;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug("VersionFavorite - Load failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Moves the plugin's favorite routes in front of Emby's own routes. Emby appends plugin
        /// services to the end of the route bucket, so the identical built-in route would win
        /// otherwise (the same technique is used for the public system info endpoint).
        /// </summary>
        internal static void TryOverrideFavoriteRoutes(IServerApplicationHost applicationHost, ILogger logger)
        {
            try
            {
                var httpServer = applicationHost.Resolve<IHttpServer>();
                var controller = httpServer?.GetType()
                    .GetProperty("ServiceController", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(httpServer);

                if (controller is null)
                {
                    logger?.Warn(
                        "CrossLibraryFavorites: HTTP service controller not found. Emby's favorite routes stay in use.");
                    return;
                }

                var map = controller.GetType()
                    .GetField("RestPathMap", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(controller) as IDictionary;

                if (map is null)
                {
                    logger?.Warn(
                        "CrossLibraryFavorites: HTTP route table not found. Emby's favorite routes stay in use.");
                    return;
                }

                var requestTypes = new[]
                {
                    typeof(Web.Api.MarkVersionFavoriteItem),
                    typeof(Web.Api.UnmarkVersionFavoriteItem)
                };

                var initialized = false;

                foreach (var requestType in requestTypes)
                {
                    if (PrioritizeRestPath(map, requestType)) continue;

                    if (!initialized)
                    {
                        initialized = true;
                        var init = controller.GetType().GetMethod("Init", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { httpServer.GetType(), typeof(Type[]) }, null);
                        init?.Invoke(controller,
                            new object[] { httpServer, new[] { typeof(Web.Service.VersionFavoriteService) } });
                    }

                    if (!PrioritizeRestPath(map, requestType))
                    {
                        logger?.Warn(
                            "CrossLibraryFavorites: unable to take over {0}. Emby's favorite route stays in use.",
                            requestType.Name);
                        return;
                    }
                }

                logger?.Info("CrossLibraryFavorites: favorite routes are handled by Strm Assistant.");
            }
            catch (Exception ex)
            {
                logger?.Warn("CrossLibraryFavorites: unable to override the favorite routes ({0}).", ex.Message);
            }
        }

        private static bool PrioritizeRestPath(IDictionary map, Type requestType)
        {
            var found = false;

            foreach (DictionaryEntry entry in map)
            {
                if (!(entry.Value is IList list) || list.Count == 0) continue;

                var index = -1;
                for (var i = 0; i < list.Count; i++)
                {
                    var requestTypeProperty = list[i]?.GetType()
                        .GetProperty("RequestType", BindingFlags.Public | BindingFlags.Instance);
                    if (requestTypeProperty?.GetValue(list[i]) as Type == requestType)
                    {
                        index = i;
                        break;
                    }
                }

                if (index < 0) continue;

                found = true;

                if (index > 0)
                {
                    var restPath = list[index];
                    list.RemoveAt(index);
                    list.Insert(0, restPath);
                }
            }

            return found;
        }

        private class VersionFavoriteStore
        {
            public List<VersionFavoriteRecord> Records { get; set; }
        }

        private class VersionFavoriteRecord
        {
            public long UserId { get; set; }
            public long ItemId { get; set; }
            public long LibraryId { get; set; }
            public string ItemKey { get; set; }
            public string NameKey { get; set; }
            public string MediaSourceId { get; set; }
            public string MediaSourceItemId { get; set; }
            public string VersionKey { get; set; }
            public string VersionSignature { get; set; }
            public bool IsFavorite { get; set; }
            public DateTime LastModifiedDate { get; set; }
        }
    }

    /// <summary>
    /// Applies the favorite route override after Emby has registered its own routes. The option is
    /// read at startup, so changing it requires a restart (the same contract as the other
    /// server-level options in the plugin).
    /// </summary>
    public sealed class VersionFavoriteEntryPoint : IServerEntryPoint
    {
        private readonly IServerApplicationHost _applicationHost;
        private readonly ILogger _logger;

        public VersionFavoriteEntryPoint(IServerApplicationHost applicationHost, ILogManager logManager)
        {
            _applicationHost = applicationHost;
            _logger = logManager?.GetLogger("VersionFavorite");
        }

        public void Run()
        {
            try
            {
                if (!VersionFavoriteApi.IsEnabled) return;

                VersionFavoriteApi.TryOverrideFavoriteRoutes(_applicationHost, _logger);
            }
            catch (Exception ex)
            {
                _logger?.Warn("CrossLibraryFavorites initialization failed: {0}", ex.Message);
            }
        }

        public void Dispose()
        {
        }
    }
}
