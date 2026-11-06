using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Optional per-library favorite separation. Emby keys movie user data (and therefore the
    /// favorite flag) by provider id, so the same movie stored in two libraries shares one
    /// favorite. When enabled, Strm Assistant keeps its own favorite record for each concrete
    /// item and stops a favorite from leaking to the same movie in another library. The plugin
    /// only takes the favorite routes over after a restart, matching how other server-level
    /// options are applied.
    /// </summary>
    public class VersionFavoriteOptions : EditableOptionsBase
    {
        [DisplayNameL("VersionFavoriteOptions_EditorTitle_Cross_Library_Favorites", typeof(Resources))]
        public override string EditorTitle =>
            Resources.VersionFavoriteOptions_EditorTitle_Cross_Library_Favorites;

        [DisplayNameL(
            "VersionFavoriteOptions_EnableCrossLibraryFavoriteSeparation_Separate_Cross_Library_Favorites",
            typeof(Resources))]
        [DescriptionL(
            "VersionFavoriteOptions_EnableCrossLibraryFavoriteSeparation_Record_movie_favorites_per_library_so_favoriting_a_movie_in_one_library_does_not_affect_the_same_movie_in_another_library__Unfavoriting_only_affects_the_current_library__Applies_to_movies_in_the_web_client__A_restart_is_required_after_changing__Default_is_OFF_",
            typeof(Resources))]
        [Required]
        public bool EnableCrossLibraryFavoriteSeparation { get; set; } = false;
    }
}
