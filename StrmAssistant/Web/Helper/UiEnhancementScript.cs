using StrmAssistant.Options;
using StrmAssistant.Properties;

namespace StrmAssistant.Web.Helper
{
    /// <summary>
    /// Builds the client-side script that is appended to the patched shortcuts.js module.
    /// It is generated per request so that option changes take effect without a server restart.
    /// </summary>
    internal static class UiEnhancementScript
    {
        public static string Get()
        {
            var options = Plugin.Instance?.GetPluginOptions();

            var multiVersionSort = options?.GeneralOptions.MultiVersionSort ?? true;
            var reverseSeasonEpisodeOrder =
                options?.ExperienceEnhanceOptions.ReverseSeasonEpisodeOrder ?? false;
            var episodeDisplayStyle = options?.ExperienceEnhanceOptions.EpisodeDisplayStyle ??
                                      ExperienceEnhanceOptions.EpisodeDisplayStyleOption.Default;

            var episodeStatsFormat = (Resources.EpisodeStats_Total_In_Library ??
                                      "Total Episodes {0} / In Library {1}")
                .Replace("\\", "\\\\").Replace("'", "\\'");

            return "\n" + GetStyleScript() + "\n" + GetScript(multiVersionSort) + "\n" + GetPlaybackScript() +
                   "\n" + GetLibraryBadgeScript() + "\n" + GetEpisodeStatsScript(episodeStatsFormat) + "\n" +
                   GetCollectionMembershipScript() + "\n" +
                   GetDashboardTasksScript() + "\n" + GetEpisodeDisplayStyleScript(episodeDisplayStyle) + "\n" +
                   GetReverseOrderScript(reverseSeasonEpisodeOrder) + "\n";
        }

        private static string GetStyleScript()
        {
            return @"
(function () {
    'use strict';

    if (document.getElementById('strmAssistantStyles')) return;

    var style = document.createElement('style');
    style.id = 'strmAssistantStyles';
    style.textContent =
        '.strmAssistantCountBadge{position:absolute;top:.35em;right:.35em;z-index:4;' +
        'background:rgba(0,0,0,.72);color:#fff;border-radius:.9em;padding:.1em .5em;' +
        'font-size:.75em;line-height:1.5;font-weight:600;pointer-events:none;' +
        'box-shadow:0 1px 3px rgba(0,0,0,.4)}' +
        '.strmAssistantEpisodeStats{display:inline-block;margin:.35em 0;padding:.25em .6em;' +
        'border-radius:.3em;background:rgba(0,0,0,.35);font-size:.95em}';
    (document.head || document.documentElement).appendChild(style);
})();
";
        }

        /// <summary>
        /// Forces one episode title style everywhere in the web client by wrapping the shared
        /// BaseItemController.getDisplayName method. The web client builds every card and list
        /// title through that method, so patching it keeps all views consistent instead of
        /// rewriting rendered text per view.
        /// </summary>
        private static string GetEpisodeDisplayStyleScript(ExperienceEnhanceOptions.EpisodeDisplayStyleOption style)
        {
            if (style == ExperienceEnhanceOptions.EpisodeDisplayStyleOption.Default) return string.Empty;

            var forceNumber = style == ExperienceEnhanceOptions.EpisodeDisplayStyleOption.SeasonEpisodeNumber ? "true" : "false";

            return @"
(function () {
    'use strict';

    var forceNumber = @@FORCE_NUMBER@@;
    var attempts = 0;

    function patch(modules) {
        var BaseItemController = modules && modules[0];
        var prototype = BaseItemController && BaseItemController.prototype;

        if (!prototype || typeof prototype.getDisplayName !== 'function') return false;
        if (prototype.getDisplayName.__strmAssistant) return true;

        var original = prototype.getDisplayName;

        var patched = function (item, options) {
            if (!item || item.Type !== 'Episode') {
                return original.apply(this, arguments);
            }

            var opts = options ? Object.assign({}, options) : {};

            if (forceNumber) {
                opts.includeIndexNumber = true;
                opts.includeParentInfo = true;
            } else {
                opts.includeIndexNumber = false;
            }

            return original.call(this, item, opts);
        };

        patched.__strmAssistant = true;
        prototype.getDisplayName = patched;

        return true;
    }

    var timer = setInterval(function () {
        attempts++;

        try {
            require(['BaseItemController']).then(function (modules) {
                if (patch(modules)) clearInterval(timer);
            }, function () { });
        } catch (e) { }

        if (attempts > 60) clearInterval(timer);
    }, 1000);
})();
".Replace("@@FORCE_NUMBER@@", forceNumber);
        }

        /// <summary>
        /// Reverses the rendered order of seasons on a series detail page and of episodes on a
        /// season detail page. The server-side order is used as the reference so that list views
        /// re-rendered by Emby are reordered again without relying on DOM index labels.
        /// </summary>
        private static string GetReverseOrderScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    var cache = {};
    var loading = {};
    var apiClient = null;
    var scheduled = false;

    function withApiClient(callback) {
        if (apiClient) {
            callback(apiClient);
            return;
        }
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var client = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (client) {
                    apiClient = client;
                    callback(client);
                }
            }, function () { });
        } catch (e) { }
    }

    function currentItemId() {
        var hash = location.hash || '';
        if (hash.indexOf('item?') === -1 && hash.indexOf('/item') === -1) return null;
        var match = /[?&]id=([^&]+)/.exec(hash);
        return match ? match[1] : null;
    }

    function load(id) {
        if (cache[id] || loading[id]) return;

        withApiClient(function (client) {
            if (cache[id] || loading[id]) return;

            loading[id] = true;

            var userId = client.getCurrentUserId();

            client.getItem(userId, id).then(function (item) {
                if (!item || (item.Type !== 'Season' && item.Type !== 'Series')) {
                    loading[id] = false;
                    return;
                }

                var isSeason = item.Type === 'Season';

                client.getItems(userId, {
                    ParentId: id,
                    Recursive: false,
                    EnableTotalRecordCount: false,
                    IncludeItemTypes: isSeason ? 'Episode' : 'Season',
                    SortBy: 'IndexNumber',
                    SortOrder: 'Ascending',
                    Limit: 10000
                }).then(function (result) {
                    var rank = {};
                    var items = (result && result.Items) || [];

                    for (var i = 0; i < items.length; i++) {
                        rank[items[i].Id] = i;
                    }

                    cache[id] = { kind: isSeason ? 'Episode' : 'Season', rank: rank };
                    loading[id] = false;
                    apply();
                }, function () {
                    loading[id] = false;
                });
            }, function () {
                loading[id] = false;
            });
        });
    }

    function apply() {
        var id = currentItemId();
        if (!id) return;

        var state = cache[id];
        if (!state) {
            load(id);
            return;
        }

        var selector = state.kind === 'Episode' ? '.listItem[data-id]' : '.card[data-id]';
        var nodes = document.querySelectorAll(selector);
        var groups = [];

        function groupFor(parent) {
            for (var i = 0; i < groups.length; i++) {
                if (groups[i].parent === parent) return groups[i];
            }
            var group = { parent: parent, nodes: [] };
            groups.push(group);
            return group;
        }

        for (var i = 0; i < nodes.length; i++) {
            var node = nodes[i];
            var nodeId = node.getAttribute('data-id');

            if (!nodeId || !Object.prototype.hasOwnProperty.call(state.rank, nodeId)) continue;

            var parent = node.parentElement;
            if (parent) groupFor(parent).nodes.push(node);
        }

        for (var g = 0; g < groups.length; g++) {
            var list = groups[g].nodes;
            if (list.length < 2) continue;

            var ordered = list.slice().sort(function (a, b) {
                return state.rank[b.getAttribute('data-id')] - state.rank[a.getAttribute('data-id')];
            });

            var changed = false;
            for (var k = 0; k < list.length; k++) {
                if (list[k] !== ordered[k]) {
                    changed = true;
                    break;
                }
            }

            if (!changed) continue;

            for (var j = 0; j < ordered.length; j++) {
                groups[g].parent.appendChild(ordered[j]);
            }
        }
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            apply();
        }, 300);
    }

    function start() {
        if (!document.body) {
            setTimeout(start, 200);
            return;
        }

        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        window.addEventListener('hashchange', schedule);
        schedule();
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        withApiClient(function () {
            clearInterval(timer);
            schedule();
        });
        if (attempts > 60) clearInterval(timer);
    }, 2000);

    start();
})();
";
        }

        private static string GetScript(bool multiVersionSort)
        {
            return @"
(function () {
    'use strict';

    var enableMultiVersionSort = " + (multiVersionSort ? "true" : "false") + @";

    if (enableMultiVersionSort) {
        var sortPatched = false;

        function findVideoStream(source) {
            var streams = (source && source.MediaStreams) || [];
            for (var i = 0; i < streams.length; i++) {
                if (streams[i] && streams[i].Type === 'Video') return streams[i];
            }
            return null;
        }

        function qualityScore(source) {
            if (!source) return { height: 0, width: 0, codec: 0, size: 0 };

            var video = findVideoStream(source);
            var codec = String((video && video.Codec) || source.VideoCodec || '').toLowerCase();
            var codecRank = (codec === 'hevc' || codec === 'h265' || codec === 'av1' || codec === 'vp9') ? 3 :
                (codec === 'h264' || codec === 'avc') ? 2 : (codec ? 1 : 0);

            return {
                height: (video && video.Height) || 0,
                width: (video && video.Width) || 0,
                codec: codecRank,
                size: source.Size || 0
            };
        }

        function compareSources(a, b) {
            var x = qualityScore(a);
            var y = qualityScore(b);
            return (y.height - x.height) || (y.width - x.width) || (y.codec - x.codec) || (y.size - x.size);
        }

        function sortMediaSources(target) {
            if (!target || typeof target !== 'object') return;
            if (Array.isArray(target.MediaSources) && target.MediaSources.length > 1) {
                target.MediaSources.sort(compareSources);
            }
            if (target.Item && target.Item !== target && Array.isArray(target.Item.MediaSources) &&
                target.Item.MediaSources.length > 1) {
                target.Item.MediaSources.sort(compareSources);
            }
        }

        function patchApiClient(connectionManager) {
            if (sortPatched) return true;

            var apiClient = connectionManager && connectionManager.currentApiClient &&
                connectionManager.currentApiClient();
            if (!apiClient) return false;

            var proto = Object.getPrototypeOf(apiClient);
            if (!proto || typeof proto.getJSON !== 'function') return false;

            if (proto.__strmAssistantVersionSortPatched) {
                sortPatched = true;
                return true;
            }

            var originalGetJSON = proto.getJSON;
            proto.getJSON = function () {
                var result = originalGetJSON.apply(this, arguments);
                if (result && typeof result.then === 'function') {
                    return result.then(function (data) {
                        try { sortMediaSources(data); } catch (e) { }
                        return data;
                    });
                }
                try { sortMediaSources(result); } catch (e) { }
                return result;
            };
            proto.__strmAssistantVersionSortPatched = true;
            sortPatched = true;
            return true;
        }

        var sortAttempts = 0;
        var sortTimer = setInterval(function () {
            sortAttempts++;
            try {
                require(['connectionManager']).then(function (modules) {
                    var connectionManager = modules && modules[0];
                    if (connectionManager && patchApiClient(connectionManager)) {
                        clearInterval(sortTimer);
                    }
                }, function () { });
            } catch (e) { }
            if (sortAttempts > 60) clearInterval(sortTimer);
        }, 2000);
    }
})();
";
        }

        /// <summary>
        /// Per-version playback progress. Emby's UserData keeps a single PlaybackPositionTicks per
        /// item key shared by every version, so the wrapper asks the plugin for the position that
        /// belongs to the media source being played and passes it to the playback manager.
        /// </summary>
        private static string GetPlaybackScript()
        {
            return @"
(function () {
    'use strict';

    var patched = false;

    function findSource(item, mediaSourceId) {
        var sources = (item && item.MediaSources) || [];
        if (mediaSourceId) {
            for (var i = 0; i < sources.length; i++) {
                if (sources[i].Id === mediaSourceId) return sources[i];
            }
        }
        return sources.length ? sources[0] : null;
    }

    function findEntry(response, source) {
        var items = (response && response.Items) || [];
        for (var i = 0; i < items.length; i++) {
            var entry = items[i];
            if (!entry) continue;
            if ((source.Id && entry.MediaSourceId === source.Id) ||
                (source.ItemId && entry.MediaSourceId === source.ItemId) ||
                (source.ItemId && entry.ItemId === source.ItemId)) {
                return entry;
            }
        }
        return null;
    }

    function patchPlaybackManager(playbackManager, connectionManager) {
        if (patched) return true;
        if (!playbackManager || typeof playbackManager.play !== 'function') return false;
        if (playbackManager.__strmAssistantVersionPlaybackPatched) {
            patched = true;
            return true;
        }

        var originalPlay = playbackManager.play;

        playbackManager.play = function (options) {
            try {
                if (options && options.items && options.items.length === 1 && options.startPositionTicks !== 0) {
                    var item = options.items[0];
                    var sources = (item && item.MediaSources) || [];
                    if (sources.length > 1) {
                        var source = findSource(item, options.mediaSourceId);
                        var apiClient = connectionManager.currentApiClient && connectionManager.currentApiClient();
                        if (source && apiClient) {
                            var self = this;
                            var args = arguments;
                            var url = apiClient.getUrl('StrmAssistant/Items/' + item.Id + '/VersionPlayback');
                            return apiClient.getJSON(url).then(function (response) {
                                var entry = findEntry(response, source);
                                var hasAnyProgress = false;
                                var items = (response && response.Items) || [];
                                for (var i = 0; i < items.length; i++) {
                                    if (items[i] && items[i].PositionTicks > 0) hasAnyProgress = true;
                                }
                                if (entry || hasAnyProgress) {
                                    var newOptions = {};
                                    for (var name in options) {
                                        if (Object.prototype.hasOwnProperty.call(options, name)) {
                                            newOptions[name] = options[name];
                                        }
                                    }
                                    if (entry && !entry.Played && entry.PositionTicks > 0) {
                                        var runtime = item.RunTimeTicks || source.RunTimeTicks || 0;
                                        newOptions.startPositionTicks = (!runtime || entry.PositionTicks < runtime * 0.95)
                                            ? entry.PositionTicks : 0;
                                    } else {
                                        newOptions.startPositionTicks = 0;
                                    }
                                    return originalPlay.apply(self, [newOptions]);
                                }
                                return originalPlay.apply(self, args);
                            }, function () {
                                return originalPlay.apply(self, args);
                            });
                        }
                    }
                }
            } catch (e) { }
            return originalPlay.apply(this, arguments);
        };
        playbackManager.__strmAssistantVersionPlaybackPatched = true;
        patched = true;
        return true;
    }

    var playbackAttempts = 0;
    var playbackTimer = setInterval(function () {
        playbackAttempts++;
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                if (!connectionManager) return;
                try {
                    require(['playbackManager']).then(function (playbackManagers) {
                        if (patchPlaybackManager(playbackManagers && playbackManagers[0], connectionManager)) {
                            clearInterval(playbackTimer);
                        }
                    }, function () { });
                } catch (e) {
                    Emby.importModule('./modules/playback/playbackmanager.js').then(function (module) {
                        var playbackManager = module && (module.default || module);
                        if (patchPlaybackManager(playbackManager, connectionManager)) {
                            clearInterval(playbackTimer);
                        }
                    }, function () { });
                }
            }, function () { });
        } catch (e) { }
        if (playbackAttempts > 60) clearInterval(playbackTimer);
    }, 3000);
})();
";
        }

        /// <summary>
        /// Item count badges on the home screen media library cards. Counts are fetched per library
        /// view and matched to the rendered cards by the view item id.
        /// </summary>
        private static string GetLibraryBadgeScript()
        {
            return @"
(function () {
    'use strict';

    var badgeClass = 'strmAssistantCountBadge';

    var typeMap = {
        movies: 'Movie',
        tvshows: 'Series',
        music: 'MusicAlbum',
        musicvideos: 'MusicVideo',
        homevideos: 'Video',
        books: 'Book',
        boxsets: 'BoxSet',
        photos: 'Photo'
    };

    var counts = {};
    var apiClient = null;
    var loading = false;
    var loaded = false;

    function withApiClient(callback) {
        if (apiClient) {
            callback(apiClient);
            return;
        }
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var client = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (client) {
                    apiClient = client;
                    callback(client);
                }
            }, function () { });
        } catch (e) { }
    }

    function countForCard(card) {
        var id = card.getAttribute('data-id');
        if (id && Object.prototype.hasOwnProperty.call(counts, id)) return counts[id];

        var link = card.querySelector('a[href]');
        var href = link ? link.getAttribute('href') || '' : '';
        if (href) {
            for (var key in counts) {
                if (Object.prototype.hasOwnProperty.call(counts, key) && href.indexOf(key) !== -1) {
                    return counts[key];
                }
            }
        }
        return null;
    }

    function applyBadge(card) {
        if (!card || card.querySelector('.' + badgeClass)) return;

        var count = countForCard(card);
        if (count === null) return;

        try {
            if (getComputedStyle(card).position === 'static') card.style.position = 'relative';
        } catch (e) { }

        var badge = document.createElement('span');
        badge.className = badgeClass;
        badge.textContent = count.toLocaleString();
        card.appendChild(badge);
    }

    function applyAll() {
        var cards = document.querySelectorAll('.card');
        for (var i = 0; i < cards.length; i++) {
            applyBadge(cards[i]);
        }
    }

    function fetchCounts(client) {
        if (loaded || loading) return;

        loading = true;

        var userId = client.getCurrentUserId();

        client.getJSON(client.getUrl('Users/' + userId + '/Views')).then(function (views) {
            var items = (views && views.Items) || [];
            var pending = [];

            for (var i = 0; i < items.length; i++) {
                var view = items[i];
                var type = view && typeMap[String(view.CollectionType || '').toLowerCase()];
                if (type && view.Id) pending.push({ id: view.Id, type: type });
            }

            if (!pending.length) {
                loading = false;
                loaded = true;
                return;
            }

            var remaining = pending.length;

            pending.forEach(function (entry) {
                client.getItems(userId, {
                    ParentId: entry.id,
                    Recursive: true,
                    Limit: 1,
                    IncludeItemTypes: entry.type
                }).then(function (result) {
                    if (result && typeof result.TotalRecordCount === 'number') {
                        counts[entry.id] = result.TotalRecordCount;
                    }
                }, function () { }).then(function () {
                    remaining--;
                    if (remaining <= 0) {
                        loading = false;
                        loaded = true;
                        applyAll();
                    }
                });
            });
        }, function () {
            loading = false;
        });
    }

    var scheduled = false;

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            applyAll();
        }, 400);
    }

    var badgeAttempts = 0;
    var badgeTimer = setInterval(function () {
        badgeAttempts++;
        if (loaded || badgeAttempts > 60) {
            clearInterval(badgeTimer);
            return;
        }
        withApiClient(fetchCounts);
    }, 3000);

    function startObserving() {
        if (!document.body) {
            setTimeout(startObserving, 200);
            return;
        }

        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    startObserving();
})();
";
        }

        /// <summary>
        /// "Total episodes / in library" line on the series detail page. Emby keeps no external
        /// episode total, so the numbers come from the episodes known to the server (including
        /// virtual episodes created by the missing episodes feature).
        /// </summary>
        private static string GetEpisodeStatsScript(string format)
        {
            return @"
(function () {
    'use strict';

    var statsClass = 'strmAssistantEpisodeStats';
    var itemFormat = '@@FORMAT@@';
    var cache = {};
    var apiClient = null;
    var pendingId = null;
    var scheduled = false;

    function withApiClient(callback) {
        if (apiClient) {
            callback(apiClient);
            return;
        }
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var client = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (client) {
                    apiClient = client;
                    callback(client);
                }
            }, function () { });
        } catch (e) { }
    }

    function currentItemId() {
        var hash = location.hash || '';
        if (hash.indexOf('item?') === -1 && hash.indexOf('/item') === -1) return null;
        var match = /[?&]id=([^&]+)/.exec(hash);
        return match ? match[1] : null;
    }

    function removeStats() {
        var nodes = document.querySelectorAll('.' + statsClass);
        for (var i = nodes.length - 1; i >= 0; i--) {
            if (nodes[i].parentNode) nodes[i].parentNode.removeChild(nodes[i]);
        }
    }

    function buildText(stats) {
        return String(itemFormat)
            .replace('{0}', stats.Total)
            .replace('{1}', stats.InLibrary);
    }

    function render(id) {
        var stats = cache[id];
        if (!stats || !stats.IsSeries || !stats.Total) {
            removeStats();
            return;
        }

        var text = buildText(stats);
        var existing = document.querySelectorAll('.' + statsClass);

        if (existing.length === 1 && existing[0].textContent === text && document.contains(existing[0])) return;

        removeStats();

        var page = document.querySelector('.itemDetailPage');
        if (!page) return;

        var element = document.createElement('div');
        element.className = statsClass;
        element.textContent = text;

        var anchor = page.querySelector('.itemMiscInfo-primary');
        if (anchor && anchor.parentNode) {
            anchor.parentNode.insertBefore(element, anchor.nextSibling);
            return;
        }

        var name = page.querySelector('.itemName');
        if (name && name.parentNode) {
            name.parentNode.insertBefore(element, name.nextSibling);
            return;
        }

        var primary = page.querySelector('.detailPagePrimaryContent');
        if (primary) {
            primary.insertBefore(element, primary.firstChild);
            return;
        }

        page.insertBefore(element, page.firstChild);
    }

    function refresh() {
        var id = currentItemId();

        if (!id) {
            removeStats();
            return;
        }

        if (Object.prototype.hasOwnProperty.call(cache, id)) {
            render(id);
            return;
        }

        if (pendingId === id) return;
        pendingId = id;

        withApiClient(function (client) {
            client.getJSON(client.getUrl('StrmAssistant/Items/' + id + '/EpisodeStats')).then(function (stats) {
                if (pendingId === id) pendingId = null;
                cache[id] = stats || {};
                render(id);
            }, function () {
                if (pendingId === id) pendingId = null;
            });
        });
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            refresh();
        }, 300);
    }

    window.addEventListener('hashchange', schedule);

    function startObserving() {
        if (!document.body) {
            setTimeout(startObserving, 200);
            return;
        }

        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    startObserving();
})();
".Replace("@@FORMAT@@", format);
        }

        /// <summary>
        /// Fills the "included in" section of a series detail page with the collections the series
        /// belongs to. The server endpoint also reports collections that contain one of the
        /// series' seasons, which Emby's own query does not return, so the section's fetch
        /// function is replaced with the combined result.
        /// </summary>
        private static string GetCollectionMembershipScript()
        {
            return @"
(function () {
    'use strict';

    var results = {};
    var pending = {};
    var apiClient = null;
    var scheduled = false;

    function withApiClient(callback) {
        if (apiClient) {
            callback(apiClient);
            return;
        }
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var client = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (client) {
                    apiClient = client;
                    callback(client);
                }
            }, function () { });
        } catch (e) { }
    }

    function currentItemId() {
        var hash = location.hash || '';
        if (hash.indexOf('item?') === -1 && hash.indexOf('/item') === -1) return null;
        var match = /[?&]id=([^&]+)/.exec(hash);
        return match ? match[1] : null;
    }

    function ensure(id) {
        var container = document.querySelector('.appearsOnListsItemsContainer');

        // The section is only prepared by Emby for item types that can be added to a collection.
        if (!container || !container.parentContainer) return;

        if (container.__strmCollectionItemId === id &&
            container.fetchData && container.fetchData.__strmAssistant) return;

        var result = results[id];

        if (!result) {
            if (pending[id]) return;

            pending[id] = true;

            withApiClient(function (client) {
                client.getJSON(client.getUrl('StrmAssistant/Items/' + id + '/Collections')).then(function (data) {
                    pending[id] = false;
                    results[id] = data && data.Items ? data : { Items: [], TotalRecordCount: 0 };
                    apply();
                }, function () {
                    pending[id] = false;
                });
            });

            return;
        }

        if (!result.Items || !result.Items.length) return;

        var fetchData = function () {
            return Promise.resolve(result);
        };

        fetchData.__strmAssistant = true;

        container.fetchData = fetchData;
        container.__strmCollectionItemId = id;

        try {
            container.refreshItems();
        } catch (e) { }
    }

    function apply() {
        var id = currentItemId();
        if (!id) return;
        ensure(id);
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            apply();
        }, 400);
    }

    window.addEventListener('hashchange', schedule);

    function startObserving() {
        if (!document.body) {
            setTimeout(startObserving, 200);
            return;
        }

        new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true });
        schedule();
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        withApiClient(function () {
            schedule();
        });
        if (attempts > 60) clearInterval(timer);
    }, 2000);

    startObserving();
})();
";
        }

        /// <summary>
        /// Floating panel on the dashboard that lists running plugin tasks with their progress and
        /// offers a button to rebuild what search returns (queues the built-in library scan).
        /// </summary>
        private static string GetDashboardTasksScript()
        {
            return @"
(function () {
    'use strict';

    var labels = {
        title: '@@TITLE@@',
        none: '@@NONE@@',
        rebuild: '@@REBUILD@@',
        queued: '@@QUEUED@@',
        failed: '@@FAILED@@'
    };
    var panel = null;
    var list = null;
    var timer = null;
    var apiClient = null;

    function withApiClient(callback) {
        if (apiClient) {
            callback(apiClient);
            return;
        }
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var client = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (client) {
                    apiClient = client;
                    callback(client);
                }
            }, function () { });
        } catch (e) { }
    }

    function isDashboard() {
        return (location.hash || '').toLowerCase().indexOf('#/dashboard') === 0;
    }

    function ensurePanel() {
        if (panel) return panel;

        panel = document.createElement('div');
        panel.id = 'strmAssistantTasksPanel';
        panel.style.cssText = 'position:fixed;right:1.5em;bottom:1.5em;z-index:1000;width:20em;' +
            'max-width:calc(100% - 3em);background:rgba(16,16,16,.92);color:#fff;border-radius:.5em;' +
            'padding:.75em 1em;font-size:.85em;box-shadow:0 4px 16px rgba(0,0,0,.5)';

        var header = document.createElement('div');
        header.textContent = labels.title;
        header.style.cssText = 'font-weight:600;margin-bottom:.5em';

        list = document.createElement('div');

        var button = document.createElement('button');
        button.type = 'button';
        button.textContent = labels.rebuild;
        button.style.cssText = 'margin-top:.6em;width:100%;padding:.4em;border:0;border-radius:.3em;' +
            'cursor:pointer;background:#52b54b;color:#fff';

        button.addEventListener('click', function () {
            button.disabled = true;

            withApiClient(function (client) {
                client.ajax({
                    type: 'POST',
                    url: client.getUrl('StrmAssistant/Search/Rebuild'),
                    dataType: 'json'
                }).then(function (result) {
                    button.textContent = result && result.Queued ? labels.queued : labels.failed;
                }, function () {
                    button.textContent = labels.failed;
                }).then(function () {
                    setTimeout(function () {
                        button.textContent = labels.rebuild;
                        button.disabled = false;
                    }, 4000);
                });
            });
        });

        panel.appendChild(header);
        panel.appendChild(list);
        panel.appendChild(button);
        document.body.appendChild(panel);

        return panel;
    }

    function render(tasks) {
        ensurePanel();
        list.innerHTML = '';

        var active = (tasks || []).filter(function (task) { return task.IsRunning; });

        if (!active.length) {
            var empty = document.createElement('div');
            empty.textContent = labels.none;
            empty.style.opacity = '.7';
            list.appendChild(empty);
            return;
        }

        active.forEach(function (task) {
            var row = document.createElement('div');
            row.style.cssText = 'display:flex;justify-content:space-between;gap:.5em;padding:.15em 0';

            var name = document.createElement('span');
            name.textContent = task.Name;

            var value = document.createElement('span');
            value.style.opacity = '.8';
            value.textContent = task.Progress === null || task.Progress === undefined
                ? task.State
                : Math.round(task.Progress) + '%';

            row.appendChild(name);
            row.appendChild(value);
            list.appendChild(row);
        });
    }

    function refresh() {
        if (!isDashboard()) {
            if (panel) panel.style.display = 'none';
            return;
        }

        ensurePanel().style.display = '';

        withApiClient(function (client) {
            client.getJSON(client.getUrl('StrmAssistant/Tasks')).then(function (result) {
                render(result && result.Tasks);
            }, function () {
                render([]);
            });
        });
    }

    function start() {
        if (timer) return;
        timer = setInterval(refresh, 5000);
        refresh();
    }

    window.addEventListener('hashchange', refresh);
    document.addEventListener('DOMContentLoaded', start);

    if (document.readyState !== 'loading') start();
})();
"
                .Replace("@@TITLE@@", JsString("DashboardTasks_Title_Active_Tasks", "Active Tasks"))
                .Replace("@@NONE@@", JsString("DashboardTasks_NoActiveTasks_No_active_tasks",
                    "No active tasks"))
                .Replace("@@REBUILD@@", JsString("DashboardTasks_RebuildSearchIndex_Rebuild_Search_Index",
                    "Rebuild Search Index"))
                .Replace("@@QUEUED@@", JsString("DashboardTasks_RebuildQueued_Search_index_rebuild_queued",
                    "Search index rebuild queued"))
                .Replace("@@FAILED@@", JsString("DashboardTasks_RebuildFailed_Failed_to_queue_search_index_rebuild",
                    "Failed to queue search index rebuild"));
        }

        private static string JsString(string resourceName, string fallback)
        {
            var value = Resources.ResourceManager.GetString(resourceName);

            return (string.IsNullOrEmpty(value) ? fallback : value)
                .Replace("\\", "\\\\").Replace("'", "\\'");
        }
    }
}
