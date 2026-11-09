using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Reflection;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Settings of the Douban rating cache prebuild task. This section is mounted under
    /// MetadataEnhanceOptions next to the existing Douban options.
    /// </summary>
    public class DoubanCacheOptions : EditableOptionsBase
    {
        // The mount is MetadataEnhanceOptions.DoubanCacheOptions.
        private const string MountPropertyName = "DoubanCacheOptions";

        private static DoubanCacheOptions _defaultOptions;

        [DisplayNameL("DoubanCacheOptions_EditorTitle_Douban_Cache_Prebuild", typeof(Resources))]
        public override string EditorTitle => Resources.DoubanCacheOptions_EditorTitle_Douban_Cache_Prebuild;

        [DisplayNameL("DoubanCacheOptions_EnableDoubanCachePrebuild_Enable_Douban_Cache_Prebuild",
            typeof(Resources))]
        [DescriptionL(
            "DoubanCacheOptions_EnableDoubanCachePrebuild_Prefetch_Douban_ratings_in_the_background_so_the_detail_page_badge_appears_without_waiting_for_the_first_Douban_request__Only_items_without_a_cached_rating_are_requested__Default_is_OFF_",
            typeof(Resources))]
        public bool EnableDoubanCachePrebuild { get; set; } = false;

        [DisplayNameL("DoubanCacheOptions_ExecutionIntervalHours_Execution_Interval__Hours_", typeof(Resources))]
        [DescriptionL(
            "DoubanCacheOptions_ExecutionIntervalHours_Default_trigger_interval_of_the_prebuild_task__The_interval_is_only_applied_when_Emby_creates_the_default_trigger__It_can_be_changed_afterwards_in_the_scheduled_task_settings__Default_is_24_",
            typeof(Resources))]
        [Required, MinValue(1), MaxValue(168)]
        [VisibleCondition(nameof(EnableDoubanCachePrebuild), SimpleCondition.IsTrue)]
        public int ExecutionIntervalHours { get; set; } = 24;

        [DisplayNameL("DoubanCacheOptions_BatchSize_Batch_Size", typeof(Resources))]
        [DescriptionL(
            "DoubanCacheOptions_BatchSize_Number_of_items_processed_between_progress_updates__Smaller_batches_report_progress_more_often__Default_is_50_",
            typeof(Resources))]
        [Required, MinValue(1), MaxValue(500)]
        [VisibleCondition(nameof(EnableDoubanCachePrebuild), SimpleCondition.IsTrue)]
        public int BatchSize { get; set; } = 50;

        [DisplayNameL("DoubanCacheOptions_RequestIntervalSeconds_Request_Interval__Seconds_", typeof(Resources))]
        [DescriptionL(
            "DoubanCacheOptions_RequestIntervalSeconds_Minimum_interval_between_Douban_requests__One_item_may_need_a_search_and_a_detail_request__It_must_be_between_2_and_30__Default_is_3_",
            typeof(Resources))]
        [Required, MinValue(2), MaxValue(30)]
        [VisibleCondition(nameof(EnableDoubanCachePrebuild), SimpleCondition.IsTrue)]
        public int RequestIntervalSeconds { get; set; } = 3;

        public int GetRequestIntervalSeconds()
        {
            return Math.Max(2, Math.Min(30, RequestIntervalSeconds));
        }

        /// <summary>
        /// Returns the live settings section, or defaults when the plugin options are unavailable.
        /// </summary>
        internal static DoubanCacheOptions GetEffective()
        {
            try
            {
                var enhanceOptions = Plugin.Instance?.GetPluginOptions()?.MetadataEnhanceOptions;
                var property = enhanceOptions?.GetType()
                    .GetProperty(MountPropertyName, BindingFlags.Public | BindingFlags.Instance);

                if (property?.GetValue(enhanceOptions) is DoubanCacheOptions options) return options;
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("DoubanCachePrebuild - Options lookup failed: " + e.Message);
            }

            return _defaultOptions ?? (_defaultOptions = new DoubanCacheOptions());
        }
    }
}
