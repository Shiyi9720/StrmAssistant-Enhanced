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
            var multiVersionFriendlyNames =
                options?.GeneralOptions.MultiVersionFriendlyNames ?? true;
            var reverseSeasonEpisodeOrder =
                options?.ExperienceEnhanceOptions.ReverseSeasonEpisodeOrder ?? false;
            var episodeDisplayStyle = options?.ExperienceEnhanceOptions.EpisodeDisplayStyle ??
                                      ExperienceEnhanceOptions.EpisodeDisplayStyleOption.Default;

            var episodeStatsFormat = (Resources.EpisodeStats_Total_In_Library ??
                                      "Total Episodes {0} / In Library {1}")
                .Replace("\\", "\\\\").Replace("'", "\\'");
            var chineseSearch = options?.SearchOptions.EnableChineseSearch ?? true;
            var naturalSort = options?.ExperienceEnhanceOptions.NaturalSort ?? false;
            var enforceLibraryOrder = options?.ExperienceEnhanceOptions.EnforceLibraryOrder ?? false;
            var logNewestFirst = options?.ExperienceEnhanceOptions.LogNewestFirst ?? false;
            var multiSelectActions = options?.ExperienceEnhanceOptions.MultiSelectActions ?? false;
            var doubanRecommendations = options?.MetadataEnhanceOptions.EnableDoubanRecommendations ?? false;
            var doubanTrailers = doubanRecommendations &&
                                 (options?.MetadataEnhanceOptions.EnableDoubanTrailer ?? false);
            var versionFavorites = options?.ExperienceEnhanceOptions.VersionFavoriteOptions
                ?.EnableCrossLibraryFavoriteSeparation ?? false;

            return "\n" + GetStyleScript() + "\n" + GetDoubanRatingScript() + "\n" +
                   GetScript(multiVersionSort) + "\n" +
                   GetVersionNamesScript(multiVersionFriendlyNames) + "\n" + GetPlaybackScript() +
                   "\n" + GetVersionFavoriteScript(versionFavorites) +
                   "\n" + GetLibraryBadgeScript() + "\n" + GetEpisodeStatsScript(episodeStatsFormat) + "\n" +
                   GetCollectionMembershipScript() + "\n" +
                   GetDashboardTasksScript() + "\n" + GetEpisodeDisplayStyleScript(episodeDisplayStyle) + "\n" +
                   GetChineseSearchScript(chineseSearch) + "\n" +
                   GetReverseOrderScript(reverseSeasonEpisodeOrder) + "\n" +
                   GetMultiSelectActionsScript(multiSelectActions) + "\n" +
                   GetNaturalSortScript(naturalSort) + "\n" +
                   GetEnforceLibraryOrderScript(enforceLibraryOrder) + "\n" +
                   GetLogNewestFirstScript(logNewestFirst) + "\n" +
                   GetHiddenBoxSetsScript() + "\n" +
                   GetDoubanRecommendationsScript(doubanRecommendations, doubanTrailers) + "\n" +
                   GetDoubanTrailerScript(doubanTrailers) + "\n" +
                   GetMediaBadgeScript() + "\n";
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
        'border-radius:.3em;background:rgba(0,0,0,.35);font-size:.95em}' +
        '.strmAssistantHiddenBoxSet{display:none!important}' +
        '.strmAssistantVersionList{display:flex;flex-wrap:wrap;gap:.35em;width:100%;margin-top:.45em}' +
        '.strmAssistantVersionItem{display:inline-flex;align-items:center;max-width:100%;padding:.25em .65em;' +
        'border:1px solid rgba(255,255,255,.22);border-radius:999px;background:rgba(255,255,255,.06);' +
        'color:inherit;font:inherit;font-size:.9em;line-height:1.4;cursor:pointer;text-align:left}' +
        '.strmAssistantVersionItem:hover,.strmAssistantVersionItem:focus{background:rgba(255,255,255,.13)}' +
        '.strmAssistantVersionItem.strmAssistantVersionSelected{border-color:#52b54b;' +
        'background:rgba(82,181,75,.2)}' +
        '.strmAssistantVersionName{overflow-wrap:anywhere}' +
        '.strmAssistantVersionToken{font-weight:600}' +
        '.strmAssistantVersionToken-resolution{color:#6fc3ff}' +
        '.strmAssistantVersionToken-source{color:#8fd694}' +
        '.strmAssistantVersionToken-codec{color:#ffd166}' +
        '.strmAssistantVersionToken-audio{color:#d8a7ff}' +
        '.strmAssistantDoubanSection{padding-top:.5em}' +
        '.strmAssistantDoubanMore{margin-left:auto;background:none;border:0;color:inherit;cursor:pointer;' +
        'font-size:.9em;opacity:.75}' +
        '.strmAssistantDoubanItems{display:flex;gap:1em;overflow-x:auto;padding:.5em 0 1em;scrollbar-width:thin}' +
        '.strmAssistantDoubanCard{flex:0 0 10em;width:10em;text-decoration:none;color:inherit}' +
        '.strmAssistantDoubanPoster{position:relative;width:10em;height:15em;border-radius:.35em;' +
        'overflow:hidden;background:rgba(0,0,0,.25);display:flex;align-items:center;justify-content:center}' +
        '.strmAssistantDoubanPoster img{width:100%;height:100%;object-fit:cover}' +
        '.strmAssistantDoubanTitle{margin-top:.4em;font-size:.9em;line-height:1.25;overflow:hidden;' +
        'display:-webkit-box;-webkit-line-clamp:2;-webkit-box-orient:vertical}' +
        '.strmAssistantDoubanMeta{margin-top:.15em;font-size:.8em;opacity:.7}' +
        '.strmAssistantDoubanRating{display:inline-flex;align-items:center;gap:.25em;margin-left:.5em;' +
        'padding:0 .45em;border-radius:.3em;background:rgba(0,181,29,.16);color:#00b51d;' +
        'font-size:.9em;font-weight:600;line-height:1.6}' +
        '.strmAssistantDoubanRatingLabel{font-size:.85em;font-weight:500;opacity:.9}' +
        '.searchSuggestions{display:none!important}';
    (document.head || document.documentElement).appendChild(style);
})();
";
        }

        /// <summary>
        /// Hides collection libraries configured through the library shortcut menu from the home
        /// screen only. The library itself, its detail page and the "more" views stay available.
        /// </summary>
        private static string GetHiddenBoxSetsScript()
        {
            return @"
(function () {
    'use strict';

    if (window.__strmAssistantBoxSetHomePatched) return;
    window.__strmAssistantBoxSetHomePatched = true;

    var hiddenClass = 'strmAssistantHiddenBoxSet';
    var scheduled = false;

    function isHome() {
        var hash = (location.hash || '').toLowerCase();
        return hash === '' || hash === '#/' || hash === '#/home' ||
            hash.indexOf('#/home?') === 0 || hash.indexOf('#/home/') === 0;
    }

    function restore() {
        var nodes = document.querySelectorAll('.' + hiddenClass);
        for (var i = 0; i < nodes.length; i++) {
            nodes[i].classList.remove(hiddenClass);
        }
    }

    function apply() {
        restore();
        if (!isHome()) return;

        var ids = window.strmAssistantHiddenBoxSets || {};

        Object.keys(ids).forEach(function (id) {
            var nodes = document.querySelectorAll('[data-id=""' + id + '""], [data-itemid=""' + id + '""]');
            for (var i = 0; i < nodes.length; i++) {
                var card = nodes[i].classList && nodes[i].classList.contains('card')
                    ? nodes[i]
                    : nodes[i].closest && nodes[i].closest('.card');
                if (card) card.classList.add(hiddenClass);
            }
        });
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            apply();
        }, 250);
    }

    window.addEventListener('hashchange', schedule);
    window.addEventListener('strmAssistantBoxSetVisibilityChanged', schedule);

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
        /// Adds a Douban recommendation channel to the home screen. The endpoint only returns
        /// entries that were matched to a library item; unmatched entries link to search.
        /// </summary>
        private static string GetDoubanRecommendationsScript(bool enabled, bool trailersEnabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    var labels = {
        title: '@@TITLE@@',
        more: '@@MORE@@',
        less: '@@LESS@@',
        open: '@@OPEN@@',
        search: '@@SEARCH@@',
        trailer: '@@TRAILER@@'
    };
    var trailersEnabled = @@TRAILERS_ENABLED@@;
    var apiClient = null;
    var items = [];
    var loading = false;
    var expanded = false;
    var section = null;
    var scheduled = false;
    var lastAttempt = 0;
    var lastLoad = 0;
    var renderVersion = 0;
    var renderedVersion = -1;

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

    function isHome() {
        var hash = (location.hash || '').toLowerCase();
        return hash === '' || hash === '#/' || hash === '#/home' ||
            hash.indexOf('#/home?') === 0 || hash.indexOf('#/home/') === 0;
    }

    function getHost() {
        return document.querySelector('.homeSectionsContainer') ||
            document.querySelector('.view-home .sections') ||
            document.querySelector('.view-home .scrollFrame') ||
            document.querySelector('.view-home');
    }

    function removeSection() {
        if (section && section.parentNode) section.parentNode.removeChild(section);
        section = null;
        renderedVersion = -1;
    }

    function currentLimit() {
        return expanded ? 40 : 18;
    }

    function load(force) {
        if (loading) return;

        var now = Date.now();

        if (!force) {
            if (items.length && now - lastLoad < 600000) {
                render();
                return;
            }
            if (now - lastAttempt < 60000) {
                render();
                return;
            }
        }

        lastAttempt = now;
        loading = true;

        withApiClient(function (client) {
            client.getJSON(client.getUrl('StrmAssistant/Douban/Recommendations?Limit=' +
                currentLimit() + '&Kind=hot')).then(function (result) {
                items = (result && result.Items) || [];
                loading = false;
                lastLoad = Date.now();
                renderVersion++;
                render();
            }, function () {
                loading = false;
                expanded = false;
                renderVersion++;
                if (!items.length) removeSection();
            });
        });
    }

    function createCard(item) {
        var card = document.createElement('a');
        card.className = 'strmAssistantDoubanCard';
        card.href = item.ItemId
            ? '#/item?id=' + encodeURIComponent(item.ItemId)
            : '#/search?query=' + encodeURIComponent(item.Title || '');
        card.title = item.ItemId ? labels.open : labels.search;

        var poster = document.createElement('div');
        poster.className = 'strmAssistantDoubanPoster';

        if (item.CoverUrl) {
            var image = document.createElement('img');
            image.src = item.CoverUrl;
            image.alt = item.Title || '';
            image.loading = 'lazy';
            image.referrerPolicy = 'no-referrer';
            poster.appendChild(image);
        } else {
            poster.textContent = '?';
        }

        card.appendChild(poster);
        addTrailerButton(card, item);

        var title = document.createElement('div');
        title.className = 'strmAssistantDoubanTitle';
        title.textContent = item.Title || '';
        card.appendChild(title);

        var metaParts = [];

        if (typeof item.Rating === 'number' && item.Rating > 0) {
            metaParts.push('\u2605 ' + item.Rating.toFixed(1));
        }
        if (item.Year) metaParts.push(item.Year);

        if (metaParts.length) {
            var meta = document.createElement('div');
            meta.className = 'strmAssistantDoubanMeta';
            meta.textContent = metaParts.join(' \u00b7 ');
            card.appendChild(meta);
        }

        return card;
    }

    function addTrailerButton(card, item) {
        if (!trailersEnabled || !item || !item.DoubanId) return;

        card.setAttribute('data-douban-id', item.DoubanId);

        if (item.TrailerUrl) {
            card.setAttribute('data-trailer-url', item.TrailerUrl);
            card.setAttribute('data-trailer-title', item.TrailerTitle || '');
            card.setAttribute('data-trailer-cover', item.TrailerCoverUrl || '');
        }

        var button = document.createElement('span');
        button.className = 'strmAssistantDoubanTrailer';
        button.setAttribute('role', 'button');
        button.setAttribute('tabindex', '0');
        button.textContent = '\u25B6 ' + labels.trailer;

        button.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' || event.key === ' ') {
                event.preventDefault();
                button.click();
            }
        });

        card.querySelector('.strmAssistantDoubanPoster').appendChild(button);
    }

    function render() {
        if (!isHome() || !items.length) {
            removeSection();
            return;
        }

        var host = getHost();
        if (!host) return;

        if (section && renderedVersion === renderVersion && document.contains(section)) return;

        if (!section || !document.contains(section)) {
            section = document.createElement('div');
            section.className = 'verticalSection strmAssistantDoubanSection';
            section.innerHTML = '<div class=""sectionTitleContainer flex align-items-center"">' +
                '<h2 class=""sectionTitle""></h2>' +
                '<button type=""button"" class=""strmAssistantDoubanMore""></button>' +
                '</div><div class=""itemsContainer strmAssistantDoubanItems""></div>';
            section.querySelector('.sectionTitle').textContent = labels.title;
            section.querySelector('.strmAssistantDoubanMore').addEventListener('click', function () {
                expanded = !expanded;
                renderVersion++;
                if (expanded) load(true);
                else render();
            });
            host.insertBefore(section, host.firstChild);
        }

        var container = section.querySelector('.strmAssistantDoubanItems');
        container.innerHTML = '';

        var visible = expanded ? items : items.slice(0, 18);

        for (var i = 0; i < visible.length; i++) {
            container.appendChild(createCard(visible[i]));
        }

        var more = section.querySelector('.strmAssistantDoubanMore');
        more.textContent = expanded ? labels.less : labels.more;
        more.style.display = items.length > 18 || expanded ? '' : 'none';

        renderedVersion = renderVersion;
    }

    function apply() {
        if (!isHome()) {
            removeSection();
            return;
        }

        render();
        load(false);
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            apply();
        }, 350);
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
"
                .Replace("@@TITLE@@", JsString("DoubanRecommendations_Title_Douban_Recommendations",
                    "Douban Recommendations"))
                .Replace("@@MORE@@", JsString("DoubanRecommendations_More_More", "More"))
                .Replace("@@LESS@@", JsString("DoubanRecommendations_Less_Less", "Less"))
                .Replace("@@OPEN@@", JsString("DoubanRecommendations_OpenInLibrary_Open_in_library",
                    "Open in library"))
                .Replace("@@SEARCH@@", JsString("DoubanRecommendations_SearchInLibrary_Search_in_library",
                    "Search in library"))
                .Replace("@@TRAILER@@", trailersEnabled
                    ? JsString("DoubanTrailer_Button_Trailer", "Trailer")
                    : string.Empty)
                .Replace("@@TRAILERS_ENABLED@@", trailersEnabled ? "true" : "false");
        }

        /// <summary>
        /// Handles the trailer button on Douban recommendation cards. Douban returns direct MP4
        /// URLs; they are played in a small in-page player. If the browser cannot play the direct
        /// link, the player offers a clearly marked new-tab fallback.
        /// </summary>
        private static string GetDoubanTrailerScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    if (document.getElementById('strmAssistantTrailerStyles')) return;

    var labels = {
        trailer: '@@TRAILER@@',
        loading: '@@LOADING@@',
        unavailable: '@@UNAVAILABLE@@',
        failed: '@@FAILED@@',
        playbackFailed: '@@PLAYBACK_FAILED@@',
        openExternal: '@@OPEN_EXTERNAL@@',
        close: '@@CLOSE@@',
        source: '@@SOURCE@@'
    };
    var apiClient = null;
    var noticeTimer = null;
    var overlay = null;

    function addStyles() {
        var style = document.createElement('style');
        style.id = 'strmAssistantTrailerStyles';
        style.textContent =
            '.strmAssistantDoubanTrailer{position:absolute;left:.35em;bottom:.35em;z-index:5;' +
            'display:inline-flex;align-items:center;gap:.2em;padding:.15em .5em;border-radius:.9em;' +
            'background:rgba(0,0,0,.72);color:#fff;font-size:.72em;line-height:1.5;font-weight:600;' +
            'cursor:pointer;user-select:none;box-shadow:0 1px 3px rgba(0,0,0,.45)}' +
            '.strmAssistantDoubanTrailer:hover,.strmAssistantDoubanTrailer:focus{' +
            'background:rgba(0,0,0,.9);outline:none}' +
            '.strmAssistantTrailerNotice{position:fixed;left:50%;bottom:2em;transform:translateX(-50%);' +
            'z-index:100000;max-width:80vw;padding:.6em 1em;border-radius:.4em;background:rgba(0,0,0,.85);' +
            'color:#fff;font-size:.9em;display:none}' +
            '.strmAssistantTrailerOverlay{position:fixed;left:0;top:0;right:0;bottom:0;z-index:99999;' +
            'background:rgba(0,0,0,.86);display:none;align-items:center;justify-content:center;padding:2em}' +
            '.strmAssistantTrailerDialog{width:92vw;max-width:1100px;background:#111;color:#fff;' +
            'border-radius:.5em;overflow:hidden;box-shadow:0 1em 3em rgba(0,0,0,.6)}' +
            '.strmAssistantTrailerHeader{display:flex;align-items:center;gap:1em;padding:.7em 1em}' +
            '.strmAssistantTrailerTitle{flex:1;font-size:1em;font-weight:600;overflow:hidden;' +
            'text-overflow:ellipsis;white-space:nowrap}' +
            '.strmAssistantTrailerClose{border:0;background:rgba(255,255,255,.14);color:#fff;' +
            'padding:.3em .8em;border-radius:.3em;cursor:pointer;font:inherit}' +
            '.strmAssistantTrailerClose:hover{background:rgba(255,255,255,.26)}' +
            '.strmAssistantTrailerVideo{display:block;width:100%;max-height:72vh;background:#000}' +
            '.strmAssistantTrailerFooter{display:flex;align-items:center;gap:1em;flex-wrap:wrap;' +
            'padding:.6em 1em .9em;font-size:.82em}' +
            '.strmAssistantTrailerSource{opacity:.7;flex:1}' +
            '.strmAssistantTrailerExternal{color:#8cc8ff;text-decoration:none}' +
            '.strmAssistantTrailerExternal:hover{text-decoration:underline}' +
            '.strmAssistantTrailerFallback{padding:0 1em .9em;color:#ffd166;font-size:.85em}';
        (document.head || document.documentElement).appendChild(style);
    }

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
                } else {
                    callback(null);
                }
            }, function () {
                callback(null);
            });
        } catch (e) {
            callback(null);
        }
    }

    function hideNotice() {
        var notice = document.getElementById('strmAssistantTrailerNotice');
        if (notice) notice.style.display = 'none';
    }

    function showNotice(text) {
        var notice = document.getElementById('strmAssistantTrailerNotice');
        if (!notice) {
            notice = document.createElement('div');
            notice.id = 'strmAssistantTrailerNotice';
            notice.className = 'strmAssistantTrailerNotice';
            document.body.appendChild(notice);
        }
        notice.textContent = text;
        notice.style.display = 'block';
        if (noticeTimer) clearTimeout(noticeTimer);
        noticeTimer = setTimeout(function () {
            notice.style.display = 'none';
        }, 3500);
    }

    function fetchTrailer(card, done) {
        var cachedUrl = card.getAttribute('data-trailer-url');

        if (cachedUrl) {
            done({
                TrailerUrl: cachedUrl,
                TrailerTitle: card.getAttribute('data-trailer-title') || '',
                CoverUrl: card.getAttribute('data-trailer-cover') || ''
            }, false);
            return;
        }

        var doubanId = card.getAttribute('data-douban-id');

        if (!doubanId) {
            done(null, false);
            return;
        }

        showNotice(labels.loading);

        withApiClient(function (client) {
            if (!client) {
                done(null, true);
                return;
            }

            client.getJSON(client.getUrl('StrmAssistant/Douban/Trailer?DoubanId=' +
                encodeURIComponent(doubanId))).then(function (result) {
                if (result && result.TrailerUrl) {
                    card.setAttribute('data-trailer-url', result.TrailerUrl);
                    card.setAttribute('data-trailer-title', result.TrailerTitle || '');
                    card.setAttribute('data-trailer-cover', result.CoverUrl || '');
                    done(result, false);
                } else {
                    done(null, false);
                }
            }, function () {
                done(null, true);
            });
        });
    }

    function buildOverlay() {
        overlay = document.createElement('div');
        overlay.id = 'strmAssistantTrailerOverlay';
        overlay.className = 'strmAssistantTrailerOverlay';
        overlay.innerHTML = '<div class=""strmAssistantTrailerDialog"" role=""dialog"" aria-modal=""true"">' +
            '<div class=""strmAssistantTrailerHeader"">' +
            '<span class=""strmAssistantTrailerTitle""></span>' +
            '<button type=""button"" class=""strmAssistantTrailerClose""></button>' +
            '</div>' +
            '<video class=""strmAssistantTrailerVideo"" controls playsinline preload=""metadata""></video>' +
            '<div class=""strmAssistantTrailerFooter"">' +
            '<span class=""strmAssistantTrailerSource""></span>' +
            '<a class=""strmAssistantTrailerExternal"" target=""_blank"" rel=""noopener noreferrer""></a>' +
            '</div>' +
            '<div class=""strmAssistantTrailerFallback"" hidden></div>' +
            '</div>';
        document.body.appendChild(overlay);

        overlay.addEventListener('click', function (event) {
            if (event.target === overlay) closePlayer();
        });

        overlay.querySelector('.strmAssistantTrailerClose').addEventListener('click', closePlayer);

        overlay.querySelector('.strmAssistantTrailerVideo').addEventListener('error', function () {
            if (!overlay) return;
            overlay.querySelector('.strmAssistantTrailerFallback').hidden = false;
        });

        document.addEventListener('keydown', function (event) {
            if (event.key === 'Escape') closePlayer();
        });
    }

    function showPlayer(url, title, coverUrl) {
        hideNotice();

        if (!overlay) buildOverlay();

        var video = overlay.querySelector('.strmAssistantTrailerVideo');
        var fallback = overlay.querySelector('.strmAssistantTrailerFallback');
        var external = overlay.querySelector('.strmAssistantTrailerExternal');

        overlay.querySelector('.strmAssistantTrailerTitle').textContent = title || labels.trailer;
        overlay.querySelector('.strmAssistantTrailerSource').textContent = labels.source;
        overlay.querySelector('.strmAssistantTrailerClose').textContent = labels.close;
        external.textContent = labels.openExternal;
        external.href = url;
        fallback.textContent = labels.playbackFailed;
        fallback.hidden = true;

        video.pause();
        video.removeAttribute('src');
        video.load();
        video.setAttribute('referrerpolicy', 'no-referrer');
        if (coverUrl) video.setAttribute('poster', coverUrl);
        else video.removeAttribute('poster');
        video.src = url;

        overlay.style.display = 'flex';

        var playPromise = video.play();
        if (playPromise && playPromise.catch) playPromise.catch(function () { });
    }

    function closePlayer() {
        if (!overlay) return;

        var video = overlay.querySelector('.strmAssistantTrailerVideo');
        video.pause();
        video.removeAttribute('src');
        video.load();
        overlay.style.display = 'none';
    }

    function openTrailer(card) {
        if (card.getAttribute('data-trailer-loading')) return;

        card.setAttribute('data-trailer-loading', '1');

        fetchTrailer(card, function (trailer, failed) {
            card.removeAttribute('data-trailer-loading');

            if (!trailer || !trailer.TrailerUrl) {
                showNotice(failed ? labels.failed : labels.unavailable);
                return;
            }

            showPlayer(trailer.TrailerUrl, trailer.TrailerTitle || labels.trailer,
                trailer.CoverUrl || card.getAttribute('data-trailer-cover') || '');
        });
    }

    document.addEventListener('click', function (event) {
        var target = event.target;
        var button = target && target.closest ? target.closest('.strmAssistantDoubanTrailer') : null;

        if (!button) return;

        event.preventDefault();
        event.stopPropagation();

        var card = button.closest('.strmAssistantDoubanCard');

        if (card) openTrailer(card);
    }, true);

    addStyles();
})();
"
                .Replace("@@TRAILER@@", JsString("DoubanTrailer_Button_Trailer", "Trailer"))
                .Replace("@@LOADING@@", JsString("DoubanTrailer_Loading_Loading_trailer",
                    "Loading trailer..."))
                .Replace("@@UNAVAILABLE@@", JsString("DoubanTrailer_Unavailable_No_trailer_available",
                    "No trailer available"))
                .Replace("@@FAILED@@", JsString("DoubanTrailer_Failed_Failed_to_load_trailer",
                    "Failed to load the trailer"))
                .Replace("@@PLAYBACK_FAILED@@",
                    JsString("DoubanTrailer_PlaybackFailed_In_page_playback_failed",
                        "In-page playback failed. Open the trailer in a new tab instead."))
                .Replace("@@OPEN_EXTERNAL@@",
                    JsString("DoubanTrailer_OpenExternal_Open_in_a_new_tab__fallback",
                        "Open in a new tab (fallback)"))
                .Replace("@@CLOSE@@", JsString("DoubanTrailer_Close_Close", "Close"))
                .Replace("@@SOURCE@@",
                    JsString("DoubanTrailer_Source_Trailer_source__Douban_public_API__display_only",
                        "Trailer source: Douban public API (display only)"));
        }

        /// <summary>
        /// Shows the cached Douban rating next to the rating area on an item detail page. The
        /// endpoint never calls Douban, so items without a cached rating simply keep the badge
        /// hidden. The cache is filled by the Douban supplement and the scheduled tasks.
        /// </summary>
        private static string GetDoubanRatingScript()
        {
            return @"
(function () {
    'use strict';

    var ratingClass = 'strmAssistantDoubanRating';
    var label = '@@LABEL@@';
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

    function removeRating() {
        var nodes = document.querySelectorAll('.' + ratingClass);
        for (var i = nodes.length - 1; i >= 0; i--) {
            if (nodes[i].parentNode) nodes[i].parentNode.removeChild(nodes[i]);
        }
    }

    function render(id) {
        if (currentItemId() !== id) return;

        var entry = cache[id];
        var rating = entry && typeof entry.Rating === 'number' && entry.Rating > 0
            ? entry.Rating
            : null;

        if (rating === null) {
            removeRating();
            return;
        }

        var text = label + ' ' + rating.toFixed(1);
        var existing = document.querySelectorAll('.' + ratingClass);
        if (existing.length === 1 && existing[0].textContent === text && document.contains(existing[0])) return;

        removeRating();

        var page = document.querySelector('.itemDetailPage');
        if (!page) return;

        var element = document.createElement('span');
        element.className = ratingClass;
        element.title = text;

        var labelElement = document.createElement('span');
        labelElement.className = 'strmAssistantDoubanRatingLabel';
        labelElement.textContent = label;
        element.appendChild(labelElement);
        element.appendChild(document.createTextNode(' ' + rating.toFixed(1)));

        var anchor = page.querySelector('.itemMiscInfo-primary') || page.querySelector('.itemMiscInfo');
        var ratingAnchor = anchor && (anchor.querySelector('.starRatingContainer') ||
            anchor.querySelector('.ratingValue'));

        if (ratingAnchor && ratingAnchor.parentNode) {
            ratingAnchor.parentNode.insertBefore(element, ratingAnchor.nextSibling);
            return;
        }

        if (anchor) {
            anchor.appendChild(element);
            return;
        }

        var name = page.querySelector('.itemName');
        if (name && name.parentNode) {
            name.parentNode.insertBefore(element, name.nextSibling);
            return;
        }

        var primary = page.querySelector('.detailPagePrimaryContent');
        if (primary) primary.appendChild(element);
    }

    function refresh() {
        var id = currentItemId();

        if (!id) {
            removeRating();
            return;
        }

        if (Object.prototype.hasOwnProperty.call(cache, id)) {
            render(id);
            return;
        }

        if (pendingId === id) return;
        pendingId = id;

        withApiClient(function (client) {
            client.getJSON(client.getUrl('StrmAssistant/Items/' + encodeURIComponent(id) +
                '/DoubanRating')).then(function (result) {
                if (pendingId === id) pendingId = null;
                cache[id] = result || {};
                render(id);
            }, function () {
                if (pendingId === id) pendingId = null;
                cache[id] = {};
                render(id);
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
".Replace("@@LABEL@@", JsString("DoubanRating_Label_Douban", "Douban"));
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
        /// <summary>
        /// Routes the web client's search requests to the plugin endpoint, which adds pinyin,
        /// simplified/traditional, alias and ranked matching on top of Emby's results. When the
        /// index is not available the original ApiClient method is called, so search always keeps
        /// working even while the index is still being built.
        /// </summary>
        private static string GetChineseSearchScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    function toCsv(value) {
        if (!value) return null;
        if (Array.isArray(value)) return value.join(',');
        return value;
    }

    function buildParams(query) {
        return {
            Q: query.SearchTerm,
            ParentId: query.ParentId,
            Limit: query.Limit,
            StartIndex: query.StartIndex,
            IncludeItemTypes: toCsv(query.IncludeItemTypes),
            ExcludeItemTypes: toCsv(query.ExcludeItemTypes),
            Fields: toCsv(query.Fields),
            ImageTypeLimit: query.ImageTypeLimit,
            EnableImages: query.EnableImages,
            EnableUserData: query.EnableUserData
        };
    }

    function patch(apiClient) {
        var proto = Object.getPrototypeOf(apiClient);
        if (!proto) return false;
        if (proto.__strmAssistantChineseSearchPatched) return true;

        if (typeof proto.getSearchResults === 'function') {
            var original = proto.getSearchResults;
            proto.getSearchResults = function (query, signal) {
                var self = this;
                var args = arguments;
                if (!query || !query.SearchTerm) return original.apply(self, args);

                var url;
                try {
                    url = self.getUrl('StrmAssistant/Search', buildParams(query));
                } catch (e) {
                    return original.apply(self, args);
                }

                return self.getJSON(url, signal).then(function (result) {
                    if (!result || result.Available === false) return original.apply(self, args);

                    var needsTypes = !(query.StartIndex || query.IncludeItemTypes ||
                        query.IncludeSearchTypes === false);
                    if (!needsTypes || typeof self.getItemTypes !== 'function') return result;

                    return self.getItemTypes(self.getCurrentUserId(), query, signal).then(function (types) {
                        if (types && types.Items) result.ItemTypes = types.Items;
                        return result;
                    }, function () {
                        return result;
                    });
                }, function () {
                    return original.apply(self, args);
                });
            };
        } else if (typeof proto.getItems === 'function') {
            var originalItems = proto.getItems;
            proto.getItems = function (userId, options, signal) {
                var self = this;
                var args = arguments;
                if (!options || !options.SearchTerm) return originalItems.apply(self, args);

                var url;
                try {
                    url = self.getUrl('StrmAssistant/Search', buildParams(options));
                } catch (e) {
                    return originalItems.apply(self, args);
                }

                return self.getJSON(url, signal).then(function (result) {
                    if (!result || result.Available === false) return originalItems.apply(self, args);
                    return result;
                }, function () {
                    return originalItems.apply(self, args);
                });
            };
        } else {
            return false;
        }

        proto.__strmAssistantChineseSearchPatched = true;
        return true;
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var apiClient = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (apiClient && patch(apiClient)) clearInterval(timer);
            }, function () { });
        } catch (e) { }
        if (attempts > 60) clearInterval(timer);
    }, 2000);
})();
";
        }

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
        /// Shows readable file names in the multi-version selector. Media source names are
        /// decorated before Emby renders the select options, and a small highlighted list mirrors
        /// the same sources without touching the item title or any other lists.
        /// </summary>
        private static string GetVersionNamesScript(bool enabled)
        {
            return @"
(function () {
    'use strict';

    var enabled = " + (enabled ? "true" : "false") + @";
    if (!enabled) return;
    if (window.__strmAssistantVersionNamesPatched) return;

    var itemCache = {};
    var patched = false;
    var scheduled = false;

    var extensionPattern = /\.(?:mkv|mp4|avi|ts|m2ts|iso|strm|mov|wmv|flv|webm|rmvb|m4v|mpg|mpeg)$/i;
    var highlightPattern = /\b(?:2160p|1080p|720p|576p|480p|8k|4k|uhd|blu-?ray|bdrip|brrip|remux|web-?dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax|h\s*26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg-?2|10-?bit|8-?bit|hdr10\+?|hdr|dolby\s+vision|dv|dts(?:-?hd)?(?:\s+ma)?|truehd|atmos|ddp?|eac3|ac3|aac|flac|opus)\b/gi;

    function decode(value) {
        try { return decodeURIComponent(value); } catch (e) { return value; }
    }

    function getFileName(source) {
        if (!source) return '';

        var path = source.Path ? String(source.Path) : '';
        if (path) {
            var marker = path.lastIndexOf('?/');
            if (marker >= 0) {
                path = path.substring(marker + 2);
            } else if (/^https?:\/\//i.test(path) && source.Name) {
                return String(source.Name);
            } else {
                var query = path.indexOf('?');
                if (query >= 0) path = path.substring(0, query);
                path = path.substring(Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\')) + 1);
            }

            path = decode(path);
            if (path) return path;
        }

        return source.Name ? String(source.Name) : '';
    }

    function getPrettyName(source) {
        var name = getFileName(source).replace(extensionPattern, '').replace(/[._]+/g, ' ')
            .replace(/\s+/g, ' ').trim();
        return name || (source && source.Name ? String(source.Name) : '');
    }

    function classifyToken(token) {
        var value = token.toLowerCase().replace(/-/g, ' ').replace(/\s+/g, ' ')
            .replace(/^[^a-z0-9]+|[^a-z0-9]+$/g, '');
        if (/^(?:2160p|1080p|720p|576p|480p|8k|4k|uhd)$/.test(value)) return 'resolution';
        if (/^(?:blu ray|bdrip|brrip|remux|web dl|webdl|webrip|hdtv|dvdrip|hdrip|amzn|nf|dsnp|atvp|hmax)$/.test(value)) return 'source';
        if (/^(?:h 26[45]|h26[45]|hevc|avc|x26[45]|av1|vp9|mpeg 2|10 bit|10bit|8 bit|8bit|hdr10\+?|hdr|dolby vision|dv)$/.test(value)) return 'codec';
        if (/^(?:dts|dts hd|dts hd ma|truehd|atmos|dd|ddp|eac3|ac3|aac|flac|opus)$/.test(value)) return 'audio';
        return '';
    }

    function appendToken(element, token) {
        var kind = classifyToken(token);
        var span = document.createElement('span');
        span.textContent = token;
        span.className = 'strmAssistantVersionToken' + (kind ? ' strmAssistantVersionToken-' + kind : '');
        element.appendChild(span);
    }

    function appendHighlighted(element, text) {
        var lastIndex = 0;
        var match;
        highlightPattern.lastIndex = 0;

        while ((match = highlightPattern.exec(text)) !== null) {
            if (match.index > lastIndex) {
                element.appendChild(document.createTextNode(text.substring(lastIndex, match.index)));
            }
            appendToken(element, match[0]);
            lastIndex = highlightPattern.lastIndex;
            if (!match[0].length) highlightPattern.lastIndex++;
        }

        if (lastIndex < text.length) {
            element.appendChild(document.createTextNode(text.substring(lastIndex)));
        }
    }

    function decorateSource(source) {
        if (!source) return '';

        var prettyName = source.__strmAssistantFriendlyName;
        if (!prettyName) {
            prettyName = getPrettyName(source);
            source.__strmAssistantFriendlyName = prettyName;
        }
        if (prettyName) source.Name = prettyName;
        return prettyName || '';
    }

    function rememberSources(sources) {
        if (!Array.isArray(sources) || sources.length < 2) return;
        for (var i = 0; i < sources.length; i++) decorateSource(sources[i]);
    }

    function rememberItem(item) {
        if (!item || item.Id == null || !Array.isArray(item.MediaSources) ||
            item.MediaSources.length < 2) return;

        var signature = [];
        for (var i = 0; i < item.MediaSources.length; i++) {
            var source = item.MediaSources[i];
            if (!source) continue;

            var prettyName = decorateSource(source);
            signature.push((source.Id || '') + '|' + (source.ItemId || '') + '|' + prettyName);
        }

        itemCache[String(item.Id)] = { item: item, signature: signature.join('||') };
    }

    function rememberResponse(data) {
        if (!data || typeof data !== 'object') return;
        rememberItem(data);
        if (data.Item && data.Item !== data) rememberItem(data.Item);
    }

    function findCurrentItem(select) {
        var ids = {};
        for (var i = 0; i < select.options.length; i++) ids[select.options[i].value] = true;

        for (var key in itemCache) {
            if (!Object.prototype.hasOwnProperty.call(itemCache, key)) continue;
            var entry = itemCache[key];
            var sources = entry.item.MediaSources || [];
            for (var j = 0; j < sources.length; j++) {
                if (ids[sources[j].Id] || ids[sources[j].ItemId]) return entry;
            }
        }

        return null;
    }

    function entryFromOptions(select) {
        if (!select || select.options.length < 2) return null;

        var sources = [];
        var signature = [];
        for (var i = 0; i < select.options.length; i++) {
            var option = select.options[i];
            var name = option.textContent || option.value;
            sources.push({
                Id: option.value,
                ItemId: option.value,
                Name: name
            });
            signature.push(option.value + '|' + name);
        }

        return { item: { MediaSources: sources }, signature: signature.join('||') };
    }

    function updateSelectLabels(select, entry) {
        var changed = false;
        var sources = entry.item.MediaSources || [];

        for (var i = 0; i < select.options.length; i++) {
            var option = select.options[i];
            var source = null;
            for (var j = 0; j < sources.length; j++) {
                if (sources[j].Id === option.value || sources[j].ItemId === option.value) {
                    source = sources[j];
                    break;
                }
            }

            var prettyName = source ? decorateSource(source) : '';
            if (!prettyName) continue;
            if (option.textContent !== prettyName) {
                option.textContent = prettyName;
                changed = true;
            }
            if (option.title !== prettyName) option.title = prettyName;
        }

        if (changed && typeof select.refreshItems === 'function') {
            select.refreshItems();
        }
    }

    function updateSelection(select) {
        var container = select.closest ? select.closest('.selectSourceContainer') : null;
        if (!container) return;
        var buttons = container.querySelectorAll('.strmAssistantVersionItem');
        for (var i = 0; i < buttons.length; i++) {
            var selected = buttons[i].getAttribute('data-source-id') === select.value;
            buttons[i].classList.toggle('strmAssistantVersionSelected', selected);
            buttons[i].setAttribute('aria-selected', selected ? 'true' : 'false');
        }
    }

    function renderVersionList(entry, select) {
        var container = select.closest ? select.closest('.selectSourceContainer') : null;
        if (!container) return;

        var list = container.querySelector('.strmAssistantVersionList');
        if (!list) {
            list = document.createElement('div');
            list.className = 'strmAssistantVersionList';
            list.setAttribute('role', 'listbox');
            container.appendChild(list);
        }

        if (list.getAttribute('data-signature') !== entry.signature) {
            while (list.firstChild) list.removeChild(list.firstChild);
            list.setAttribute('data-signature', entry.signature);

            for (var i = 0; i < entry.item.MediaSources.length; i++) {
                (function (source) {
                    if (!source) return;

                    var button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'strmAssistantVersionItem';
                    button.setAttribute('role', 'option');
                    button.setAttribute('data-source-id', source.Id || source.ItemId || '');
                    button.title = source.__strmAssistantFriendlyName || source.Name || '';

                    var label = document.createElement('span');
                    label.className = 'strmAssistantVersionName';
                    appendHighlighted(label, button.title);
                    button.appendChild(label);

                    button.addEventListener('click', function (event) {
                        event.preventDefault();
                        event.stopPropagation();
                        var sourceId = button.getAttribute('data-source-id');
                        if (select.value !== sourceId) {
                            select.value = sourceId;
                            select.dispatchEvent(new Event('change', { bubbles: true }));
                        }
                    });

                    list.appendChild(button);
                })(entry.item.MediaSources[i]);
            }
        }

        updateSelectLabels(select, entry);
        updateSelection(select);
    }

    function apply() {
        var select = document.querySelector('.itemView .selectSource') ||
            document.querySelector('.selectSource');
        if (!select) return;

        if (!select.__strmAssistantVersionListBound) {
            select.__strmAssistantVersionListBound = true;
            select.addEventListener('change', function () { updateSelection(select); });
        }

        var entry = findCurrentItem(select) || entryFromOptions(select);
        if (entry) renderVersionList(entry, select);
    }

    function schedule() {
        if (scheduled) return;
        scheduled = true;
        setTimeout(function () {
            scheduled = false;
            try { apply(); } catch (e) { }
        }, 150);
    }

    function patchApiClient(connectionManager) {
        if (patched) return true;
        if (!connectionManager || !connectionManager.currentApiClient) return false;

        var apiClient = connectionManager.currentApiClient();
        if (!apiClient) return false;

        var proto = Object.getPrototypeOf(apiClient);
        if (!proto || typeof proto.getJSON !== 'function') return false;

        if (proto.__strmAssistantVersionNamesPatched) {
            patched = true;
            return true;
        }

        var originalGetJSON = proto.getJSON;
        proto.getJSON = function () {
            var result = originalGetJSON.apply(this, arguments);
            if (result && typeof result.then === 'function') {
                return result.then(function (data) {
                    try { rememberResponse(data); } catch (e) { }
                    return data;
                });
            }
            try { rememberResponse(result); } catch (e) { }
            return result;
        };

        if (typeof proto.getPlaybackInfo === 'function') {
            var originalGetPlaybackInfo = proto.getPlaybackInfo;
            proto.getPlaybackInfo = function () {
                var result = originalGetPlaybackInfo.apply(this, arguments);
                if (result && typeof result.then === 'function') {
                    return result.then(function (response) {
                        try { rememberSources(response && response.MediaSources); } catch (e) { }
                        return response;
                    });
                }
                try { rememberSources(result && result.MediaSources); } catch (e) { }
                return result;
            };
        }

        proto.__strmAssistantVersionNamesPatched = true;
        patched = true;
        return true;
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
        try {
            require(['connectionManager']).then(function (modules) {
                if (patchApiClient(modules && modules[0])) clearInterval(timer);
            }, function () { });
        } catch (e) { }
        if (attempts > 60) clearInterval(timer);
    }, 2000);

    start();
})();
";
        }

        /// <summary>
        /// Per-version playback progress. Emby's UserData keeps a single PlaybackPositionTicks per
        /// item key shared by every version, so the wrapper asks the plugin for the position that
        /// belongs to the media source being played and passes it to the playback manager. Movies
        /// are checked even with a single source because the same movie can be split across
        /// libraries while still sharing Emby's provider-based user-data key.
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
                    if (sources.length && (sources.length > 1 || item.Type === 'Movie')) {
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
        /// Per-library favorites. Emby's user data is keyed by provider id, so the same movie in
        /// two libraries shares one favorite. The plugin records favorites per item; this wrapper
        /// replaces the favorite flag in item payloads with the plugin's value and drops
        /// cross-library copies from favorite query results.
        /// </summary>
        private static string GetVersionFavoriteScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    var patched = false;
    var cache = {};
    var resolved = {};
    var pending = {};
    var maxBatch = 100;

    function isItem(node) {
        return node && typeof node === 'object' && !Array.isArray(node) &&
            node.Id && node.UserData && typeof node.UserData.IsFavorite === 'boolean';
    }

    function walk(node, visit, depth) {
        if (!node || typeof node !== 'object' || depth > 4) return;
        if (Array.isArray(node)) {
            for (var i = 0; i < node.length; i++) walk(node[i], visit, depth + 1);
            return;
        }
        visit(node);
        for (var key in node) {
            if (!Object.prototype.hasOwnProperty.call(node, key)) continue;
            if (key === 'UserData' || key === 'MediaSources' || key === 'MediaStreams' ||
                key === 'People' || key === 'Studios' || key === 'Genres') continue;
            walk(node[key], visit, depth + 1);
        }
    }

    function collect(node, ids) {
        walk(node, function (item) {
            if (!isItem(item)) return;
            var id = item.Id;
            if (Object.prototype.hasOwnProperty.call(cache, id)) return;
            if (Object.prototype.hasOwnProperty.call(resolved, id)) return;
            if (Object.prototype.hasOwnProperty.call(pending, id)) return;
            ids.push(id);
        }, 0);
    }

    function apply(node) {
        walk(node, function (item) {
            if (!isItem(item)) return;
            if (Object.prototype.hasOwnProperty.call(cache, item.Id)) {
                item.UserData.IsFavorite = cache[item.Id];
            }
        }, 0);
    }

    function isFavoriteQuery(url) {
        var value = String(url || '');
        return /[?&]IsFavorite=true/i.test(value) ||
            /[?&]Filters=[^&]*IsFavorite/i.test(value);
    }

    function filterFavorites(data) {
        if (!data || !data.Items || !data.Items.length) return;
        data.Items = data.Items.filter(function (item) {
            if (!item || !item.Id) return true;
            if (!Object.prototype.hasOwnProperty.call(cache, item.Id)) return true;
            return cache[item.Id] === true;
        });
    }

    function fetchMissing(apiClient, ids, originalGetJSON) {
        if (!ids.length) return Promise.resolve();

        var chunk = ids.slice(0, maxBatch);
        for (var i = 0; i < chunk.length; i++) pending[chunk[i]] = true;

        var url = apiClient.getUrl('StrmAssistant/VersionFavorites/Query', { Ids: chunk.join(',') });

        return originalGetJSON.call(apiClient, url).then(function (response) {
            var items = (response && response.Items) || [];
            for (var j = 0; j < items.length; j++) {
                if (items[j] && items[j].ItemId) cache[items[j].ItemId] = !!items[j].IsFavorite;
            }
            for (var k = 0; k < chunk.length; k++) {
                if (!Object.prototype.hasOwnProperty.call(cache, chunk[k])) resolved[chunk[k]] = true;
                delete pending[chunk[k]];
            }
            if (ids.length > chunk.length) return fetchMissing(apiClient, ids.slice(chunk.length), originalGetJSON);
        }, function () {
            for (var k = 0; k < chunk.length; k++) {
                resolved[chunk[k]] = true;
                delete pending[chunk[k]];
            }
        });
    }

    function invalidate(itemId) {
        if (!itemId) return;
        delete cache[itemId];
        delete resolved[itemId];
    }

    function favoriteItemId(url) {
        var match = /\/FavoriteItems\/([^\/?#]+)/i.exec(String(url || ''));
        if (!match) return null;
        try {
            return decodeURIComponent(match[1]);
        } catch (e) {
            return match[1];
        }
    }

    function patchApiClient(connectionManager) {
        if (patched) return true;
        if (!connectionManager || !connectionManager.currentApiClient) return false;

        var apiClient = connectionManager.currentApiClient();
        if (!apiClient) return false;

        var proto = Object.getPrototypeOf(apiClient);
        if (!proto || typeof proto.getJSON !== 'function') return false;

        if (proto.__strmAssistantVersionFavoritePatched) {
            patched = true;
            return true;
        }

        var originalGetJSON = proto.getJSON;
        proto.getJSON = function (url) {
            var self = this;
            var args = arguments;
            var result = originalGetJSON.apply(this, args);

            if (!result || typeof result.then !== 'function') {
                try { apply(result); if (isFavoriteQuery(url)) filterFavorites(result); } catch (e) { }
                return result;
            }

            return result.then(function (data) {
                var ids = [];
                try { collect(data, ids); } catch (e) { }

                if (!ids.length) {
                    try { apply(data); if (isFavoriteQuery(url)) filterFavorites(data); } catch (e) { }
                    return data;
                }

                return fetchMissing(self, ids, originalGetJSON).then(function () {
                    try { apply(data); if (isFavoriteQuery(url)) filterFavorites(data); } catch (e) { }
                    return data;
                });
            });
        };

        if (typeof proto.ajax === 'function') {
            var originalAjax = proto.ajax;
            proto.ajax = function (request) {
                var url = typeof request === 'string' ? request : (request && request.url);
                var itemId = favoriteItemId(url);
                if (itemId) invalidate(itemId);

                var result = originalAjax.apply(this, arguments);

                if (itemId && result && typeof result.then === 'function') {
                    result.then(function () { invalidate(itemId); }, function () { invalidate(itemId); });
                }

                return result;
            };
        }

        var favoriteMethods = ['updateFavorite', 'markFavorite', 'unmarkFavorite'];
        for (var i = 0; i < favoriteMethods.length; i++) {
            (function (name) {
                if (typeof proto[name] !== 'function') return;
                var original = proto[name];
                proto[name] = function () {
                    var itemId = arguments[0];
                    if (itemId) invalidate(itemId);
                    var result = original.apply(this, arguments);
                    if (itemId && result && typeof result.then === 'function') {
                        result.then(function () { invalidate(itemId); }, function () { invalidate(itemId); });
                    }
                    return result;
                };
            })(favoriteMethods[i]);
        }

        proto.__strmAssistantVersionFavoritePatched = true;
        patched = true;
        return true;
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        try {
            require(['connectionManager']).then(function (modules) {
                if (patchApiClient(modules && modules[0])) clearInterval(timer);
            }, function () { });
        } catch (e) { }
        if (attempts > 60) clearInterval(timer);
    }, 2000);
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
        failed: '@@FAILED@@',
        settings: '@@SETTINGS@@'
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

        var settingsLink = document.createElement('a');
        settingsLink.id = 'strmAssistantIntroSettingsLink';
        settingsLink.href = '#';
        settingsLink.textContent = labels.settings;
        settingsLink.style.cssText = 'display:block;margin-top:.5em;color:#6fc3ff;' +
            'text-decoration:none;font-size:.95em';

        settingsLink.addEventListener('click', function (event) {
            event.preventDefault();

            withApiClient(function (client) {
                var url = client.getUrl('StrmAssistant/IntroDetectionSettings');
                var accessToken = client.accessToken ? client.accessToken() : '';

                if (accessToken && url.indexOf('api_key=') === -1) {
                    url += (url.indexOf('?') === -1 ? '?' : '&') +
                        'api_key=' + encodeURIComponent(accessToken);
                }

                window.open(url, '_blank');
            });
        });

        panel.appendChild(header);
        panel.appendChild(list);
        panel.appendChild(button);
        panel.appendChild(settingsLink);
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
                    "Failed to queue search index rebuild"))
                .Replace("@@SETTINGS@@", JsString(
                    "IntroDetectionSettings_Title_Per_Library_Intro_Detection_Length",
                    "Per-Library Intro Detection Length"));
        }

        /// <summary>
        /// Enables multi-select on the season page episode list (delete, download and convert are
        /// provided by the built-in item commands once selection is active) and on the library
        /// setup page so several libraries can be selected and scanned together.
        /// </summary>
        private static string GetMultiSelectActionsScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    function isLibrarySetup() {
        return (location.hash || '').toLowerCase().indexOf('librarysetup') !== -1;
    }

    function isSeasonTrackList(options) {
        var container = options && options.itemsContainer;
        if (!container || !container.classList) return false;
        if (!container.classList.contains('trackList')) return false;
        return options.showIndexNumber === false && options.imageSize === 'large';
    }

    function patch(renderer) {
        if (!renderer || typeof renderer.getItemsHtml !== 'function') return false;
        if (renderer.__strmAssistantMultiSelectPatched) return true;

        var original = renderer.getItemsHtml;

        renderer.getItemsHtml = function (items, options) {
            try {
                if (options && (isSeasonTrackList(options) || isLibrarySetup())) {
                    options.multiSelect = true;
                }
            } catch (e) { }

            return original.apply(this, arguments);
        };

        renderer.__strmAssistantMultiSelectPatched = true;
        return true;
    }

    function start() {
        try {
            require(['modules/listview/listview.js']).then(function (modules) {
                var renderer = modules && modules[0] && (modules[0].default || modules[0]);
                patch(renderer);
            }, function () { });
            require(['modules/cardbuilder/cardbuilder.js']).then(function (modules) {
                var renderer = modules && modules[0] && (modules[0].default || modules[0]);
                patch(renderer);
            }, function () { });
        } catch (e) { }
    }

    start();
    setTimeout(start, 3000);
})();
";
        }

        /// <summary>
        /// Re-sorts title based result pages with a locale aware natural collation so numbers are
        /// compared by value and Chinese titles keep their reading order, e.g. 白蛇：缘起 before
        /// 白蛇2：青蛇劫起.
        /// </summary>
        private static string GetNaturalSortScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    var collator = null;
    try {
        collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });
    } catch (e) {
        collator = null;
    }

    function compare(a, b) {
        var left = (a && (a.SortName || a.Name)) || '';
        var right = (b && (b.SortName || b.Name)) || '';
        if (collator) return collator.compare(left, right);
        return left.localeCompare(right, undefined, { numeric: true, sensitivity: 'base' });
    }

    function isTitleSort(query) {
        if (!query || !query.SortBy) return false;
        // Never touch search results: their order comes from the ranked search index.
        if (query.SearchTerm) return false;
        var fields = String(query.SortBy).split(',');
        return fields.length === 1 && fields[0].toLowerCase() === 'sortname';
    }

    function patch(client) {
        var proto = Object.getPrototypeOf(client);
        if (!proto) return false;
        if (proto.__strmAssistantNaturalSortPatched) return true;

        var original = proto.getItems;
        if (typeof original !== 'function') return false;

        proto.getItems = function (userId, options, signal) {
            var self = this;
            var args = arguments;

            return original.apply(self, args).then(function (result) {
                if (result && Array.isArray(result.Items) && isTitleSort(options)) {
                    var descending = options.SortOrder === 'Descending';
                    result.Items.sort(function (a, b) {
                        var value = compare(a, b);
                        return descending ? -value : value;
                    });
                }
                return result;
            });
        };

        proto.__strmAssistantNaturalSortPatched = true;
        return true;
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var apiClient = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (apiClient && patch(apiClient)) clearInterval(timer);
            }, function () { });
        } catch (e) { }
        if (attempts > 60) clearInterval(timer);
    }, 2000);
})();
";
        }

        /// <summary>
        /// Re-orders the user library views to match the order configured by the first admin.
        /// The order is read from LibraryApi.AdminOrderedViews when the script is generated.
        /// </summary>
        private static string GetEnforceLibraryOrderScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            var orderedViews = StrmAssistant.Common.LibraryApi.AdminOrderedViews;
            if (orderedViews == null || orderedViews.Length == 0) return string.Empty;

            var orderedIds = string.Join("','", System.Array.ConvertAll(orderedViews,
                id => (id ?? string.Empty).Replace("'", string.Empty)));

            return @"
(function () {
    'use strict';

    var order = ['@@ORDER@@'];
    if (!order.length) return;

    function rank(item) {
        var id = item && item.Id;
        var index = id ? order.indexOf(id) : -1;
        return index === -1 ? order.length : index;
    }

    function patch(client) {
        var proto = Object.getPrototypeOf(client);
        if (!proto) return false;
        if (proto.__strmAssistantLibraryOrderPatched) return true;

        var original = proto.getUserViews;
        if (typeof original !== 'function') return false;

        proto.getUserViews = function (options, userId, signal) {
            var self = this;
            var args = arguments;

            return original.apply(self, args).then(function (result) {
                if (result && Array.isArray(result.Items) && result.Items.length > 1) {
                    result.Items = result.Items.slice().sort(function (a, b) {
                        return rank(a) - rank(b);
                    });
                }
                return result;
            });
        };

        proto.__strmAssistantLibraryOrderPatched = true;
        return true;
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var apiClient = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (apiClient && patch(apiClient)) clearInterval(timer);
            }, function () { });
        } catch (e) { }
        if (attempts > 60) clearInterval(timer);
    }, 2000);
})();
".Replace("@@ORDER@@", orderedIds);
        }

        /// <summary>
        /// Reverses the log line page returned by the server so the log viewer shows the most
        /// recent entries first.
        /// </summary>
        private static string GetLogNewestFirstScript(bool enabled)
        {
            if (!enabled) return string.Empty;

            return @"
(function () {
    'use strict';

    function patch(client) {
        var proto = Object.getPrototypeOf(client);
        if (!proto) return false;
        if (proto.__strmAssistantLogOrderPatched) return true;

        var original = proto.getLogLines;
        if (typeof original !== 'function') return false;

        proto.getLogLines = function (options, signal) {
            var self = this;
            var args = arguments;

            return original.apply(self, args).then(function (result) {
                if (Array.isArray(result)) return result.slice().reverse();

                if (result && Array.isArray(result.Items)) {
                    result.Items = result.Items.slice().reverse();
                } else if (result && Array.isArray(result.LogLines)) {
                    result.LogLines = result.LogLines.slice().reverse();
                }

                return result;
            });
        };

        proto.__strmAssistantLogOrderPatched = true;
        return true;
    }

    var attempts = 0;
    var timer = setInterval(function () {
        attempts++;
        try {
            require(['connectionManager']).then(function (modules) {
                var connectionManager = modules && modules[0];
                var apiClient = connectionManager && connectionManager.currentApiClient &&
                    connectionManager.currentApiClient();
                if (apiClient && patch(apiClient)) clearInterval(timer);
            }, function () { });
        } catch (e) { }
        if (attempts > 60) clearInterval(timer);
    }, 2000);
})();
";
        }

        /// <summary>
        /// Renders one ordered, deduplicated badge list in the media info header. Dynamic range
        /// labels come first, then audio formats, then remaining tags (fingerprints and other
        /// user tags). The list is rebuilt from item tags and media streams on every render, so
        /// the same label is emitted only once even when several sources describe it.
        /// </summary>
        private static string GetMediaBadgeScript()
        {
            return @"
(function () {
    'use strict';

    if (window.__strmAssistantMediaBadgePatched) return;

    function normalize(value) {
        return String(value == null ? '' : value).replace(/^\s+|\s+$/g, '').toLowerCase();
    }

    function addBadge(badges, seen, value) {
        var text = String(value == null ? '' : value).replace(/^\s+|\s+$/g, '');
        if (!text) return;

        var key = normalize(text);
        if (seen[key]) return;

        seen[key] = true;
        badges.push(text);
    }

    function isP7LayerTag(value) {
        return /^dolby vision p7(?: \((?:fel|mel)\))?$/i.test(String(value == null ? '' : value).trim());
    }

    function getP7LayerTag(tags) {
        for (var i = 0; i < tags.length; i++) {
            var value = String(tags[i] == null ? '' : tags[i]).trim();
            if (/^dolby vision p7 \((?:fel|mel)\)$/i.test(value)) return value;
        }

        return '';
    }

    function isDynamicRangeTag(value) {
        return /^(?:hdr vivid|hdr10\+|hdr10|hlg|dolby vision(?: p[0-9]+(?: \((?:fel|mel)\))?)?)$/i
            .test(String(value == null ? '' : value).trim());
    }

    function getVideoRangeLabel(stream, p7LayerTag) {
        if (!stream) return '';

        var videoType = String(stream.ExtendedVideoType == null ? '' : stream.ExtendedVideoType);
        var videoRange = String(stream.VideoRange == null ? '' : stream.VideoRange);
        var subtype = String(stream.ExtendedVideoSubType == null ? '' : stream.ExtendedVideoSubType);
        var description = String(stream.ExtendedVideoSubTypeDescription == null
            ? ''
            : stream.ExtendedVideoSubTypeDescription);

        if (videoType === 'DolbyVision' || videoType === '130' || videoRange === 'DolbyVision') {
            if (subtype === 'DoviProfile50' || subtype === '13050' || /profile\s*5(?:\.0)?/i.test(description)) {
                return 'Dolby Vision P5';
            }

            if (subtype === 'DoviProfile76' || subtype === '13076' || /profile\s*7(?:\.6)?/i.test(description)) {
                return p7LayerTag || 'Dolby Vision P7';
            }

            return 'Dolby Vision';
        }

        if (videoType === 'Hdr10Plus' || videoType === '110') return 'HDR10+';
        if (videoType === 'Hdr10' || videoType === '100') return 'HDR10';
        if (videoType === 'HyperLogGamma' || videoType === '120') return 'HLG';

        return '';
    }

    function getAudioBadge(stream) {
        if (!stream) return '';

        // mediainfo.js receives normalized copies whose Type is 'MediaStream' and whose
        // original type is kept in StreamType; other callers pass the raw stream.
        var type = stream.StreamType || stream.Type;
        if (type !== 'Audio') return '';

        var profile = String(stream.Profile || '').toLowerCase();
        if (!profile) return '';

        var codec = String(stream.Codec || '').toLowerCase();

        // ffprobe reports Atmos in the profile of the codec that carries it:
        // 'Dolby TrueHD + Dolby Atmos' and 'Dolby Digital Plus + Dolby Atmos' (E-AC-3 JOC).
        if ((codec === 'truehd' || codec === 'eac3' || codec === 'ac3') &&
            profile.indexOf('atmos') !== -1) {
            return 'Dolby Atmos';
        }

        // ffprobe reports DTS:X in the DTS-HD MA profile ('DTS-HD MA + DTS:X').
        if (codec === 'dts' && /dts[\s:_-]*x/.test(profile)) {
            return 'DTS:X';
        }

        return '';
    }

    function buildMediaBadges(item, options) {
        var badges = [];
        var seen = {};
        var streams = (options && options.mediaSource && options.mediaSource.MediaStreams) ||
            (item && item.MediaStreams) || [];
        var tags = (item && item.Tags) || [];
        var p7LayerTag = getP7LayerTag(tags);
        var i;

        // 1. Dynamic range.
        for (i = 0; i < tags.length; i++) {
            var tagText = String(tags[i] == null ? '' : tags[i]).trim();

            if (isP7LayerTag(tagText)) {
                addBadge(badges, seen, p7LayerTag || 'Dolby Vision P7');
                continue;
            }

            if (!isDynamicRangeTag(tagText)) continue;

            addBadge(badges, seen, tagText);
        }

        for (i = 0; i < streams.length; i++) {
            addBadge(badges, seen, getVideoRangeLabel(streams[i], p7LayerTag));
        }

        // 2. Audio formats.
        for (i = 0; i < streams.length; i++) {
            addBadge(badges, seen, getAudioBadge(streams[i]));
        }

        // 3. Fingerprints and any other item tags.
        for (i = 0; i < tags.length; i++) {
            var tag = String(tags[i] == null ? '' : tags[i]).trim();
            if (!tag || isDynamicRangeTag(tag) || isP7LayerTag(tag)) continue;

            addBadge(badges, seen, tag);
        }

        return badges;
    }

    function escapeHtml(value) {
        return String(value).replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/""/g, '&quot;');
    }

    function renderBadges(badges) {
        var html = '<div class=""mediaInfoItems mediaInfoItems-condensed align-self-center"">';

        for (var i = 0; i < badges.length; i++) {
            html += '<div class=""mediaInfoItem mediaInfoItem-border""><span class="""">' +
                escapeHtml(badges[i]) + '</span></div>';
        }

        return html + '</div>';
    }

    function patch(mod) {
        var mediainfo = mod && (mod.default || mod);
        if (!mediainfo || typeof mediainfo.getPrimaryMediaInfoHtml !== 'function') return false;
        if (mediainfo.getPrimaryMediaInfoHtml.__strmAssistantMediaBadgePatched) return true;

        var original = mediainfo.getPrimaryMediaInfoHtml;

        mediainfo.getPrimaryMediaInfoHtml = function (item, options) {
            var html = original.apply(this, arguments) || '';

            try {
                if (options && options.mediaInfoIcons === false) return html;

                var badges = buildMediaBadges(item, options || {});
                if (badges.length) html += renderBadges(badges);
            } catch (e) { }

            return html;
        };

        mediainfo.getPrimaryMediaInfoHtml.__strmAssistantMediaBadgePatched = true;
        window.__strmAssistantMediaBadgePatched = true;
        return true;
    }

    var timer = null;

    function start() {
        try {
            require(['modules/mediainfo/mediainfo.js']).then(function (modules) {
                if (patch(modules && modules[0]) && timer) clearInterval(timer);
            }, function () { });
        } catch (e) { }
    }

    var attempts = 0;
    timer = setInterval(function () {
        attempts++;
        start();
        if (attempts > 60) clearInterval(timer);
    }, 2000);

    start();
})();
";
        }

        internal static string JsString(string resourceName, string fallback)
        {
            var value = Resources.ResourceManager.GetString(resourceName);

            return (string.IsNullOrEmpty(value) ? fallback : value)
                .Replace("\\", "\\\\").Replace("'", "\\'");
        }
    }
}
