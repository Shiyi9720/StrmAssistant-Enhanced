using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;
        
        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMoviesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }
        
        [DisplayName("")]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMoviesScopeOption MergeMoviesPreference { get; set; } = MergeMoviesScopeOption.LibraryScope;

        public enum MergeSeriesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [Browsable(false)]
        public MergeSeriesScopeOption MergeSeriesPreference => MergeSeriesScopeOption.LibraryScope;

        [DisplayNameL("VersionFavoriteOptions_EditorTitle_Cross_Library_Favorites", typeof(Resources))]
        public VersionFavoriteOptions VersionFavoriteOptions { get; set; } = new VersionFavoriteOptions();

        [DisplayNameL("ExperienceEnhanceOptions_AutoMarkPreviousEpisodesPlayed_Auto_Mark_Previous_Episodes_Played", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_AutoMarkPreviousEpisodesPlayed_Mark_previous_episodes_in_the_same_season_as_played_when_the_current_episode_finishes__Default_is_OFF_", typeof(Resources))]
        public bool AutoMarkPreviousEpisodesPlayed { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_ReverseSeasonEpisodeOrder_Reverse_Season_and_Episode_Order", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_ReverseSeasonEpisodeOrder_Show_seasons_and_episodes_in_descending_order_in_the_web_client__Default_is_OFF_", typeof(Resources))]
        public bool ReverseSeasonEpisodeOrder { get; set; } = false;

        public enum EpisodeDisplayStyleOption
        {
            [DescriptionL("EpisodeDisplayStyleOption_Default_Default", typeof(Resources))]
            Default,

            [DescriptionL("EpisodeDisplayStyleOption_SeasonEpisodeNumber_SxxExx", typeof(Resources))]
            SeasonEpisodeNumber,

            [DescriptionL("EpisodeDisplayStyleOption_EpisodeName_Episode_Name", typeof(Resources))]
            EpisodeName
        }

        [DisplayNameL("ExperienceEnhanceOptions_EpisodeDisplayStyle_Episode_Display_Style", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EpisodeDisplayStyle_Force_a_consistent_episode_title_style_in_the_web_client__Default_is_Default_", typeof(Resources))]
        public EpisodeDisplayStyleOption EpisodeDisplayStyle { get; set; } = EpisodeDisplayStyleOption.Default;

        [DisplayNameL("ExperienceEnhanceOptions_EnhanceNotification_Enhance_Notification", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EnhanceNotification_Show_episode_details_in_series_notification__Default_is_OFF_", typeof(Resources))]
        public bool EnhanceNotification { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_NaturalSort_Natural_Sort", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_NaturalSort_Sort_titles_with_a_locale_aware_natural_collation_so_numbers_are_compared_by_value_and_Chinese_titles_keep_their_reading_order__Default_is_OFF_", typeof(Resources))]
        public bool NaturalSort { get; set; } = false;

        [DisplayNameL("PinyinSortOptions_EditorTitle_Pinyin_Sort_Title", typeof(Resources))]
        public PinyinSortOptions PinyinSortOptions { get; set; } = new PinyinSortOptions();

        [DisplayNameL("UIFunctionOptions_EnforceLibraryOrder_Enforce_Library_Order", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_EnforceLibraryOrder_Enforce_library_order_per_the_first_admin__Default_if_OFF_", typeof(Resources))]
        public bool EnforceLibraryOrder { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_LogNewestFirst_Logs_Newest_First", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_LogNewestFirst_Show_log_entries_from_newest_to_oldest_in_the_web_client_log_viewer__Default_is_OFF_", typeof(Resources))]
        public bool LogNewestFirst { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_MultiSelectActions_Multi_Select_Actions", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_MultiSelectActions_Enable_multi_select_actions_on_season_pages__delete__download_and_convert__and_allow_selecting_multiple_libraries_to_scan_library_files__Default_is_OFF_", typeof(Resources))]
        public bool MultiSelectActions { get; set; } = false;
    }
}
