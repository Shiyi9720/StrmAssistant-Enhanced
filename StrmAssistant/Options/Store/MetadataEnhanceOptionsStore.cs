using Emby.Web.GenericEdit.PropertyDiff;
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using StrmAssistant.Options.UIBaseClasses.Store;
using System;
using System.Collections.Generic;
using System.Linq;

namespace StrmAssistant.Options.Store
{
    public class MetadataEnhanceOptionsStore : SimpleFileStore<MetadataEnhanceOptions>
    {
        private readonly ILogger _logger;

        public MetadataEnhanceOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
            : base(applicationHost, logger, pluginFullName)
        {
            _logger= logger;

            FileSaved += OnFileSaved;
            FileSaving += OnFileSaving;
        }

        public MetadataEnhanceOptions MetadataEnhanceOptions => GetOptions();

        private void OnFileSaving(object sender, FileSavingEventArgs e)
        {
            if (e.Options is MetadataEnhanceOptions options)
            {
                if (string.IsNullOrEmpty(options.FallbackLanguages))
                {
                    options.FallbackLanguages = "zh-sg";
                }
                else
                {
                    var languages = options.FallbackLanguages.Split(',');
                    options.FallbackLanguages = string.Join(",",
                        LanguageUtility.MovieDbFallbackLanguages
                            .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase))
                            .Select(l => l.ToLowerInvariant()));
                }

                if (string.IsNullOrEmpty(options.TvdbFallbackLanguages))
                {
                    options.TvdbFallbackLanguages = "zhtw";
                }
                else
                {
                    var languages = options.TvdbFallbackLanguages.Split(',');
                    options.TvdbFallbackLanguages = string.Join(",",
                        LanguageUtility.TvdbFallbackLanguages
                            .Where(l => languages.Contains(l, StringComparer.OrdinalIgnoreCase)));
                }

                options.ShowBlockNonFallbackLanguage = options.ChineseMovieDb || options.ChineseTvdb;

                options.AltMovieDbApiUrl =
                    !string.IsNullOrWhiteSpace(options.AltMovieDbApiUrl)
                        ? options.AltMovieDbApiUrl.Trim().TrimEnd('/')
                        : options.AltMovieDbApiUrl?.Trim();

                options.AltMovieDbImageUrl =
                    !string.IsNullOrWhiteSpace(options.AltMovieDbImageUrl)
                        ? options.AltMovieDbImageUrl.Trim().TrimEnd('/')
                        : options.AltMovieDbImageUrl?.Trim();

                var changes = PropertyChangeDetector.DetectObjectPropertyChanges(MetadataEnhanceOptions, options);
                var changedProperties = new HashSet<string>(changes.Select(c => c.PropertyName));

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.ChineseMovieDb)))
                {
                    if (options.ChineseMovieDb)
                    {
                        ChineseMovieDb.Patch();
                    }
                    else
                    {
                        ChineseMovieDb.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.ChineseTvdb)))
                {
                    if (options.ChineseTvdb)
                    {
                        ChineseTvdb.Patch();
                    }
                    else
                    {
                        ChineseTvdb.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.MovieDbEpisodeGroup)))
                {
                    if (options.MovieDbEpisodeGroup)
                    {
                        MovieDbEpisodeGroup.Patch();
                    }
                    else
                    {
                        MovieDbEpisodeGroup.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.EnhanceMovieDbPerson)))
                {
                    if (options.EnhanceMovieDbPerson)
                    {
                        EnhanceMovieDbPerson.Patch();
                    }
                    else
                    {
                        EnhanceMovieDbPerson.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.AltMovieDbConfig)))
                {
                    if (options.AltMovieDbConfig)
                    {
                        AltMovieDbConfig.PatchApiUrl();
                        if (!string.IsNullOrEmpty(options.AltMovieDbImageUrl)) AltMovieDbConfig.PatchImageUrl();
                    }
                    else
                    {
                        AltMovieDbConfig.UnpatchApiUrl();
                        if (!string.IsNullOrEmpty(MetadataEnhanceOptions.AltMovieDbImageUrl))
                            AltMovieDbConfig.UnpatchImageUrl();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.AltMovieDbImageUrl)))
                {
                    if (!string.IsNullOrEmpty(options.AltMovieDbImageUrl))
                    {
                        AltMovieDbConfig.PatchImageUrl();
                    }
                    else
                    {
                        AltMovieDbConfig.UnpatchImageUrl();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.PreferOriginalPoster)))
                {
                    if (options.PreferOriginalPoster)
                    {
                        PreferOriginalPoster.Patch();
                    }
                    else
                    {
                        PreferOriginalPoster.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.PinyinSortName)))
                {
                    if (options.PinyinSortName)
                    {
                        PinyinSortName.Patch();
                    }
                    else
                    {
                        PinyinSortName.Unpatch();
                    }
                }

                if (changedProperties.Contains(nameof(MetadataEnhanceOptions.EnhanceNfoMetadata)))
                {
                    if (options.EnhanceNfoMetadata)
                    {
                        EnhanceNfoMetadata.Patch();
                    }
                    else
                    {
                        EnhanceNfoMetadata.Unpatch();
                    }
                }
            }
        }

        private void OnFileSaved(object sender, FileSavedEventArgs e)
        {
            if (e.Options is MetadataEnhanceOptions options)
            {
                _logger.Info("ChineseMovieDb is set to {0}", options.ChineseMovieDb);
                _logger.Info("MovieDbFallbackLanguages is set to {0}", options.FallbackLanguages);
                _logger.Info("ChineseTvdb is set to {0}", options.ChineseTvdb);
                _logger.Info("TvdbFallbackLanguages is set to {0}", options.TvdbFallbackLanguages);
                _logger.Info("BlockNonFallbackLanguage is set to {0}", options.BlockNonFallbackLanguage);
                _logger.Info("MovieDbEpisodeGroup is set to {0}", options.MovieDbEpisodeGroup);
                _logger.Info("LocalEpisodeGroup is set to {0}", options.LocalEpisodeGroup);
                _logger.Info("EnhanceMovieDbPerson is set to {0}", options.EnhanceMovieDbPerson);
                _logger.Info("AltMovieDbConfig is set to {0}", options.AltMovieDbConfig);
                _logger.Info("AltMovieDbApiUrl is set to {0}",
                    !string.IsNullOrEmpty(options.AltMovieDbApiUrl)
                        ? options.AltMovieDbApiUrl
                        : "EMPTY");
                _logger.Info("AltMovieDbImageUrl is set to {0}",
                    !string.IsNullOrEmpty(options.AltMovieDbImageUrl)
                        ? options.AltMovieDbImageUrl
                        : "EMPTY");
                _logger.Info("AltMovieDbApiKey is set to {0}",
                    !string.IsNullOrEmpty(options.AltMovieDbApiKey)
                        ? options.AltMovieDbApiKey
                        : "EMPTY");
                _logger.Info("PreferOriginalPoster is set to {0}", options.PreferOriginalPoster);
                _logger.Info("PinyinSortName is set to {0}", options.PinyinSortName);
                _logger.Info("EnhanceNfoMetadata is set to {0}", options.EnhanceNfoMetadata);
            }
        }
    }
}
