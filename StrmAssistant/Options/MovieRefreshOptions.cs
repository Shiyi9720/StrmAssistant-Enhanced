using Emby.Web.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Extends the episode metadata refresh rules to movies. The rules themselves and their
    /// thresholds stay the ones configured for episodes
    /// (<see cref="MetadataEnhanceOptions.EpisodeRefreshScope"/> and the episode lookback days);
    /// this section only decides whether movies take part. Off by default so that enabling it is
    /// a deliberate choice, because a refresh replaces all metadata and images of the item.
    /// </summary>
    public class MovieRefreshOptions : EditableOptionsBase
    {
        [DisplayNameL("MovieRefreshOptions_EditorTitle_Movie_Metadata_Refresh", typeof(Resources))]
        public override string EditorTitle =>
            Resources.MovieRefreshOptions_EditorTitle_Movie_Metadata_Refresh;

        [DisplayNameL("MovieRefreshOptions_EnableMovieRefresh_Enable_Movie_Refresh", typeof(Resources))]
        [DescriptionL(
            "MovieRefreshOptions_EnableMovieRefresh_Refresh_movies_with_the_same_rules_as_episodes__missing_overview__missing_image_or_non_Chinese_overview__Items_are_scoped_by_the_episode_refresh_lookback_days_against_the_date_added__Default_is_OFF_",
            typeof(Resources))]
        public bool EnableMovieRefresh { get; set; } = false;
    }
}
