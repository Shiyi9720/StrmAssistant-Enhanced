using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Options for the optional IntroDb lookup that runs before Emby's fingerprint based intro
    /// detection. IntroDb provides already known intro/outro timestamps for episodes, which are
    /// written through the same chapter marker store that the fingerprint pipeline uses.
    /// </summary>
    public class IntroDbOptions : EditableOptionsBase
    {
        [DisplayNameL("IntroDbOptions_EditorTitle_IntroDb", typeof(Resources))]
        public override string EditorTitle => Resources.IntroDbOptions_EditorTitle_IntroDb;

        [DisplayNameL("IntroDbOptions_EnableIntroDb_Enable_IntroDb", typeof(Resources))]
        [DescriptionL("IntroDbOptions_EnableIntroDb_Fetch_intro_and_credits_markers_from_the_public_IntroDb_database_during_intro_detection__Default_is_OFF_", typeof(Resources))]
        [Required]
        public bool EnableIntroDb { get; set; } = false;

        [DisplayNameL("IntroDbOptions_EnableMovieIntroDb_Enable_Movie_IntroDb", typeof(Resources))]
        [DescriptionL("IntroDbOptions_EnableMovieIntroDb_Look_up_credits_for_movies_and_record_the_range_in_the_plugin_store__Emby_has_no_movie_skip_button__Default_is_OFF_", typeof(Resources))]
        public bool EnableMovieIntroDb { get; set; } = false;

        [DisplayNameL("IntroDbOptions_WriteIntroMarkers_Write_Intro_Markers", typeof(Resources))]
        [DescriptionL("IntroDbOptions_WriteIntroMarkers_Write_intro_start_and_end_markers_when_IntroDb_has_them__Default_is_True_", typeof(Resources))]
        [VisibleCondition(nameof(EnableIntroDb), SimpleCondition.IsTrue)]
        public bool WriteIntroMarkers { get; set; } = true;

        [DisplayNameL("IntroDbOptions_WriteCreditsMarkers_Write_Credits_Markers", typeof(Resources))]
        [DescriptionL("IntroDbOptions_WriteCreditsMarkers_Write_credits_start_markers_from_the_IntroDb_outro_segment__Default_is_True_", typeof(Resources))]
        [VisibleCondition(nameof(EnableIntroDb), SimpleCondition.IsTrue)]
        public bool WriteCreditsMarkers { get; set; } = true;

        [DisplayNameL("IntroDbOptions_MinimumConfidencePercent_Minimum_Confidence", typeof(Resources))]
        [DescriptionL("IntroDbOptions_MinimumConfidencePercent_Ignore_IntroDb_segments_below_this_confidence_percentage__0_accepts_everything__Default_is_50_", typeof(Resources))]
        [MinValue(0), MaxValue(100)]
        [VisibleCondition(nameof(EnableIntroDb), SimpleCondition.IsTrue)]
        public int MinimumConfidencePercent { get; set; } = 50;

        [DisplayNameL("IntroDbOptions_CacheExpiryDays_Cache_Expiry_Days", typeof(Resources))]
        [DescriptionL("IntroDbOptions_CacheExpiryDays_How_long_lookup_results_are_reused_before_querying_IntroDb_again__Default_is_30_", typeof(Resources))]
        [MinValue(1), MaxValue(365)]
        [VisibleCondition(nameof(EnableIntroDb), SimpleCondition.IsTrue)]
        public int CacheExpiryDays { get; set; } = 30;
    }
}
