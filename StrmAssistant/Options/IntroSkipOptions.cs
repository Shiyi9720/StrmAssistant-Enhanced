using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Common;
using MediaBrowser.Model.Attributes;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.GenericEdit;
using MediaBrowser.Model.LocalizationAttributes;
using StrmAssistant.Properties;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace StrmAssistant.Options
{
    public class LibraryIntroDetectionFingerprintOptionCollection :
        List<LibraryIntroDetectionFingerprintOption>, IEditableObjectCollection
    {
        IEnumerator<MediaBrowser.Model.GenericEdit.IEditableObject>
            IEnumerable<MediaBrowser.Model.GenericEdit.IEditableObject>.GetEnumerator()
        {
            return this.Cast<MediaBrowser.Model.GenericEdit.IEditableObject>().GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public LibraryIntroDetectionFingerprintOption FindByLibraryId(string libraryId)
        {
            foreach (var option in this)
            {
                if (option.LibraryId == libraryId)
                {
                    return option;
                }
            }

            return null;
        }

        public LibraryIntroDetectionFingerprintOption FindOverride(string libraryId)
        {
            var option = FindByLibraryId(libraryId);

            return option != null && option.Override ? option : null;
        }

        public IEnumerable<LibraryIntroDetectionFingerprintOption> Overrides()
        {
            foreach (var option in this)
            {
                if (option.Override)
                {
                    yield return option;
                }
            }
        }
    }

    public class LibraryIntroDetectionFingerprintOption : EditableObjectBase
    {
        [Browsable(false)]
        public string LibraryId { get; set; }

        [Browsable(false)]
        public string LibraryName { get; set; }

        public override string EditorTitle => LibraryName;

        [DisplayNameL("IntroSkipOptions_LibraryIntroDetectionFingerprint_Override_Override", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryIntroDetectionFingerprint_Override_Override_the_global_intro_detection_fingerprint_length_for_this_library_", typeof(Resources))]
        public bool Override { get; set; }

        [DisplayNameL("IntroSkipOptions_IntroDetectionFingerprintMinutes_Intro_Detection_Fingerprint_Minutes", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroDetectionFingerprintMinutes_It_must_be_between_2_and_20__Default_is_10_", typeof(Resources))]
        [MinValue(2), MaxValue(20)]
        [VisibleCondition(nameof(Override), SimpleCondition.IsTrue)]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;
    }

    public class IntroSkipOptions : EditableOptionsBase
    {
        [DisplayNameL("PluginOptions_IntroSkipOptions_Intro_Credits_Detection", typeof(Resources))]
        public override string EditorTitle => Resources.PluginOptions_IntroSkipOptions_Intro_Credits_Detection;
        
        [DisplayNameL("IntroSkipOptions_UnlockIntroSkip_Built_in_Intro_Skip_Enhanced", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UnlockIntroSkip_Unlock_Strm_support_for_built_in_intro_skip_detection", typeof(Resources))]
        [Required]
        public bool UnlockIntroSkip { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_IntroDetectionFingerprintMinutes_Intro_Detection_Fingerprint_Minutes", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_IntroDetectionFingerprintMinutes_It_must_be_between_2_and_20__Default_is_10_", typeof(Resources))]
        [MinValue(2), MaxValue(20)]
        [Required]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public int IntroDetectionFingerprintMinutes { get; set; } = 10;

        // Emby 4.10 的通用设置页渲染这种 EditableObjectBase 集合会栈溢出直接把进程带走，
        // 因此不在 UI 中暴露；按库覆盖仍可通过插件配置文件直接编辑。
        [Browsable(false)]
        public LibraryIntroDetectionFingerprintOptionCollection LibraryIntroDetectionFingerprintOptions { get; set; } =
            new LibraryIntroDetectionFingerprintOptionCollection();

        public int GetIntroDetectionFingerprintMinutes(string libraryId)
        {
            var overrideOption = string.IsNullOrEmpty(libraryId)
                ? null
                : LibraryIntroDetectionFingerprintOptions?.FindOverride(libraryId);

            var minutes = overrideOption?.IntroDetectionFingerprintMinutes ?? IntroDetectionFingerprintMinutes;

            return Math.Max(2, Math.Min(20, minutes));
        }

        public void SyncLibraryIntroDetectionFingerprintOptions(IEnumerable<VirtualFolderInfo> libraries)
        {
            var currentOptions = LibraryIntroDetectionFingerprintOptions ??
                                 new LibraryIntroDetectionFingerprintOptionCollection();
            var syncedOptions = new LibraryIntroDetectionFingerprintOptionCollection();

            foreach (var library in libraries)
            {
                var existingOption = currentOptions.FindByLibraryId(library.ItemId);

                syncedOptions.Add(new LibraryIntroDetectionFingerprintOption
                {
                    LibraryId = library.ItemId,
                    LibraryName = library.Name,
                    Override = existingOption?.Override ?? false,
                    IntroDetectionFingerprintMinutes = existingOption?.IntroDetectionFingerprintMinutes ??
                                                       IntroDetectionFingerprintMinutes
                });
            }

            LibraryIntroDetectionFingerprintOptions = syncedOptions;
        }

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> MarkerEnabledLibraryList { get; set; }

        [DisplayNameL("IntroSkipOptions_MarkerEnabledLibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_MarkerEnabledLibraryScope_Intro_detection_enabled_library_scope__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(MarkerEnabledLibraryList))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        [Browsable(false)]
        public string MarkerEnabledLibraryScope
        {
            get => string.Empty;
            set { }
        }

        [DisplayNameL("PluginOptions_EnableIntroSkip_Enable_Intro_Skip__Experimental_", typeof(Resources))]
        [DescriptionL("PluginOptions_EnableIntroSkip_Enable_intro_skip_and_credits_skip_for_episodes__Default_is_False_", typeof(Resources))]
        public bool EnableIntroSkip { get; set; } = false;

        [DisplayNameL("IntroSkipOptions_MaxIntroDurationSeconds", typeof(Resources))]
        [MinValue(10), MaxValue(600)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MaxIntroDurationSeconds { get; set; } = 150;

        [DisplayNameL("IntroSkipOptions_MaxCreditsDurationSeconds", typeof(Resources))]
        [MinValue(10), MaxValue(600)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MaxCreditsDurationSeconds { get; set; } = 360;

        [DisplayNameL("IntroSkipOptions_MinOpeningPlotDurationSeconds", typeof(Resources))]
        [MinValue(30), MaxValue(120)]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public int MinOpeningPlotDurationSeconds { get; set; } = 60;

        [DisplayNameL("IntroDbOptions_EditorTitle_IntroDb", typeof(Resources))]
        public IntroDbOptions IntroDbOptions { get; set; } = new IntroDbOptions();

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> LibraryList { get; set; }

        [DisplayNameL("IntroSkipOptions_LibraryScope_Library_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_LibraryScope_TV_shows_library_scope_to_detect__Blank_includes_all_", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(LibraryList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string LibraryScope { get; set; } = string.Empty;

        [Browsable(false)]
        public IEnumerable<EditorSelectOption> UserList { get; set; }

        [DisplayNameL("IntroSkipOptions_UserScope_User_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_UserScope_Users_allowed_to_detect__Blank_includes_all", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(UserList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string UserScope { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_ClientScope_Client_Scope", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_ClientScope_Allowed_clients__Default_is_Emby_Infuse_SenPlayer", typeof(Resources))]
        [Required]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string ClientScope { get; set; } = "Emby,Infuse,SenPlayer";

        public enum IntroSkipPreference
        {
            [DescriptionL("IntroSkipControl_ResetAndOverwrite_ResetAndOverwrite", typeof(Resources))]
            ResetAndOverwrite,
            [DescriptionL("IntroSkipPreference_NoDetectionButReset_NoDetectionButReset", typeof(Resources))]
            NoDetectionButReset
        }

        [Browsable(false)]
        public List<EditorSelectOption> IntroSkipPreferenceList { get; set; } = new List<EditorSelectOption>();

        [DisplayNameL("IntroSkipOptions_IntroSkipPreferences_IntroSkip_Preferences", typeof(Resources))]
        [EditMultilSelect]
        [SelectItemsSource(nameof(IntroSkipPreferenceList))]
        [VisibleCondition(nameof(EnableIntroSkip), SimpleCondition.IsTrue)]
        public string IntroSkipPreferences { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_FingerprintExcludeKeywords_Fingerprint_Exclude_Keywords", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_FingerprintExcludeKeywords_Comma_separated_keywords_or_wildcards__Programs_or_seasons_matching_the_keywords_or_tagged_fingerprint_exclude_are_skipped__Default_is_EMPTY_", typeof(Resources))]
        [VisibleCondition(nameof(UnlockIntroSkip), SimpleCondition.IsTrue)]
        public string FingerprintExcludeKeywords { get; set; } = string.Empty;

        [DisplayNameL("IntroSkipOptions_ShowRedetectIntroCommand_Show_Re_detect_Intro_Command", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_ShowRedetectIntroCommand_Show_a_Re_detect_Intro_command_for_a_series_or_a_season_in_the_shortcut_menu__It_clears_that_season_s_existing_intro_and_credits_markers_and_runs_intro_detection_again__Default_is_ON_", typeof(Resources))]
        public bool ShowRedetectIntroCommand { get; set; } = true;

        [DisplayNameL("IntroSkipOptions_FingerprintLibraryFilePath_Fingerprint_Library_File", typeof(Resources))]
        [DescriptionL("IntroSkipOptions_FingerprintLibraryFilePath_JSON_file_used_by_the_export_and_import_tasks__Blank_uses_the_plugin_data_folder__Default_is_EMPTY_", typeof(Resources))]
        public string FingerprintLibraryFilePath { get; set; } = string.Empty;
    }
}
