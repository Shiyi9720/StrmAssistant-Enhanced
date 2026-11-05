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

            var episodeStatsFormat = (Resources.EpisodeStats_Total_In_Library ??
                                      "Total Episodes {0} / In Library {1}")
                .Replace("\\", "\\\\").Replace("'", "\\'");

            return "\n" + GetStyleScript() + "\n" + GetScript(multiVersionSort) + "\n" + GetPlaybackScript() +
                   "\n" + GetLibraryBadgeScript() + "\n" + GetEpisodeStatsScript(episodeStatsFormat) + "\n";
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
                    Limit: 0,
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
    }
}
