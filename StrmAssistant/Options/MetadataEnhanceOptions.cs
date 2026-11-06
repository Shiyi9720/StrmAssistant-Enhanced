using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public enum RefreshPersonOption
    {
        Default,
        FullRefresh,
        NoAdult
    }

    public enum RefreshPersonModeOption
    {
        [DescriptionL("RefreshPersonModeOption_Auto_Automatic", typeof(Resources))]
        Auto,

        [DescriptionL("RefreshPersonModeOption_Manual_Manual__Full_Refresh_", typeof(Resources))]
        Manual,

        [DescriptionL("RefreshPersonModeOption_Off_Off", typeof(Resources))]
        Off
    }

    public enum RefreshPersonScopeOption
    {
        [DescriptionL("RefreshPersonScopeOption_FollowRefreshMode_Follow_Refresh_Mode", typeof(Resources))]
        FollowRefreshMode,

        [DescriptionL("RefreshPersonScopeOption_MissingChineseName_Missing_Chinese_Name_Only", typeof(Resources))]
        MissingChineseName,

        [DescriptionL("RefreshPersonScopeOption_AllPersons_All_Persons", typeof(Resources))]
        AllPersons,

        [DescriptionL("RefreshPersonScopeOption_MainCast_Main_Cast_Only", typeof(Resources))]
        MainCast
    }

    public enum ChineseMetadataOrderOption
    {
        [DescriptionL("ChineseMetadataOrderOption_LibraryDefault_Library_Default", typeof(Resources))]
        LibraryDefault,
        [DescriptionL("ChineseMetadataOrderOption_TraditionalFirst_Traditional_Chinese_First__zh_TW_", typeof(Resources))]
        TraditionalFirst,
        [DescriptionL("ChineseMetadataOrderOption_SimplifiedFirst_Simplified_Chinese_First__zh_CN_", typeof(Resources))]
        SimplifiedFirst
    }

    public enum MovieMetadataRefreshMode
    {
        [DescriptionL("MovieMetadataRefreshMode_FullRefresh_Full_Refresh", typeof(Resources))]
        FullRefresh,
        [DescriptionL("MovieMetadataRefreshMode_MissingFieldsOnly_Missing_Fields_Only", typeof(Resources))]
        MissingFieldsOnly,
        [DescriptionL("MovieMetadataRefreshMode_NeverOverwriteExisting_Never_Overwrite_Existing", typeof(Resources))]
        NeverOverwriteExisting
    }

    public class MetadataEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_MetadataEnhanceOptions_Metadata_Enhance;

        [DisplayNameL("MetadataEnhanceOptions_NewLibraryMovieDb_Prefer_TMDB_For_New_Library", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_NewLibraryMovieDb_Automatically_prefer_TheMovieDb_over_TheTVDB_as_the_metadata_and_image_fetcher_for_newly_created_libraries__Default_is_OFF_", typeof(Resources))]
        public bool NewLibraryMovieDb { get; set; } = false;

        // Superseded by ChineseMetadataOrder. Kept so existing configurations keep their
        // behaviour after upgrading.
        [Browsable(false)]
        [DisplayNameL("MetadataEnhanceOptions_PreferTraditionalChinese_Prefer_Traditional_Chinese", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_PreferTraditionalChinese_Use_Traditional_Chinese_metadata_from_TMDB_when_Chinese_metadata_is_requested__The_language_chain_is_zh_Hant__zh_TW__then_zh_CN__Default_is_OFF_", typeof(Resources))]
        public bool PreferTraditionalChinese { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_ChineseMetadataOrder_Chinese_Metadata_Language_Order", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ChineseMetadataOrder_Chinese_metadata_and_images_are_requested_in_this_language__Library_Default_keeps_the_library_language__Traditional_First_requests_zh_TW_and_prefers_zh_TW_or_zh_HK_images__Simplified_First_requests_zh_CN_and_prefers_zh_CN_images__Default_is_Library_Default_", typeof(Resources))]
        public ChineseMetadataOrderOption ChineseMetadataOrder { get; set; } = ChineseMetadataOrderOption.LibraryDefault;

        [DisplayNameL("MetadataEnhanceOptions_ConvertSimplifiedToTraditional_Convert_Simplified_Chinese_to_Traditional", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_ConvertSimplifiedToTraditional_Convert_simplified_Chinese_metadata_fetched_from_TMDB_to_traditional_Chinese__Default_is_OFF_", typeof(Resources))]
        public bool ConvertSimplifiedToTraditional { get; set; } = false;

        public ChineseMetadataOrderOption GetEffectiveChineseMetadataOrder()
        {
            return PreferTraditionalChinese
                ? ChineseMetadataOrderOption.TraditionalFirst
                : ChineseMetadataOrder;
        }

        [DisplayNameL("MetadataEnhanceOptions_DistinguishChinesePosters_Distinguish_Simplified_and_Traditional_Chinese_Images", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DistinguishChinesePosters_Request_only_the_matching_Chinese_image_set_for_each_item__zh_CN_content_gets_zh_CN_posters_and_logos_while_zh_TW_and_zh_HK_content_gets_zh_TW_and_zh_HK_images__The_item_preferred_image_language_or_library_metadata_language_decides__Default_is_OFF_", typeof(Resources))]
        public bool DistinguishChinesePosters { get; set; } = false;

        [DisplayNameL("ModOptions_OriginalPoster_Original_Poster", typeof(Resources))]
        [DescriptionL("ModOptions_OriginalPoster_Show_original_poster_based_on_original_language__Default_is_OFF_", typeof(Resources))]
        public bool PreferOriginalPoster { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_EnableLocalMovieDb_Local_MovieDb_Source", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableLocalMovieDb_Route_all_TMDB_api_metadata_search_and_image_requests_through_one_local_mirror_or_reverse_proxy__It_takes_precedence_over_the_alternative_MovieDb_options_below__Default_is_OFF_", typeof(Resources))]
        public bool EnableLocalMovieDb { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_LocalMovieDbUrl_Local_MovieDb_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_LocalMovieDbUrl_Base_address_of_the_local_TMDB_mirror__for_example_http___192_168_1_10_8080__Both_api_and_image_requests_are_rewritten_to_this_address__Default_is_EMPTY_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMovieDb), SimpleCondition.IsTrue)]
        public string LocalMovieDbUrl { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_LocalMovieDbApiKey_Local_MovieDb_Api_Key", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_LocalMovieDbApiKey_Optional_api_key_sent_to_the_local_mirror__Blank_uses_the_alternative_or_system_default_key_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMovieDb), SimpleCondition.IsTrue)]
        public string LocalMovieDbApiKey { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_EnableAltMovieDbUrl", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableAltMovieDbUrl_Legacy_alternative_MovieDb_source__kept_for_compatibility__Use_the_local_MovieDb_source_above_to_configure_api_and_image_addresses_together_", typeof(Resources))]
        public bool EnableAltMovieDbUrl { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbApiUrl_Alternative_MovieDb_Api_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbApiUrl_Default_alternative_is_https___api_tmdb_org", typeof(Resources))]
        [VisibleCondition(nameof(EnableAltMovieDbUrl), SimpleCondition.IsTrue)]
        public string AltMovieDbApiUrl { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbImageUrl_Alternative_MovieDb_Image_Url", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbImageUrl_No_default_alternative__Provide_by_yourself_", typeof(Resources))]
        [VisibleCondition(nameof(EnableAltMovieDbUrl), SimpleCondition.IsTrue)]
        public string AltMovieDbImageUrl { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_AltMovieDbApiKey_Alternative_MovieDb_Api_Key", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_AltMovieDbApiKey_Provide_your_own_MovieDb_Api_Key__Blank_uses_system_default_", typeof(Resources))]
        [VisibleCondition(nameof(EnableAltMovieDbUrl), SimpleCondition.IsTrue)]
        public string AltMovieDbApiKey { get; set; } = string.Empty;

        [DisplayNameL("MetadataEnhanceOptions_EnableMovieDbCache_Enable_TMDB_Disk_Cache", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableMovieDbCache_Cache_TMDB_search_and_detail_json_responses_on_disk_so_transient_network_errors_and_rate_limits_do_not_break_scraping__Only_GET_responses_are_cached__Default_is_OFF_", typeof(Resources))]
        public bool EnableMovieDbCache { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_MovieDbCacheTtlDays_Cache_TTL__Days_", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MovieDbCacheTtlDays_Number_of_days_a_cached_TMDB_response_stays_fresh__It_must_be_between_1_and_365__Default_is_30_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(365)]
        [VisibleCondition(nameof(EnableMovieDbCache), SimpleCondition.IsTrue)]
        public int MovieDbCacheTtlDays { get; set; } = 30;

        [DisplayNameL("MetadataEnhanceOptions_MovieDbCacheMaxSizeMb_Maximum_Cache_Size__MB_", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MovieDbCacheMaxSizeMb_Upper_limit_of_the_TMDB_disk_cache__Least_recently_used_entries_are_evicted_when_it_is_exceeded__It_must_be_between_16_and_4096__Default_is_256_", typeof(Resources))]
        [Required, MinValue(16), MaxValue(4096)]
        [VisibleCondition(nameof(EnableMovieDbCache), SimpleCondition.IsTrue)]
        public int MovieDbCacheMaxSizeMb { get; set; } = 256;

        [DisplayNameL("MetadataEnhanceOptions_EnableDoubanSupplement_Enable_Douban_Supplement", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableDoubanSupplement_When_TMDB_has_no_Chinese_overview_or_poster__try_to_fill_the_missing_values_from_Douban__Existing_values_are_never_overwritten__Default_is_OFF_", typeof(Resources))]
        public bool EnableDoubanSupplement { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanRequestIntervalSeconds_Douban_Request_Interval__Seconds_", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanRequestIntervalSeconds_Minimum_interval_between_Douban_requests__Douban_rate_limits_aggressively__It_must_be_between_2_and_30__Default_is_3_", typeof(Resources))]
        [Required, MinValue(2), MaxValue(30)]
        [VisibleCondition(nameof(EnableDoubanSupplement), SimpleCondition.IsTrue)]
        public int DoubanRequestIntervalSeconds { get; set; } = 3;

        [DisplayNameL("MetadataEnhanceOptions_EnableDoubanCast_Enable_Douban_Cast_Supplement", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableDoubanCast_Append_missing_main_cast_and_directors_from_Douban__Existing_people_are_kept_and_deduplicated_by_name__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanSupplement), SimpleCondition.IsTrue)]
        public bool EnableDoubanCast { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_EnableDoubanSeason_Enable_Douban_Season_Supplement", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableDoubanSeason_Fill_missing_season_and_episode_information_for_series_from_Douban__Existing_values_are_never_overwritten__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableDoubanSupplement), SimpleCondition.IsTrue)]
        public bool EnableDoubanSeason { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_EnableDoubanRecommendations_Douban_Recommendation_Channel", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableDoubanRecommendations_Show_Douban_hot_movie_recommendations_on_the_home_screen__Uses_Douban_s_public_mobile_API_and_degrades_silently__Default_is_OFF_", typeof(Resources))]
        public bool EnableDoubanRecommendations { get; set; } = false;

        [Browsable(false)]
        public List<EditorRadioOption> RefreshPersonModeList { get; set; } = new List<EditorRadioOption>();

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonMode_Refresh_Chinese_Actor", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonMode_Automatic_only_runs_when_the_server_preferred_metadata_language_is_Chinese_and_skips_recently_refreshed_persons__Manual_always_performs_a_full_refresh_when_triggered_even_when_the_server_language_is_not_Chinese__Off_disables_the_task_", typeof(Resources))]
        [Required]
        [SelectItemsSource(nameof(RefreshPersonModeList))]
        [SelectShowRadioGroup]
        public string RefreshPersonMode { get; set; } = RefreshPersonModeOption.Auto.ToString();

        public RefreshPersonModeOption GetEffectiveRefreshPersonMode()
        {
            if (Enum.TryParse(RefreshPersonMode, true, out RefreshPersonModeOption mode) &&
                Enum.IsDefined(typeof(RefreshPersonModeOption), mode))
            {
                return mode;
            }

            if (!string.IsNullOrEmpty(RefreshPersonMode) &&
                RefreshPersonMode.IndexOf(nameof(RefreshPersonOption.FullRefresh),
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return RefreshPersonModeOption.Manual;
            }

            return RefreshPersonModeOption.Auto;
        }

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonScope_Refresh_Scope", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonScope_Follow_Refresh_Mode_keeps_the_existing_Auto_and_Manual_behaviour__Missing_Chinese_Name_Only_processes_persons_without_a_Chinese_name__All_Persons_processes_every_person__Main_Cast_Only_limits_the_task_to_actors_in_the_main_cast_lists__Default_is_Follow_Refresh_Mode_", typeof(Resources))]
        public RefreshPersonScopeOption RefreshPersonScope { get; set; } =
            RefreshPersonScopeOption.FollowRefreshMode;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonSkipLockedPeople_Skip_Locked_Persons", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonSkipLockedPeople_Skip_persons_marked_as_locked__Default_is_ON_", typeof(Resources))]
        public bool RefreshPersonSkipLockedPeople { get; set; } = true;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonSkipMissingTmdbId_Skip_Persons_Without_TMDB_Id", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonSkipMissingTmdbId_Skip_persons_that_have_no_TMDB_person_id__Default_is_ON_", typeof(Resources))]
        public bool RefreshPersonSkipMissingTmdbId { get; set; } = true;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonSkipRecentlyRefreshed_Skip_Recently_Refreshed_Persons", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonSkipRecentlyRefreshed_Skip_persons_that_were_refreshed_within_the_configured_number_of_days__Default_is_ON_", typeof(Resources))]
        public bool RefreshPersonSkipRecentlyRefreshed { get; set; } = true;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonRecentRefreshDays_Skip_Recently_Refreshed_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonRecentRefreshDays_Number_of_days_used_by_the_recently_refreshed_skip_rule__Default_is_30_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(3650)]
        [VisibleCondition(nameof(RefreshPersonSkipRecentlyRefreshed), SimpleCondition.IsTrue)]
        public int RefreshPersonRecentRefreshDays { get; set; } = 30;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonRequireChineseLanguage_Require_Chinese_Server_Language", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonRequireChineseLanguage_Require_the_server_preferred_metadata_language_to_be_Chinese_for_automatic_runs__Manual_runs_can_always_be_triggered__Default_is_ON_", typeof(Resources))]
        public bool RefreshPersonRequireChineseLanguage { get; set; } = true;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonMaxConcurrentCount_Max_Concurrent_Count", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonMaxConcurrentCount_Number_of_persons_refreshed_in_parallel__It_must_be_between_1_and_4__Default_is_1_", typeof(Resources))]
        [Required, MinValue(1), MaxValue(4)]
        public int RefreshPersonMaxConcurrentCount { get; set; } = 1;

        [DisplayNameL("MetadataEnhanceOptions_RefreshPersonRequestIntervalMs_Request_Interval__Milliseconds_", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_RefreshPersonRequestIntervalMs_Minimum_interval_between_TMDB_requests_in_milliseconds__It_must_be_between_0_and_10000__Default_is_250_", typeof(Resources))]
        [Required, MinValue(0), MaxValue(10000)]
        public int RefreshPersonRequestIntervalMs { get; set; } = 250;

        public enum EpisodeRefreshOption
        {
            [DescriptionL("EpisodeRefreshOption_NoOverview_No_Overview", typeof(Resources))]
            NoOverview,
            [DescriptionL("EpisodeRefreshOption_NoImage_No_Image", typeof(Resources))]
            NoImage,
            [DescriptionL("EpisodeRefreshOption_NonChineseOverview_Non_Chinese_Overview", typeof(Resources))]
            NonChineseOverview,
            [DescriptionL("EpisodeRefreshOption_DefaultEpisodeName_Default_Episode_Name", typeof(Resources))]
            DefaultEpisodeName,
            [DescriptionL("EpisodeRefreshOption_ReplaceCapturedImage_Replace_Captured_Image", typeof(Resources))]
            ReplaceCapturedImage
        }

        [Browsable(false)]
        public List<EditorSelectOption> EpisodeRefreshOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_Metadata_Refresh_Scope", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_refresh_scope_for_scheduled_task_and_catch_up__Default_is_no_overview_and_no_image_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(EpisodeRefreshOptionList))]
        public string EpisodeRefreshScope { get; set; } = string.Join(",", EpisodeRefreshOption.NoOverview.ToString(),
            EpisodeRefreshOption.NoImage.ToString());
        
        [DisplayNameL("MetadataEnhanceOptions_EpisodeRefreshLookBackDays_Episode_Refresh_Lookback_Days", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EpisodeRefreshLookbackDays_Episode_metadata_refresh_lookback_days__Default_is_365_", typeof(Resources))]
        [Required, MinValue(1)]
        public int EpisodeRefreshLookbackDays { get; set; } = 365;

        [DisplayNameL("MetadataEnhanceOptions_MovieRefreshMode_Movie_Metadata_Refresh_Mode", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_MovieRefreshMode_Choose_how_existing_movies_are_refreshed__Full_Refresh_replaces_everything_as_before__Missing_Fields_Only_skips_complete_movies_and_fills_gaps__Never_Overwrite_Existing_queries_the_provider_but_keeps_current_values__Default_is_Full_Refresh_", typeof(Resources))]
        public MovieMetadataRefreshMode MovieRefreshMode { get; set; } = MovieMetadataRefreshMode.FullRefresh;
    }
}
