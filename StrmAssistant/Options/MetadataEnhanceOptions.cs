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

    public class MetadataEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_MetadataEnhanceOptions_Metadata_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_MetadataEnhanceOptions_Metadata_Enhance;
        
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
            DefaultEpisodeName
        }

        [Browsable(false)]
        public List<EditorSelectOption> EpisodeRefreshOptionList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_Metadata_Refresh_Scope", typeof(Resources))]
        [DescriptionL("MetadataEnhanceOptions_EpisodeRefreshScope_Episode_refresh_scope_for_scheduled_task_and_catch_up__Default_is_no_overview_and_no_image_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(EpisodeRefreshOptionList))]
        public string EpisodeRefreshScope { get; set; } = string.Join(",", EpisodeRefreshOption.NoOverview.ToString(),
            EpisodeRefreshOption.NoImage.ToString());
        
        [Browsable(false)]
        [Required]
        public int EpisodeRefreshLookBackDays { get; set; } = 365;
    }
}
