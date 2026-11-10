using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
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

        [DisplayNameL("ExperienceEnhanceOptions_MergeSeriesPreferences_Series_Merge_Preference", typeof(Resources))]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeSeriesScopeOption MergeSeriesPreference { get; set; } = MergeSeriesScopeOption.LibraryScope;

        [DisplayNameL("VersionFavoriteOptions_EditorTitle_Cross_Library_Favorites", typeof(Resources))]
        public VersionFavoriteOptions VersionFavoriteOptions { get; set; } = new VersionFavoriteOptions();

        [DisplayNameL("VersionPlaybackOptions_EditorTitle_Version_Playback", typeof(Resources))]
        public VersionPlaybackOptions VersionPlaybackOptions { get; set; } = new VersionPlaybackOptions();

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

        [DisplayNameL("UIFunctionOptions_BeautifyMissingMetadata_Beautify_Missing_Metadata", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_BeautifyMissingMetadata_Beautify_missing_metadata_for_episode_display__Default_is_OFF_", typeof(Resources))]
        public bool BeautifyMissingMetadata { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryScope_TV_shows_library_scope_to_detect__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(BeautifyMissingMetadataLibraryList))]
        [VisibleCondition(nameof(BeautifyMissingMetadata), SimpleCondition.IsTrue)]
        public string BeautifyMissingMetadataLibraryScope { get; set; } = string.Empty;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> BeautifyMissingMetadataLibraryList { get; set; }

        [DisplayNameL("UIFunctionOptions_EnhanceMissingEpisodes_Missing_Episodes_Enhanced", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_EnhanceMissingEpisodes_Add_MovieDb_support_for_the_missing_episodes_function__Default_is_OFF_", typeof(Resources))]
        public bool EnhanceMissingEpisodes { get; set; } = false;

        [DisplayNameL("UIFunctionOptions_NoBoxsetsAutoCreation_Prevent_Boxsets_Auto_Creation", typeof(Resources))]
        [DescriptionL("UIFunctionOptions_NoBoxsetsAutoCreation_Prevent_automatic_boxset_library_creation__Default_is_OFF_", typeof(Resources))]
        public bool NoBoxsetsAutoCreation { get; set; } = false;

        [DisplayNameL("ModOptions_HidePersonNoImage_Hide_Person_without_Image", typeof(Resources))]
        [DescriptionL("ModOptions_HidePersonNoImage_Hide_person_without_image__Default_is_OFF_", typeof(Resources))]
        public bool HidePersonNoImage { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_SuppressPluginUpdates_Suppress_Auto_Plugin_Updates", typeof(Resources))]
        public bool SuppressPluginUpdates { get; set; } = false;

        [DescriptionL("ExperienceEnhanceOptions_SuppressPluginUpdates_Plugin_names_separated_by_comma_or_semicolon_like_MovieDb_Tvdb__Default_is_BLANK_", typeof(Resources))]
        [VisibleCondition(nameof(SuppressPluginUpdates), SimpleCondition.IsTrue)]
        public string SuppressPluginUpdatePlugins { get; set; } = string.Empty;
    }
}
