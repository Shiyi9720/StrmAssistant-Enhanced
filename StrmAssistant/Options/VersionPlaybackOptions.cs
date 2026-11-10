using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Optional per-version resume points. Emby keeps a single
    /// <c>UserData.PlaybackPositionTicks</c> per item key, so every version of a merged movie (and
    /// the same movie in another library) shares one position. When enabled, the plugin stores a
    /// separate resume point for each concrete (item, media source) pair, converts the position
    /// once when switching versions, and keeps the finished state shared. Default is OFF, which
    /// preserves the previous behaviour.
    /// </summary>
    public class VersionPlaybackOptions : EditableOptionsBase
    {
        [DisplayNameL("VersionPlaybackOptions_EditorTitle_Version_Playback", typeof(Resources))]
        public override string EditorTitle =>
            Resources.VersionPlaybackOptions_EditorTitle_Version_Playback;

        [DisplayNameL(
            "VersionPlaybackOptions_PersistPerVersionResume_Keep_Resume_Point_Per_Version",
            typeof(Resources))]
        [DescriptionL(
            "VersionPlaybackOptions_PersistPerVersionResume_Record_the_resume_point_for_each_version_separately__When_switching_versions_the_position_is_converted_once_by_runtime_ratio_and_the_finished_state_stays_shared__Applies_to_movies_in_the_web_client__Default_is_OFF_",
            typeof(Resources))]
        [Required]
        public bool PersistPerVersionResume { get; set; } = false;
    }
}
