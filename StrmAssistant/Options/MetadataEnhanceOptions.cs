using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
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

        [Browsable(false)]
        [Required]
        public int EpisodeRefreshLookBackDays { get; set; } = 365;

        [Browsable(false)]
        [Required]
        public bool EpisodeRefreshNonChineseOverview { get; set; } = false;
    }
}
