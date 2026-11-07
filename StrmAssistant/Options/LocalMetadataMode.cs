using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    public enum LocalMetadataMode
    {
        [DescriptionL("LocalMetadataMode_CacheFirst_Cache_First", typeof(Resources))]
        CacheFirst,

        [DescriptionL("LocalMetadataMode_OnlineWithCache_Online_Scrape_And_Cache", typeof(Resources))]
        OnlineWithCache,

        [DescriptionL("LocalMetadataMode_OverrideOnly_Override_Cache_Only", typeof(Resources))]
        OverrideOnly,

        [DescriptionL("LocalMetadataMode_Off_Off", typeof(Resources))]
        Off
    }
}
