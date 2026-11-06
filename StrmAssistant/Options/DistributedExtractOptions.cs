using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Optional distributed extraction (media info, chapter images and intro fingerprints). Off
    /// by default: when enabled, the tasks first ask the configured worker nodes to do the work
    /// and silently fall back to local extraction when no worker is reachable or returns a usable
    /// result.
    /// </summary>
    public class DistributedExtractOptions : EditableOptionsBase
    {
        [DisplayNameL("DistributedExtractOptions_EditorTitle_Distributed_Extract", typeof(Resources))]
        public override string EditorTitle =>
            Resources.DistributedExtractOptions_EditorTitle_Distributed_Extract;

        [DisplayNameL("DistributedExtractOptions_EnableDistributedExtract_Enable_Distributed_Extraction",
            typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_EnableDistributedExtract_Offload_media_info_extraction_to_worker_nodes__Every_node_must_use_the_same_shared_key__When_no_worker_is_reachable_the_task_falls_back_to_local_extraction__Default_is_False_",
            typeof(Resources))]
        public bool EnableDistributedExtract { get; set; } = false;

        [DisplayNameL(
            "DistributedExtractOptions_EnableDistributedThumbnail_Enable_Distributed_Thumbnail_Extraction",
            typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_EnableDistributedThumbnail_Offload_chapter_image_extraction_to_worker_nodes__It_applies_to_libraries_that_extract_chapter_images_only__When_no_worker_is_reachable_the_task_falls_back_to_local_extraction__Default_is_False_",
            typeof(Resources))]
        [VisibleCondition(nameof(EnableDistributedExtract), SimpleCondition.IsTrue)]
        public bool EnableDistributedThumbnail { get; set; } = false;

        [DisplayNameL(
            "DistributedExtractOptions_EnableDistributedFingerprint_Enable_Distributed_Intro_Fingerprint_Extraction",
            typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_EnableDistributedFingerprint_Offload_audio_fingerprint_extraction_for_intro_detection_to_worker_nodes__The_detection_and_marker_writes_stay_on_this_node__When_no_worker_is_reachable_the_task_falls_back_to_local_extraction__Default_is_False_",
            typeof(Resources))]
        [VisibleCondition(nameof(EnableDistributedExtract), SimpleCondition.IsTrue)]
        public bool EnableDistributedFingerprint { get; set; } = false;

        [DisplayNameL("DistributedExtractOptions_WorkerNodes_Worker_Nodes", typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_WorkerNodes_Comma_separated_HTTP_URLs_of_worker_nodes__for_example_http_192_168_1_10_8096__Default_is_EMPTY_",
            typeof(Resources))]
        [VisibleCondition(nameof(EnableDistributedExtract), SimpleCondition.IsTrue)]
        public string WorkerNodes { get; set; } = string.Empty;

        [DisplayNameL("DistributedExtractOptions_SharedApiKey_Shared_API_Key", typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_SharedApiKey_Secret_used_to_authenticate_requests_between_nodes__It_must_be_the_same_on_every_node__Default_is_EMPTY_",
            typeof(Resources))]
        [VisibleCondition(nameof(EnableDistributedExtract), SimpleCondition.IsTrue)]
        public string SharedApiKey { get; set; } = string.Empty;

        [DisplayNameL("DistributedExtractOptions_PathMapping_Path_Mapping", typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_PathMapping_Comma_separated_mappings_in_the_form_local_prefix_worker_prefix__Use_it_when_workers_mount_the_same_media_at_a_different_path__Default_is_EMPTY_",
            typeof(Resources))]
        [VisibleCondition(nameof(EnableDistributedExtract), SimpleCondition.IsTrue)]
        public string PathMapping { get; set; } = string.Empty;

        [DisplayNameL("DistributedExtractOptions_TimeoutSeconds_Timeout_Seconds", typeof(Resources))]
        [DescriptionL(
            "DistributedExtractOptions_TimeoutSeconds_Total_time_the_task_waits_for_worker_nodes_before_falling_back_to_local_extraction__It_must_be_between_10_and_600__Default_is_120_",
            typeof(Resources))]
        [Required, MinValue(10), MaxValue(600)]
        [VisibleCondition(nameof(EnableDistributedExtract), SimpleCondition.IsTrue)]
        public int TimeoutSeconds { get; set; } = 120;
    }
}
