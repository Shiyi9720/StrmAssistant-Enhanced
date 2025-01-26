using Emby.Web.GenericEdit.Common;
using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.UIFunctionOptions;

namespace StrmAssistant.Options.Store
{
    public class ExperienceEnhanceOptionsStore : SimpleFileStore<ExperienceEnhanceOptions>
    {
        private readonly ILogger _logger;

        private bool _currentSuppressOnOptionsSaved;

        public ExperienceEnhanceOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger = logger;

            FileSaving += OnFileSaving;
            FileSaved += OnFileSaved;
        }
        
        public ExperienceEnhanceOptions ExperienceEnhanceOptions => GetOptions();

        public void SavePluginOptionsSuppress()
        {
            _currentSuppressOnOptionsSaved = true;
            SetOptions(ExperienceEnhanceOptions);
        }

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is ExperienceEnhanceOptions options)
            {
                if (string.IsNullOrEmpty(options.UIFunctionOptions.HidePersonPreference))
                {
                    options.UIFunctionOptions.HidePersonPreference = HidePersonOption.NoImage.ToString();
                }

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(ExperienceEnhanceOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));
                
                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.MergeMultiVersion)))
                {
                    if (options.MergeMultiVersion)
                    {
                        MergeMultiVersion.Patch();
                    }
                    else
                    {
                        MergeMultiVersion.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.EnhanceNotificationSystem)))
                {
                    if (options.EnhanceNotificationSystem)
                    {
                        EnhanceNotificationSystem.Patch();
                    }
                    else
                    {
                        EnhanceNotificationSystem.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.HidePersonNoImage)))
                {
                    if (options.UIFunctionOptions.HidePersonNoImage)
                    {
                        HidePersonNoImage.Patch();
                    }
                    else
                    {
                        HidePersonNoImage.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.EnforceLibraryOrder)))
                {
                    if (options.UIFunctionOptions.EnforceLibraryOrder)
                    {
                        EnforceLibraryOrder.Patch();
                    }
                    else
                    {
                        EnforceLibraryOrder.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.BeautifyMissingMetadata)))
                {
                    if (options.UIFunctionOptions.BeautifyMissingMetadata)
                    {
                        BeautifyMissingMetadata.Patch();
                    }
                    else
                    {
                        BeautifyMissingMetadata.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.EnhanceMissingEpisodes)))
                {
                    if (options.UIFunctionOptions.EnhanceMissingEpisodes)
                    {
                        EnhanceMissingEpisodes.Patch();
                    }
                    else
                    {
                        EnhanceMissingEpisodes.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(ExperienceEnhanceOptions.UIFunctionOptions.NoBoxsetsAutoCreation)))
                {
                    if (options.UIFunctionOptions.NoBoxsetsAutoCreation)
                    {
                        NoBoxsetsAutoCreation.Patch();
                    }
                    else
                    {
                        NoBoxsetsAutoCreation.Unpatch();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is ExperienceEnhanceOptions options)
            {
                var suppressLogger = _currentSuppressOnOptionsSaved;

                if (!suppressLogger)
                {
                    _logger.Info("MergeMultiVersion is set to {0}", options.MergeMultiVersion);
                    _logger.Info("EnhanceNotificationSystem is set to {0}", options.EnhanceNotificationSystem);
                    _logger.Info("HidePersonNoImage is set to {0}", options.UIFunctionOptions.HidePersonNoImage);
                    var hidePersonPreference = string.Join(", ",
                        options.UIFunctionOptions.HidePersonPreference
                            ?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s =>
                                Enum.TryParse(s.Trim(), true, out HidePersonOption option)
                                    ? option.GetDescription()
                                    : null)
                            .Where(d => d != null) ?? Enumerable.Empty<string>());
                    _logger.Info("HidePersonPreference is set to {0}", hidePersonPreference);
                    _logger.Info("EnforceLibraryOrder is set to {0}", options.UIFunctionOptions.EnforceLibraryOrder);
                    _logger.Info("BeautifyMissingMetadata is set to {0}",
                        options.UIFunctionOptions.BeautifyMissingMetadata);
                    _logger.Info("EnhanceMissingEpisodes is set to {0}",
                        options.UIFunctionOptions.EnhanceMissingEpisodes);
                    _logger.Info("NoBoxsetsAutoCreation is set to {0}",
                        options.UIFunctionOptions.NoBoxsetsAutoCreation);
                }

                if (suppressLogger) _currentSuppressOnOptionsSaved = false;
            }
        }
    }
}
