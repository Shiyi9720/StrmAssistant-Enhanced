using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class GeneralOptions : EditableOptionsBase
    {
        [DisplayNameL("GeneralOptions_EditorTitle_General_Options", typeof(Resources))]
        public override string EditorTitle => Resources.GeneralOptions_EditorTitle_General_Options;

        [DisplayNameL("PluginOptions_CatchupMode_Catch_up_Mode__Experimental_", typeof(Resources))]
        [DescriptionL("PluginOptions_CatchupMode_Catch_up_users_favorites__exclusive_to_Strm___Default_is_False_", typeof(Resources))]
        [Required]
        public bool CatchupMode { get; set; } = false;

        public enum CatchupTask
        {
            [DescriptionL("CatchupTask_MediaInfo_MediaInfo", typeof(Resources))]
            MediaInfo,
            [DescriptionL("CatchupTask_Fingerprint_Fingerprint", typeof(Resources))]
            Fingerprint,
            [DescriptionL("CatchupTask_IntroSkip_IntroSkip", typeof(Resources))]
            IntroSkip,
            [DescriptionL("CatchupTask_EpisodeRefresh_EpisodeRefresh", typeof(Resources))]
            EpisodeRefresh
        }
        
        [Browsable(false)]
        public IEnumerable<EditorSelectOption> CatchupTaskList { get; set; }

        [DisplayNameL("GeneralOptions_CatchupScope_Catchup_Scope", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(CatchupTaskList))]
        [VisibleCondition(nameof(CatchupMode), SimpleCondition.IsTrue)]
        public string CatchupTaskScope { get; set; } = CatchupTask.MediaInfo.ToString();

        [DisplayNameL("GeneralOptions_CatchupMinimumDelaySeconds_Catchup_Minimum_Delay__Seconds_", typeof(Resources))]
        [DescriptionL("GeneralOptions_CatchupMinimumDelaySeconds_Minimum_seconds_between_two_catch_up_queue_runs__Favorites_are_processed_in_their_own_queue__Default_is_30_", typeof(Resources))]
        [VisibleCondition(nameof(CatchupMode), SimpleCondition.IsTrue)]
        [Required, MinValue(1), MaxValue(3600)]
        public int CatchupMinimumDelaySeconds { get; set; } = 30;

        [DisplayNameL("GeneralOptions_EnableFavoriteCatchupPriority_Favorite_Catchup_Priority_Queue", typeof(Resources))]
        [DescriptionL("GeneralOptions_EnableFavoriteCatchupPriority_Put_catch_up_work_triggered_by_favorites_into_a_separate_queue_that_is_drained_before_the_regular_queue__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(CatchupMode), SimpleCondition.IsTrue)]
        public bool EnableFavoriteCatchupPriority { get; set; } = true; = false;

        [DisplayNameL("PluginOptions_MaxConcurrentCount_Max_Concurrent_Count", typeof(Resources))]
        [DescriptionL("PluginOptions_MaxConcurrentCount_Max_Concurrent_Count_must_be_between_1_to_10__Default_is_1_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(20)]
        public int MaxConcurrentCount { get; set; } = 1;

        [DisplayNameL("GeneralOptions_CooldownSeconds_Cooldown_Time__Seconds___Default_is_0", typeof(Resources))]
        [DescriptionL("GeneralOptions_CooldownDurationSeconds_Applicable_to_single_thread_mode__Default_is_0_", typeof(Resources))]
        [VisibleCondition(nameof(MaxConcurrentCount), ValueCondition.IsEqual, 1)]
        [Required, MinValue(0), MaxValue(60)]
        public int CooldownDurationSeconds { get; set; } = 0;

        [DisplayNameL("GeneralOptions_IntroDetectionMaxConcurrentCount_Intro_Detection_Max_Concurrent_Count", typeof(Resources))]
        [DescriptionL("GeneralOptions_IntroDetectionMaxConcurrentCount_Number_of_episodes_processed_in_parallel__It_must_be_between_1_and_8__Default_is_2_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(8)]
        public int IntroDetectionMaxConcurrentCount { get; set; } = 2;

        [DisplayNameL("GeneralOptions_Tier2MaxConcurrentCount_Tier_2_Max_Concurrent_Count", typeof(Resources))]
        [DescriptionL("GeneralOptions_Tier2MaxConcurrentCount_Refresh_metadata__subtitle__local_tasks__Default_is_1_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(20)]
        public int Tier2MaxConcurrentCount { get; set; } = 1;

        [DisplayNameL("GeneralOptions_MaxMultiVersionMergeCount_Max_Multi_Version_Merge_Count", typeof(Resources))]
        [DescriptionL("GeneralOptions_MaxMultiVersionMergeCount_Max_number_of_versions_to_merge_in_the_same_folder__It_must_be_between_2_and_64__Default_is_8_", typeof(Resources))]
        [Required, MinValue(2), MaxValue(64)]
        public int MaxMultiVersionMergeCount { get; set; } = 8;

        [DisplayNameL("GeneralOptions_MultiVersionSort_Multi_Version_Smart_Sort", typeof(Resources))]
        [DescriptionL("GeneralOptions_MultiVersionSort_Sort_versions_by_quality__resolution__codec__size___Default_is_ON_", typeof(Resources))]
        public bool MultiVersionSort { get; set; } = true;

        [DisplayNameL("GeneralOptions_MultiVersionFriendlyNames_Multi_Version_Friendly_File_Names", typeof(Resources))]
        [DescriptionL("GeneralOptions_MultiVersionFriendlyNames_Show_beautified_file_names_with_highlighted_resolution__source_and_codec_in_the_multi_version_list__Default_is_ON_", typeof(Resources))]
        public bool MultiVersionFriendlyNames { get; set; } = true;
    }
}
