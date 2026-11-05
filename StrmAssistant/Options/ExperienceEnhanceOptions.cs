using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class ExperienceEnhanceOptions : EditableOptionsBase
    {
        [DisplayNameL("ExperienceEnhanceOptions_EditorTitle_Experience_Enhance", typeof(Resources))]
        public override string EditorTitle => Resources.ExperienceEnhanceOptions_EditorTitle_Experience_Enhance;
        
        [DisplayNameL("GeneralOptions_MergeMultiVersion_Merge_Multiple_Versions", typeof(Resources))]
        [DescriptionL("GeneralOptions_MergeMultiVersion_Auto_merge_multiple_versions_if_in_the_same_folder_", typeof(Resources))]
        [Required]
        public bool MergeMultiVersion { get; set; } = false;

        public enum MergeMoviesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }
        
        [DisplayName("")]
        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        public MergeMoviesScopeOption MergeMoviesPreference { get; set; } = MergeMoviesScopeOption.LibraryScope;

        public enum MergeSeriesScopeOption
        {
            [DescriptionL("MergeScopeOption_LibraryScope_LibraryScope", typeof(Resources))]
            LibraryScope,
            [DescriptionL("MergeScopeOption_GlobalScope_GlobalScope", typeof(Resources))]
            GlobalScope
        }

        [VisibleCondition(nameof(MergeMultiVersion), SimpleCondition.IsTrue)]
        [Browsable(false)]
        public MergeSeriesScopeOption MergeSeriesPreference => MergeSeriesScopeOption.LibraryScope;

        [DisplayNameL("ExperienceEnhanceOptions_AutoMarkPreviousEpisodesPlayed_Auto_Mark_Previous_Episodes_Played", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_AutoMarkPreviousEpisodesPlayed_Mark_previous_episodes_in_the_same_season_as_played_when_the_current_episode_finishes__Default_is_OFF_", typeof(Resources))]
        public bool AutoMarkPreviousEpisodesPlayed { get; set; } = false;

        [DisplayNameL("ExperienceEnhanceOptions_ReverseSeasonEpisodeOrder_Reverse_Season_and_Episode_Order", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_ReverseSeasonEpisodeOrder_Show_seasons_and_episodes_in_descending_order_in_the_web_client__Default_is_OFF_", typeof(Resources))]
        public bool ReverseSeasonEpisodeOrder { get; set; } = false;

        public enum EpisodeDisplayStyleOption
        {
            [DescriptionL("EpisodeDisplayStyleOption_Default_Default", typeof(Resources))]
            Default,

            [DescriptionL("EpisodeDisplayStyleOption_SeasonEpisodeNumber_SxxExx", typeof(Resources))]
            SeasonEpisodeNumber,

            [DescriptionL("EpisodeDisplayStyleOption_EpisodeName_Episode_Name", typeof(Resources))]
            EpisodeName
        }

        [DisplayNameL("ExperienceEnhanceOptions_EpisodeDisplayStyle_Episode_Display_Style", typeof(Resources))]
        [DescriptionL("ExperienceEnhanceOptions_EpisodeDisplayStyle_Force_a_consistent_episode_title_style_in_the_web_client__Default_is_Default_", typeof(Resources))]
        public EpisodeDisplayStyleOption EpisodeDisplayStyle { get; set; } = EpisodeDisplayStyleOption.Default;
    }
}
