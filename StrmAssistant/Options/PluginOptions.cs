using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using Emby.Web.GenericEdit.Elements.List;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Common;
using StrmAssistant.Properties;
using System;
using System.ComponentModel;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;

namespace StrmAssistant.Options
{
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Assistant;

        public override string EditorDescription => string.Empty;
        
        public GenericItemList Disclaimer { get; set; } = new GenericItemList();

        [VisibleCondition(nameof(ShowConflictPluginLoadedStatus), SimpleCondition.IsTrue)]
        public StatusItem ConflictPluginLoadedStatus { get; set; } = new StatusItem();

        [VisibleCondition(nameof(IsModSuccess), SimpleCondition.IsFalse)]
        public StatusItem ModStatus { get; set; } = new StatusItem();

        [DisplayNameL("PluginOptions_EnableLocalMetadataSource_Enable_Local_Metadata_Source", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableLocalMetadataSource_Build_a_local_TMDB_metadata_source_with_passive_cache_and_override_cache__Default_is_OFF_", typeof(Resources))]
        public bool EnableLocalMetadataSource { get; set; } = false;

        [DisplayNameL("PluginOptions_LocalMetadataRoot_Metadata_Root_Directory", typeof(Resources))]
        [DescriptionL("PluginOptions_LocalMetadataRoot_Path_of_the_metadata_root_directory__cache_and_override_subdirectories_are_created_automatically__Default_is_EMPTY_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public string LocalMetadataRoot { get; set; } = string.Empty;

        [DisplayNameL("PluginOptions_LocalMetadataMode_Metadata_Source_Mode", typeof(Resources))]
        [DescriptionL("PluginOptions_LocalMetadataMode_Cache_First_reads_override_then_passive_cache_then_online__Online_With_Cache_reads_override_then_online_and_only_writes_the_passive_cache__Override_Only_reads_override_then_online__Off_disables_the_local_source__Default_is_Cache_First_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public LocalMetadataMode LocalMetadataMode { get; set; } = LocalMetadataMode.CacheFirst;

        [DisplayNameL("PluginOptions_LocalMetadataAutoRefreshImages_Automatically_Cache_Images", typeof(Resources))]
        [DescriptionL("PluginOptions_LocalMetadataAutoRefreshImages_Download_TMDB_images_into_the_passive_cache_images_subdirectory__Default_is_ON_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool LocalMetadataAutoRefreshImages { get; set; } = true;

        [DisplayNameL("PluginOptions_EnableLocalEpisodeGroup_Enable_Local_Episode_Group", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableLocalEpisodeGroup_Map_local_season_and_episode_numbers_with_episodegroup_json_from_the_override_directory_or_the_series_directory__Missing_entries_fall_back_to_the_normal_order__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool EnableLocalEpisodeGroup { get; set; } = false;

        [DisplayNameL("PluginOptions_UnifiedRatingMode_Unified_Content_Rating", typeof(Resources))]
        [DescriptionL("PluginOptions_UnifiedRatingMode_Map_source_ratings_to_one_target_rating__Record_Only_logs_matches__Enforce_Unified_Rating_rewrites_OfficialRating_before_it_is_saved__Default_is_Off_", typeof(Resources))]
        public UnifiedRatingMode UnifiedRatingMode { get; set; } = UnifiedRatingMode.Off;

        [DisplayNameL("PluginOptions_UnifiedRatingTarget_Target_Content_Rating", typeof(Resources))]
        [DescriptionL("PluginOptions_UnifiedRatingTarget_Rating_used_by_mapping_entries_with_an_empty_target__Default_is_US_TV_MA_", typeof(Resources))]
        public string UnifiedRatingTarget { get; set; } = "US:TV-MA";

        [DisplayNameL("GeneralOptions_EditorTitle_General_Options", typeof(Resources))]
        public GeneralOptions GeneralOptions { get; set; } = new GeneralOptions();

        [DisplayNameL("NetworkOptions_EditorTitle_Network", typeof(Resources))]
        public NetworkOptions NetworkOptions { get; set; } = new NetworkOptions();

        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public MediaInfoExtractOptions MediaInfoExtractOptions { get; set; } = new MediaInfoExtractOptions();
        
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public MetadataEnhanceOptions MetadataEnhanceOptions { get; set; } = new MetadataEnhanceOptions();

        [DisplayNameL("PluginOptions_IntroSkipOptions_Intro_Credits_Detection", typeof(Resources))]
        public IntroSkipOptions IntroSkipOptions { get; set; } = new IntroSkipOptions();

        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public ExperienceEnhanceOptions ExperienceEnhanceOptions { get; set; } = new ExperienceEnhanceOptions();

        [DisplayNameL("NotificationOptions_EditorTitle_Notification", typeof(Resources))]
        public NotificationOptions NotificationOptions { get; set; } = new NotificationOptions();

        [DisplayNameL("SearchOptions_EditorTitle_Chinese_Search", typeof(Resources))]
        public SearchOptions SearchOptions { get; set; } = new SearchOptions();

        [DisplayNameL("AboutOptions_EditorTitle_About", typeof(Resources))]
        public AboutOptions AboutOptions { get; set; } = new AboutOptions();

        [Browsable(false)]
        public bool? IsModSuccess => true;

        [Browsable(false)]
        public bool ShowConflictPluginLoadedStatus =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Any(n => n == "StrmExtract" || n == "InfuseSync");

        protected override void Validate(ValidationContext context)
        {
            if (NetworkOptions.EnableProxyServer && !CommonUtility.IsValidProxyUrl(NetworkOptions.ProxyServer))
            {
                context.AddValidationError(Resources.InvalidProxyServer);
            }

            if (GeneralOptions.CatchupMode &&
                GeneralOptions.CatchupTaskScope.Contains(CatchupTask.Fingerprint.ToString()) &&
                !IntroSkipOptions.UnlockIntroSkip)
            {
                context.AddValidationError(Resources.InvalidFingerprintCatchup);
            }

            if (MetadataEnhanceOptions.EnableLocalMovieDb)
            {
                if (!CommonUtility.IsValidHttpUrl(MetadataEnhanceOptions.LocalMovieDbUrl))
                {
                    context.AddValidationError(Resources.InvalidLocalMovieDbUrl);
                }

                if (!string.IsNullOrWhiteSpace(MetadataEnhanceOptions.LocalMovieDbApiKey) &&
                    !CommonUtility.IsValidMovieDbApiKey(MetadataEnhanceOptions.LocalMovieDbApiKey))
                {
                    context.AddValidationError(Resources.InvalidLocalMovieDbApiKey);
                }
            }

            if (MetadataEnhanceOptions.EnableAltMovieDbUrl)
            {
                if (!string.IsNullOrWhiteSpace(MetadataEnhanceOptions.AltMovieDbApiUrl) &&
                    !CommonUtility.IsValidHttpUrl(MetadataEnhanceOptions.AltMovieDbApiUrl))
                {
                    context.AddValidationError(Resources.InvalidAltMovieDbApiUrl);
                }

                if (!string.IsNullOrWhiteSpace(MetadataEnhanceOptions.AltMovieDbImageUrl) &&
                    !CommonUtility.IsValidHttpUrl(MetadataEnhanceOptions.AltMovieDbImageUrl))
                {
                    context.AddValidationError(Resources.InvalidAltMovieDbImageUrl);
                }

                if (!string.IsNullOrWhiteSpace(MetadataEnhanceOptions.AltMovieDbApiKey) &&
                    !CommonUtility.IsValidMovieDbApiKey(MetadataEnhanceOptions.AltMovieDbApiKey))
                {
                    context.AddValidationError(Resources.InvalidAltMovieDbApiKey);
                }
            }
        }
    }
}
