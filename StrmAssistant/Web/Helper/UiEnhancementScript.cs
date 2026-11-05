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

            return "\n" + GetScript(multiVersionSort) + "\n";
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
    }
}
