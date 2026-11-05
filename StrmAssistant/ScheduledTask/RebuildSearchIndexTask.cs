using MediaBrowser.Model.Tasks;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.ScheduledTask
{
    /// <summary>
    /// Exposes the Chinese search index build as a normal Emby task so that it can be triggered
    /// from the dashboard (including the plugin's floating task panel) and reports progress.
    /// </summary>
    public class RebuildSearchIndexTask : IScheduledTask
    {
        public string Name => Resources.ResourceManager.GetString(
            "RebuildSearchIndexTask_Name_Rebuild_Search_Index", Plugin.Instance.DefaultUICulture);

        public string Key => "RebuildSearchIndexTask";

        public string Description => Resources.ResourceManager.GetString(
            "RebuildSearchIndexTask_Description_Rebuilds_the_Chinese_search_index_used_for_pinyin__simplified_traditional_and_alias_matching",
            Plugin.Instance.DefaultUICulture);

        public string Category => Resources.ResourceManager.GetString("PluginOptions_EditorTitle_Strm_Assistant",
            Plugin.Instance.DefaultUICulture);

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (!Plugin.Instance.GetPluginOptions().SearchOptions.EnableChineseSearch)
            {
                progress?.Report(100);
                return;
            }

            await Plugin.SearchIndexService.BuildAsync(progress, cancellationToken).ConfigureAwait(false);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }
    }
}
