using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System.Collections.Generic;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// Writes the pinyin initials of Chinese titles into the item sort title so that Emby's
    /// title sorting follows the pinyin reading order. Off by default. Only items whose sort
    /// title is still untouched (blank or identical to the name) and whose sort title field is
    /// not locked are rewritten.
    /// </summary>
    public class PinyinSortOptions : EditableOptionsBase
    {
        [DisplayNameL("PinyinSortOptions_EditorTitle_Pinyin_Sort_Title", typeof(Resources))]
        public override string EditorTitle => Resources.PinyinSortOptions_EditorTitle_Pinyin_Sort_Title;

        [DisplayNameL("PinyinSortOptions_EnablePinyinSortName_Enable_Pinyin_Sort_Title", typeof(Resources))]
        [DescriptionL(
            "PinyinSortOptions_EnablePinyinSortName_Write_pinyin_initials_of_Chinese_titles_to_the_sort_title_Only_untouched_sort_titles_are_rewritten_Default_is_OFF",
            typeof(Resources))]
        public bool EnablePinyinSortName { get; set; } = false;

        [DisplayNameL("PinyinSortOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL(
            "PinyinSortOptions_LibraryScope_Library_scope_to_generate_pinyin_sort_titles_for_Blank_includes_all",
            typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [VisibleCondition(nameof(EnablePinyinSortName), SimpleCondition.IsTrue)]
        public string LibraryScope { get; set; } = string.Empty;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }
    }
}
