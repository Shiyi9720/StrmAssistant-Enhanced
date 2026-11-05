using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
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

    public enum ChineseMetadataOrderOption
    {
        [DescriptionL("ChineseMetadataOrderOption_LibraryDefault_Library_Default", typeof(Resources))]
        LibraryDefault,
        [DescriptionL("ChineseMetadataOrderOption_TraditionalFirst_Traditional_Chinese_First__zh_TW_", typeof(Resources))]
        TraditionalFirst,
        [DescriptionL("ChineseMetadataOrderOption_SimplifiedFirst_Simplified_Chinese_First__zh_CN_", typeof(Resources))]
        SimplifiedFirst
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

        [DisplayNameL("MetadataEnhanceOptions_EnableDoubanSupplement_Enable_Douban_Supplement", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EnableDoubanSupplement_When_TMDB_has_no_Chinese_overview_or_poster__try_to_fill_the_missing_values_from_Douban__Existing_values_are_never_overwritten__Default_is_OFF_", typeof(Resources))]
        public bool EnableDoubanSupplement { get; set; } = false;

        [DisplayNameL("MetadataEnhanceOptions_DoubanRequestIntervalSeconds_Douban_Request_Interval__Seconds_", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_DoubanRequestIntervalSeconds_Minimum_interval_between_Douban_requests__Douban_rate_limits_aggressively__It_must_be_between_2_and_30__Default_is_3_", typeof(Resources))]
        [Required, MinValue(2), MaxValue(30)]
        [VisibleCondition(nameof(EnableDoubanSupplement), SimpleCondition.IsTrue)]
        public int DoubanRequestIntervalSeconds { get; set; } = 3;

        [Browsable(false)]
        [Required]
        public string RefreshPersonMode { get; set; } = RefreshPersonOption.Default.ToString();

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
    }
}
