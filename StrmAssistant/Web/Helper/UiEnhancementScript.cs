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

            return "\n" + GetScript(multiVersionSort) + "\n" + GetPlaybackScript() + "\n";
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
    }
}
