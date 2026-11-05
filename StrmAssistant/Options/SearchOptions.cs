using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    public class SearchOptions : EditableOptionsBase
    {
        [DisplayNameL("SearchOptions_EditorTitle_Chinese_Search", typeof(Resources))]
        public override string EditorTitle => Resources.SearchOptions_EditorTitle_Chinese_Search;

        [DisplayNameL("SearchOptions_EnableChineseSearch_Enable_Chinese_Search", typeof(Resources))]
        [DescriptionL("SearchOptions_EnableChineseSearch_Adds_pinyin__simplified_traditional_and_alias_matching_to_web_client_search__When_the_index_is_not_ready_the_original_Emby_search_is_used__Default_is_ON_", typeof(Resources))]
        public bool EnableChineseSearch { get; set; } = true;

        [DisplayNameL("SearchOptions_PinyinSearch_Pinyin_Search", typeof(Resources))]
        [DescriptionL("SearchOptions_PinyinSearch_Match_full_pinyin_and_initials__for_example_zhongwen_and_zw_both_find_Chinese_titles__Default_is_ON_", typeof(Resources))]
        [VisibleCondition(nameof(EnableChineseSearch), SimpleCondition.IsTrue)]
        public bool PinyinSearch { get; set; } = true;

        [DisplayNameL("SearchOptions_ConversionSearch_Simplified_Traditional_Cross_Search", typeof(Resources))]
        [DescriptionL("SearchOptions_ConversionSearch_A_simplified_Chinese_query_also_finds_traditional_titles_and_vice_versa__Default_is_ON_", typeof(Resources))]
        [VisibleCondition(nameof(EnableChineseSearch), SimpleCondition.IsTrue)]
        public bool ConversionSearch { get; set; } = true;

        [DisplayNameL("SearchOptions_AliasSearch_Alias_and_Original_Title_Search", typeof(Resources))]
        [DescriptionL("SearchOptions_AliasSearch_Match_original_titles__sort_titles__alternate_titles_and_title_plus_year_combinations__Default_is_ON_", typeof(Resources))]
        [VisibleCondition(nameof(EnableChineseSearch), SimpleCondition.IsTrue)]
        public bool AliasSearch { get; set; } = true;

        [DisplayNameL("SearchOptions_SmartRanking_Ranked_Results", typeof(Resources))]
        [DescriptionL("SearchOptions_SmartRanking_Order_results_by_exact__prefix__contains_then_fuzzy_match__with_Chinese_matches__rating_and_year_as_tie_breakers__Default_is_ON_", typeof(Resources))]
        [VisibleCondition(nameof(EnableChineseSearch), SimpleCondition.IsTrue)]
        public bool SmartRanking { get; set; } = true;

        [DisplayNameL("SearchOptions_TmdbAliasSearch_TMDB_Alternative_Titles", typeof(Resources))]
        [DescriptionL("SearchOptions_TmdbAliasSearch_Fetch_alternative_titles_from_TMDB_during_index_rebuilds__Requests_are_rate_limited_and_cached_in_the_index__Default_is_OFF_", typeof(Resources))]
        [VisibleCondition(nameof(AliasSearch), SimpleCondition.IsTrue)]
        public bool TmdbAliasSearch { get; set; } = false;

        [DisplayNameL("SearchOptions_IndexStatus_Search_Index_Status", typeof(Resources))]
        [VisibleCondition(nameof(EnableChineseSearch), SimpleCondition.IsTrue)]
        public StatusItem IndexStatus { get; set; } = new StatusItem();

        [Browsable(false)]
        public bool HasIndexStatus { get; set; } = true;
    }
}
