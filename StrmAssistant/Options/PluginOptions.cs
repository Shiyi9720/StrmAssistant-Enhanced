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

        // Advanced local-source capabilities. Each one is an independent switch and defaults to
        // off; all of them require EnableLocalMetadataSource (the master switch above).
        [DisplayName("本地元数据：图片覆盖")]
        [Description("用 override/<tmdbid>/images 下的 poster/fanart/clearlogo/landscape/banner/discart/clearart 覆盖条目图片。默认关闭。")]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool LocalImageOverrideEnabled { get; set; } = false;

        [DisplayName("本地元数据：标签合并")]
        [Description("把 tags.json 里的标签合并进电影/剧集（保留 TMDB 标签，去重）。默认关闭。")]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool LocalTagsEnabled { get; set; } = false;

        [DisplayName("本地元数据：字段补丁")]
        [Description("把 patches/*.json 按文件名顺序做字段级合并（对象递归、数组/标量替换、null 删除字段），只作用于返回 Emby 的响应，不回写缓存。默认关闭。")]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool LocalPatchesEnabled { get; set; } = false;

        [DisplayName("本地元数据：外部 ID")]
        [Description("把 external_ids.json 的 tmdb/imdb/tvdb/tvrage/douban/bgm 写入条目的外部 ID。默认关闭。")]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool LocalExternalIdsEnabled { get; set; } = false;

        [DisplayName("本地元数据：剧集播放顺序")]
        [Description("把 episodeorder.json（兼容旧 specials.json）写入剧集的 SortIndexNumber 播放顺序。默认关闭。")]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool LocalEpisodeOrderEnabled { get; set; } = false;

        [DisplayName("本地元数据：内容分级回退")]
        [Description("当 TMDB 缺少首选国家与 US 分级时，按配置的国家链取一个分级并换算为目标分级体系；不猜未知分级、不覆盖已有官方分级。默认关闭。")]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool CertificationFallbackEnabled { get; set; } = false;

        [DisplayName("本地元数据：内容分级国家链")]
        [Description("按顺序尝试的国家代码，逗号分隔。默认 US,JP,GB,DE,FR,CN。")]
        [VisibleCondition(nameof(CertificationFallbackEnabled), SimpleCondition.IsTrue)]
        public string CertificationFallbackCountries { get; set; } = "US,JP,GB,DE,FR,CN";

        [DisplayName("本地元数据：内容分级自定义映射")]
        [Description("额外映射，格式 来源=目标，逗号或分号分隔，例如 FSK 16=R。默认留空。")]
        [VisibleCondition(nameof(CertificationFallbackEnabled), SimpleCondition.IsTrue)]
        public string CertificationFallbackMappings { get; set; } = string.Empty;

        [DisplayNameL("PluginOptions_UnifiedRatingMode_Unified_Content_Rating", typeof(Resources))]
        [DescriptionL("PluginOptions_UnifiedRatingMode_Map_source_ratings_to_one_target_rating__Record_Only_logs_matches__Enforce_Unified_Rating_rewrites_OfficialRating_before_it_is_saved__Default_is_Off_", typeof(Resources))]
        public UnifiedRatingMode UnifiedRatingMode { get; set; } = UnifiedRatingMode.Off;

        [DisplayNameL("PluginOptions_UnifiedRatingTarget_Target_Content_Rating", typeof(Resources))]
        [DescriptionL("PluginOptions_UnifiedRatingTarget_Rating_used_by_mapping_entries_with_an_empty_target__Default_is_US_TV_MA_", typeof(Resources))]
        public string UnifiedRatingTarget { get; set; } = "US:TV-MA";

        [DisplayNameL("PluginOptions_EnableCustomSpecialsOrder_Custom_Specials_Order", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableCustomSpecialsOrder_Apply_specials_json_air_order_values_to_season_0_episodes_from_override_or_cache__Missing_files_or_entries_leave_the_normal_order_unchanged__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(EnableLocalMetadataSource), SimpleCondition.IsTrue)]
        public bool EnableCustomSpecialsOrder { get; set; } = false;

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
