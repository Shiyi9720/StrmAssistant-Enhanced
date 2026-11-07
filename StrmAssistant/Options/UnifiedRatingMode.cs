using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    public enum UnifiedRatingMode
    {
        [DescriptionL("UnifiedRatingMode_Off_Off", typeof(Resources))]
        Off,

        [DescriptionL("UnifiedRatingMode_RecordOnly_Record_Only", typeof(Resources))]
        RecordOnly,

        [DescriptionL("UnifiedRatingMode_Enforce_Enforce_Unified_Rating", typeof(Resources))]
        Enforce
    }
}
