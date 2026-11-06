using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class MediaInfoExtractOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_EditorTitle_Strm_Extract", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_EditorTitle_Strm_Extract;

        [DisplayNameL("PluginOptions_IncludeExtra_Include_Extra", typeof(Resources))]
        [DescriptionL("PluginOptions_IncludeExtra_Include_media_extras_to_extract__Default_is_False_", typeof(Resources))]
        [Required]
        public bool IncludeExtra { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_CustomFfprobeArguments_Custom_ffprobe_Arguments", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CustomFfprobeArguments_Extra_arguments_inserted_before_the_input_for_ffprobe_commands__Duplicate_flags_are_ignored__Default_is_EMPTY_", typeof(Resources))]
        public string CustomFfprobeArguments { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_CustomFfmpegArguments_Custom_ffmpeg_Arguments", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CustomFfmpegArguments_Extra_arguments_appended_to_ffmpeg_commands__Duplicate_flags_are_ignored__Default_is_EMPTY_", typeof(Resources))]
        public string CustomFfmpegArguments { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_CustomFfmpegPath_Custom_ffmpeg_Path", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CustomFfmpegPath_Path_to_a_custom_ffmpeg_build_with_vulkan_and_libplacebo_used_for_Dolby_Vision_Profile_5_screenshots__Default_is_EMPTY_", typeof(Resources))]
        public string CustomFfmpegPath { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_SubtitleBlacklistLanguages_Subtitle_Blacklist_Languages", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_SubtitleBlacklistLanguages_Comma_separated_languages_or_wildcards__Matching_subtitle_streams_are_removed__Default_is_EMPTY_", typeof(Resources))]
        public string SubtitleBlacklistLanguages { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_SubtitleBlacklistTitles_Subtitle_Blacklist_Titles", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_SubtitleBlacklistTitles_Comma_separated_title_keywords_or_wildcards__Matching_subtitle_streams_are_removed__Default_is_EMPTY_", typeof(Resources))]
        public string SubtitleBlacklistTitles { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_SubtitleBlacklistCodecs_Subtitle_Blacklist_Codecs", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_SubtitleBlacklistCodecs_Comma_separated_codecs_or_wildcards__Matching_subtitle_streams_are_removed__Default_is_EMPTY_", typeof(Resources))]
        public string SubtitleBlacklistCodecs { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_MediaInfoExcludeKeywords_Media_Info_Exclude_Keywords", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_MediaInfoExcludeKeywords_Comma_separated_keywords_or_wildcards__Items_matching_the_keywords_or_tagged_mediainfo_exclude_are_skipped__Default_is_EMPTY_", typeof(Resources))]
        public string MediaInfoExcludeKeywords { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_VideoThumbnailExcludeKeywords_Video_Thumbnail_Exclude_Keywords", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_VideoThumbnailExcludeKeywords_Comma_separated_keywords_or_wildcards__Items_matching_the_keywords_or_tagged_videothumbnail_exclude_are_skipped__Default_is_EMPTY_", typeof(Resources))]
        public string VideoThumbnailExcludeKeywords { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_VideoThumbnailExcludeMediaContainers_Video_Thumbnail_Exclude_Media_Containers", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_VideoThumbnailExcludeMediaContainers_Comma_separated_media_containers_to_skip_for_chapter_image_and_BIF_extraction__Default_is_EMPTY_", typeof(Resources))]
        public string VideoThumbnailExcludeMediaContainers { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_ImageExtractionTimeoutMultiplier_Image_Extraction_Timeout_Multiplier", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageExtractionTimeoutMultiplier_Multiplier_applied_to_the_default_timeout_of_chapter_image_and_BIF_extraction__Default_is_2_0_", typeof(Resources))]
        [Required]
        public double ImageExtractionTimeoutMultiplier { get; set; } = 2.0;

        [DisplayNameL("MediaInfoExtractOptions_ScreenshotQuality_Screenshot_Quality", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ScreenshotQuality_JPEG_quality_for_generated_chapter_images__0_keeps_the_original__Default_is_0_", typeof(Resources))]
        [Required, MinValue(0), MaxValue(100)]
        public int ScreenshotQuality { get; set; } = 0;

        [DisplayNameL("MediaInfoExtractOptions_ScreenshotMaxWidth_Maximum_Width", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ScreenshotMaxWidth_Maximum_width_of_generated_chapter_images__0_is_unlimited__Default_is_0_", typeof(Resources))]
        [Required, MinValue(0)]
        public int ScreenshotMaxWidth { get; set; } = 0;

        [DisplayNameL("MediaInfoExtractOptions_ScreenshotMaxHeight_Maximum_Height", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ScreenshotMaxHeight_Maximum_height_of_generated_chapter_images__0_is_unlimited__Default_is_0_", typeof(Resources))]
        [Required, MinValue(0)]
        public int ScreenshotMaxHeight { get; set; } = 0;

        [DisplayNameL("MediaInfoExtractOptions_CropUltraWideScreenshots_Crop_Ultra_Wide_Screenshots", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CropUltraWideScreenshots_Crop_screenshots_wider_than_16_9_to_16_9_by_trimming_the_sides__Default_is_False_", typeof(Resources))]
        public bool CropUltraWideScreenshots { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_CropPortraitScreenshots_Crop_Portrait_Screenshots", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_CropPortraitScreenshots_Crop_vertical_screenshots_to_a_2_3_aspect_ratio__Default_is_False_", typeof(Resources))]
        public bool CropPortraitScreenshots { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_DetectHdrVivid_Detect_HDR_Vivid", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_DetectHdrVivid_Probes_the_first_video_frame_for_CUVA_2094_10_metadata_and_tags_matching_videos_as_HDR_Vivid__Default_is_False_", typeof(Resources))]
        public bool DetectHdrVivid { get; set; } = false;

        [DisplayNameL("PluginOptions_EnableImageCapture_Enable_Image_Capture", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableImageCapture_Perform_image_capture_for_videos_without_primary_image__Default_is_False_", typeof(Resources))]
        [Browsable(false)]
        [Required]
        public bool EnableImageCapture => false;

        [DisplayNameL("MediaInfoExtractOptions_ImageCaptureOffset_Image_Capture_Offset", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_ImageCaptureOffset_Image_capture_position_as_a_percentage_of_runtime__Default_is_10_", typeof(Resources))]
        [Required, MinValue(10), MaxValue(90)]
        [VisibleCondition(nameof(EnableImageCapture), SimpleCondition.IsTrue)]
        public int ImageCapturePosition { get; set; } = 10;

        [Browsable(false)]
        [Required]
        public string ImageCaptureExcludeMediaContainers { get; set; } =
            string.Join(",", new[] { MediaContainers.MpegTs, MediaContainers.Ts, MediaContainers.M2Ts });

        public enum PersistMediaInfoOption
        {
            None,
            Default,
            Restore
        }

        [Browsable(false)]
        public List<EditorRadioOption> PersistMediaInfoOptionList { get; set; } = new List<EditorRadioOption>();

        [DisplayName("")]
        [SelectItemsSource(nameof(PersistMediaInfoOptionList))]
        [SelectShowRadioGroup]
        public string PersistMediaInfoMode { get; set; } = PersistMediaInfoOption.None.ToString();

        [DisplayNameL("MediaInfoExtractOptions_MediaInfoJsonRootFolder_MediaInfo_Json_Root_Folder", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_MediaInfoJsonRootFolder_Store_or_load_media_info_JSON_files_under_this_root_folder__Default_is_EMPTY_", typeof(Resources))]
        [EditFolderPicker]
        [VisibleCondition(nameof(PersistMediaInfoMode), ValueCondition.IsNotEqual, PersistMediaInfoOption.None)]
        public string MediaInfoJsonRootFolder { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_EnableIsoExtraction_Enable_ISO_Extraction", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_EnableIsoExtraction_Extract_the_main_video_from_ISO_images_and_register_it_as_an_additional_media_source__Default_is_OFF_", typeof(Resources))]
        public bool EnableIsoExtraction { get; set; } = false;

        [DisplayNameL("MediaInfoExtractOptions_IsoExtractionCacheFolder_ISO_Cache_Folder", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_IsoExtractionCacheFolder_Folder_used_to_cache_videos_extracted_from_ISO_images__Blank_uses_the_plugin_data_folder__Default_is_EMPTY_", typeof(Resources))]
        [EditFolderPicker]
        [VisibleCondition(nameof(EnableIsoExtraction), SimpleCondition.IsTrue)]
        public string IsoExtractionCacheFolder { get; set; } = string.Empty;

        [DisplayNameL("MediaInfoExtractOptions_IsoExtractionWaitSeconds_ISO_Extraction_Wait_Seconds", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_IsoExtractionWaitSeconds_How_long_a_playback_request_waits_for_the_first_extraction_before_it_continues_in_the_background__It_must_be_between_10_and_600__Default_is_60_", typeof(Resources))]
        [Required, MinValue(10), MaxValue(600)]
        [VisibleCondition(nameof(EnableIsoExtraction), SimpleCondition.IsTrue)]
        public int IsoExtractionWaitSeconds { get; set; } = 60;

        [DisplayNameL("MediaInfoExtractOptions_EnableDiscMediaInfoExtraction_Disc_Media_Info_Extraction", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_EnableDiscMediaInfoExtraction_Probe_ISO_images_and_BDMV_VIDEO_TS_folders_and_extract_full_media_info_from_the_main_video__Default_is_True_", typeof(Resources))]
        public bool EnableDiscMediaInfoExtraction { get; set; } = true;

        [DisplayNameL("MediaInfoExtractOptions_DiscMediaInfoExtractionTimeoutSeconds_Disc_Extraction_Timeout_Seconds", typeof(Resources))]
        [DescriptionL("MediaInfoExtractOptions_DiscMediaInfoExtractionTimeoutSeconds_How_long_the_media_info_task_waits_for_an_ISO_extraction_before_it_continues_in_the_background__It_must_be_between_60_and_7200__Default_is_600_", typeof(Resources))]
        [Required, MinValue(60), MaxValue(7200)]
        [VisibleCondition(nameof(EnableDiscMediaInfoExtraction), SimpleCondition.IsTrue)]
        public int DiscMediaInfoExtractionTimeoutSeconds { get; set; } = 600;

        [DisplayNameL("DistributedExtractOptions_EditorTitle_Distributed_Extract", typeof(Resources))]
        public DistributedExtractOptions DistributedExtractOptions { get; set; } =
            new DistributedExtractOptions();

        [DisplayNameL("MovieRefreshOptions_EditorTitle_Movie_Metadata_Refresh", typeof(Resources))]
        public MovieRefreshOptions MovieRefreshOptions { get; set; } = new MovieRefreshOptions();

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayNameL("PluginOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("PluginOptions_LibraryScope_Library_scope_to_extract__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        public string LibraryScope { get; set; } = string.Empty;
    }
}
