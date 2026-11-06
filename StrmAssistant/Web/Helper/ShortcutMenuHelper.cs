using MediaBrowser.Controller.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace StrmAssistant.Web.Helper
{
    internal static class ShortcutMenuHelper
    {
        public static string ModifiedShortcutsString { get; private set; }

        public static MemoryStream StrmAssistantJs { get; private set; }

        private static string _shortcutsJs;
        private static string _injectShortcutCommand;

        /// <summary>
        /// Builds the shortcuts script with the current option values. Called for each request
        /// so that toggling UI related options does not require a server restart.
        /// </summary>
        public static string GetShortcutsScript()
        {
            return (_shortcutsJs ?? string.Empty) + (_injectShortcutCommand ?? string.Empty) +
                   GetHiddenBoxSetsStateScript() + GetIntroRedetectStateScript() +
                   UiEnhancementScript.Get();
        }

        /// <summary>
        /// Publishes the localized texts of the "Re-detect Intro" shortcut command. Built per
        /// request so the option and the server culture are always up to date.
        /// </summary>
        private static string GetIntroRedetectStateScript()
        {
            var enabled = Plugin.Instance?.GetPluginOptions()?.IntroSkipOptions
                ?.ShowRedetectIntroCommand ?? true;

            var builder = new StringBuilder();
            builder.Append("\nwindow.strmAssistantIntroRedetect = {");
            builder.Append("enabled: ").Append(enabled ? "true" : "false").Append(", ");
            builder.Append("name: '")
                .Append(UiEnhancementScript.JsString("IntroRedetect_CommandName_Re_detect_Intro", "Re-detect Intro"))
                .Append("', ");
            builder.Append("started: '")
                .Append(UiEnhancementScript.JsString("IntroRedetect_Started_Intro_re_detection_started",
                    "Intro re-detection started"))
                .Append("', ");
            builder.Append("running: '")
                .Append(UiEnhancementScript.JsString("IntroRedetect_AlreadyRunning_Intro_re_detection_is_already_running",
                    "Intro re-detection is already running"))
                .Append("', ");
            builder.Append("failed: '")
                .Append(UiEnhancementScript.JsString("IntroRedetect_Failed_Failed_to_start_intro_re_detection",
                    "Failed to start intro re-detection"))
                .Append("'");
            builder.Append("};\n");

            return builder.ToString();
        }

        private static string GetHiddenBoxSetsStateScript()
        {
            var hiddenIds = Plugin.BoxSetHomeVisibilityStore?.GetHiddenIds();

            if (hiddenIds is null || hiddenIds.Count == 0) return string.Empty;

            var builder = new StringBuilder();
            builder.Append("\nwindow.strmAssistantHiddenBoxSets = {");

            for (var i = 0; i < hiddenIds.Count; i++)
            {
                if (i > 0) builder.Append(',');

                var id = (hiddenIds[i] ?? string.Empty)
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"");

                builder.Append('"').Append(id).Append("\":true");
            }

            builder.Append("};\n");

            return builder.ToString();
        }

        public static void Initialize(IServerConfigurationManager configurationManager)
        {
            try
            {
                StrmAssistantJs = GetResourceStream("strmassistant.js");
                ModifyShortcutMenu(configurationManager);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error($"{nameof(ShortcutMenuHelper)} Init Failed");
                Plugin.Instance.Logger.Error(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static MemoryStream GetResourceStream(string resourceName)
        {
            var name = typeof(Plugin).Namespace + ".Web.Resources." + resourceName;
            var manifestResourceStream = typeof (ShortcutMenuHelper).GetTypeInfo().Assembly.GetManifestResourceStream(name);
            var destination = new MemoryStream((int) manifestResourceStream.Length);
            manifestResourceStream.CopyTo(destination);
            return destination;
        }

        private static void ModifyShortcutMenu(IServerConfigurationManager configurationManager)
        {
            string shortcutsJs;
            var shortcutsJsStream = GetResourceStream("shortcuts.js");
            shortcutsJsStream.Seek(0, SeekOrigin.Begin);
            using (var reader = new StreamReader(shortcutsJsStream))
            {
                shortcutsJs = reader.ReadToEnd();
            }

            var injectShortcutCommand = @"
const strmAssistantCommandSource = {
    getCommands: function(options) {
        const locale = this.globalize.getCurrentLocale().toLowerCase();
        const cjk = ['zh', 'ja', 'ko'].some(lang => locale.startsWith(lang));
        const lockCommandName = ({
            'zh-cn': '\u9501\u5B9A',
            'zh-hk': '\u9396\u5B9A',
            'zh-tw': '\u9396\u5B9A'
        }[locale] || 'Lock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));
        const unlockCommandName = ({
            'zh-cn': '\u89E3\u9501',
            'zh-hk': '\u89E3\u9396',
            'zh-tw': '\u89E3\u9396'
        }[locale] || 'Unlock') + (cjk ? this.globalize.translate('Metadata') : ' ' + this.globalize.translate('Metadata'));

        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType !== 'boxsets' && options.items[0].CollectionType !== 'playlists') {
            const commandName = (locale === 'zh-cn') ? '\u590D\u5236' : (['zh-hk', 'zh-tw'].includes(locale) ? '\u8907\u8F38' : 'Copy');
            return [{ name: commandName, id: 'copy', icon: 'content_copy' }];
        }
        if (options.items?.length === 1 && options.items[0].LibraryOptions && options.items[0].Type === 'VirtualFolder' &&
            options.items[0].CollectionType === 'boxsets') {
            const libraryId = options.items[0].Id || options.items[0].ItemId;
            const hidden = !!(window.strmAssistantHiddenBoxSets &&
                window.strmAssistantHiddenBoxSets[libraryId]);
            return [
                { name: this.globalize.translate('Remove'), id: 'remove', icon: 'remove_circle_outline' },
                {
                    name: hidden ? '@@SHOW_BOXSET@@' : '@@HIDE_BOXSET@@',
                    id: 'toggle_boxset_home',
                    icon: hidden ? 'visibility' : 'visibility_off'
                }
            ];
        }
        if (options.items?.length === 1) {
            const result = [];
            if (options.items[0].Type === 'Movie') {
                result.push({ name: this.globalize.translate('HeaderScanLibraryFiles'), id: 'traverse', icon: 'refresh' });
            }
            if ((options.items[0].Type === 'Movie' || options.items[0].Type === 'Episode') &&
                 options.items[0].CanDelete && options.mediaSourceId && options.items[0].MediaSources.length > 1) {
                result.push({
                    name: cjk
                        ? this.globalize.translate('Delete') + this.globalize.translate('Version')
                        : this.globalize.translate('Delete') + ' ' + this.globalize.translate('Version'),
                    id: 'delver_' + options.mediaSourceId,
                    icon: 'remove'
                });
            }
            if (options.items[0].hasOwnProperty('LockData') && options.items[0].Type !== 'CollectionFolder' &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                if (options.items[0].LockData) {
                    result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
                } else {
                    result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                }
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                const commandName = locale === 'zh-cn' ? '\u6E05\u9664\u7247\u5934\u6807\u8BB0' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7247\u982D\u6A19\u8A18' : 'Clear Intro Markers');
                result.push({ name: commandName, id: 'clear_intro', icon: 'clear_all' });
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                result.push({
                    name: options.items[0].Type === 'Series' ? '@@REFRESH_SERIES@@' : '@@REFRESH_SEASON@@',
                    id: 'refresh_recursive',
                    icon: 'refresh'
                });
            }
            if ((options.items[0].Type === 'Series' || options.items[0].Type === 'Season') &&
                (options.user && options.user.Policy.IsAdministrator || false) &&
                window.strmAssistantIntroRedetect && window.strmAssistantIntroRedetect.enabled) {
                result.push({
                    name: window.strmAssistantIntroRedetect.name,
                    id: 'redetect_intro',
                    icon: 'graphic_eq'
                });
            }
            if (['Movie', 'Episode', 'Series', 'Season', 'MusicVideo', 'Video', 'Trailer', 'BoxSet'].includes(options.items[0].Type) &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                const commandName = locale === 'zh-cn' ? '\u5220\u9664\u6F14\u5458' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u522A\u9664\u6F14\u54E1' : 'Remove Actor');
                result.push({ name: commandName, id: 'remove_person', icon: 'person_remove' });
            }
            if (['Movie', 'Episode', 'MusicVideo', 'Video', 'Trailer'].includes(options.items[0].Type) &&
                (options.user && options.user.Policy.IsAdministrator || false)) {
                const clearChapterImagesName = locale === 'zh-cn' ? '\u6E05\u9664\u7AE0\u8282\u56FE' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u7AE0\u7BC0\u5716' : 'Clear Chapter Images');
                const clearBifName = locale === 'zh-cn' ? '\u6E05\u9664 BIF \u63D0\u53D6' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664 BIF \u64F7\u53D6' : 'Clear BIF Thumbnails');
                const clearMediaInfoName = locale === 'zh-cn' ? '\u6E05\u9664\u5A92\u4F53\u4FE1\u606F' :
                    (['zh-hk', 'zh-tw'].includes(locale) ? '\u6E05\u9664\u5A92\u9AD4\u8CC7\u8A0A' : 'Clear Media Info');
                result.push({ name: clearChapterImagesName, id: 'clear_chapter_images', icon: 'image' });
                result.push({ name: clearBifName, id: 'clear_bif', icon: 'photo_library' });
                result.push({ name: clearMediaInfoName, id: 'clear_media_info', icon: 'info' });
            }
            return result;
        }
        if (!options.multiSelect && options.items?.length > 1 && options.items[0].Type !== 'CollectionFolder') {
            const multiUser = options.user || (options.users && Object.values(options.users)[0]);
            const isAdmin = !!(multiUser && multiUser.Policy && multiUser.Policy.IsAdministrator);
            const result = [];
            if (isAdmin && options.items.every(item => ['VirtualFolder', 'Movie'].includes(item.Type))) {
                result.push({
                    name: this.globalize.translate('HeaderScanLibraryFiles'),
                    id: 'traverse',
                    icon: 'refresh'
                });
            }
            if (isAdmin && options.items.every(item => item.hasOwnProperty('LockData') && item.Type !== 'VirtualFolder')) {
                result.push({ name: lockCommandName, id: 'lock', icon: 'lock' });
                result.push({ name: unlockCommandName, id: 'unlock', icon: 'lock_open' });
            }
            return result;
        }
        return [];
    },
    executeCommand: function(command, items) {
        if (!command || !items?.length) return;
        const actions = {
            copy: 'copy',
            remove: 'remove',
            traverse: 'traverse',
            lock: 'lock',
            unlock: 'unlock',
            clear_intro: 'clear_intro',
            remove_person: 'remove_person',
            refresh_recursive: 'refresh_recursive',
            redetect_intro: 'redetect_intro',
            clear_chapter_images: 'clear_chapter_images',
            clear_bif: 'clear_bif',
            clear_media_info: 'clear_media_info'
        };
        if (command.startsWith('delver_')) {
            const mediaSourceId = command.replace('delver_', '');
            const mediaSources = items[0].MediaSources || [];
            const matchingItem = mediaSources.find(source => source.Id === mediaSourceId);
            const itemId = matchingItem?.ItemId;
            const itemName = matchingItem?.Name;
            if (itemId && itemName) {
                return require(['components/strmassistant/strmassistant']).then(responses => {
                    return responses[0].delver(itemId, itemName, items[0].Type);
                });
            }
        }
        if (command === actions.lock || command === actions.unlock) {
            const lockData = command === actions.lock;
            return require(['components/strmassistant/strmassistant']).then(responses => {
                const promises = items.map(item => responses[0].lock(item.Id, lockData));
                return Promise.all(promises);
            });
        }
        if (command === 'toggle_boxset_home') {
            const libraryId = items[0].Id || items[0].ItemId;
            const hidden = !!(window.strmAssistantHiddenBoxSets &&
                window.strmAssistantHiddenBoxSets[libraryId]);
            return require(['components/strmassistant/strmassistant']).then(responses => {
                return responses[0].toggleBoxsetHome(libraryId, items[0].Name, !hidden);
            });
        }
        if (actions[command]) {
            return require(['components/strmassistant/strmassistant']).then(responses => {
                if (command === 'traverse') {
                    const targets = [];
                    items.forEach(item => {
                        const target = (item.Type === 'VirtualFolder' || item.Type === 'CollectionFolder')
                            ? item.Id
                            : item.ParentId;
                        if (target && targets.indexOf(target) === -1) targets.push(target);
                    });
                    if (!targets.length) return;
                    if (targets.length > 1 && typeof responses[0].traverseMany === 'function') {
                        return responses[0].traverseMany(targets);
                    }
                    return responses[0][actions[command]](targets[0]);
                }
                return responses[0][actions[command]](items[0].Id, items[0].Name);
            });
        }
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/globalize.js').then(globalize => {
        strmAssistantCommandSource.globalize = globalize;
        Emby.importModule('./modules/common/itemmanager/itemmanager.js').then(itemmanager => {
            itemmanager.registerCommandSource(strmAssistantCommandSource);
        });
    });
}, 3000);
    ".Replace("@@HIDE_BOXSET@@", UiEnhancementScript.JsString(
                    "BoxSetHomeVisibility_HideFromHome_Hide_from_Home", "Hide from Home"))
                .Replace("@@SHOW_BOXSET@@", UiEnhancementScript.JsString(
                    "BoxSetHomeVisibility_ShowOnHome_Show_on_Home", "Show on Home"))
                .Replace("@@REFRESH_SERIES@@", UiEnhancementScript.JsString(
                    "RecursiveRefresh_Series_Refresh_Seasons_and_Episodes", "Refresh Seasons and Episodes"))
                .Replace("@@REFRESH_SEASON@@", UiEnhancementScript.JsString(
                    "RecursiveRefresh_Season_Refresh_Season_Episodes", "Refresh Season Episodes"));

            var dataExplorer2Assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Emby.DataExplorer2");
            _shortcutsJs = shortcutsJs;
            _injectShortcutCommand = injectShortcutCommand;
            ModifiedShortcutsString = GetShortcutsScript();

            if (dataExplorer2Assembly != null)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"{nameof(ShortcutMenuHelper)} - Emby.DataExplorer2 plugin is installed");
                }

                var contextMenuHelperType = dataExplorer2Assembly.GetType("Emby.DataExplorer2.Api.ContextMenuHelper");
                var modifiedShortcutsProperty = contextMenuHelperType?.GetProperty("ModifiedShortcutsString",
                    BindingFlags.Static | BindingFlags.Public);
                var setMethod = modifiedShortcutsProperty?.GetSetMethod(true);

                if (setMethod != null)
                {
                    const string injectDataExplorerCommand = @"
const dataExplorerCommandSource = {
    getCommands(options) {
        const commands = [];
        if (options.items?.length === 1 && options.items[0].ProviderIds) {
            commands.push({
                name: 'Explore Item Data',
                id: 'dataexplorer',
                icon: 'manage_search'
            });
        }
        return commands;
    },
    executeCommand(command, items) {
        return require(['components/dataexplorer/dataexplorer']).then((responses) => {
            return responses[0].show(items[0].Id);
        });
    }
};

setTimeout(() => {
    Emby.importModule('./modules/common/itemmanager/itemmanager.js').then((itemmanager) => {
        itemmanager.registerCommandSource(dataExplorerCommandSource);
    });
}, 5000);
";
                    ModifiedShortcutsString += injectDataExplorerCommand;
                    setMethod.Invoke(null, new object[] { ModifiedShortcutsString });
                }
            }
        }
    }
}
