using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using StrmAssistant.Web.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Serves and persists the per-library intro detection fingerprint settings. Everything runs
    /// through the plugin's own routes: the Emby generic options page cannot render
    /// <see cref="LibraryIntroDetectionFingerprintOptionCollection"/> on 4.10 without crashing
    /// the process with a stack overflow.
    /// </summary>
    public class IntroDetectionSettingsService : IService, IRequiresRequest
    {
        private const int MinMinutes = 2;
        private const int MaxMinutes = 20;
        private const string PageResourceName = "introsettings.html";

        private readonly IHttpResultFactory _resultFactory;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public IRequest Request { get; set; }

        public IntroDetectionSettingsService(IHttpResultFactory resultFactory, ILibraryManager libraryManager)
        {
            _resultFactory = resultFactory;
            _libraryManager = libraryManager;
            _logger = Plugin.Instance.Logger;
        }

        public object Get(GetIntroDetectionSettingsPage request)
        {
            return _resultFactory.GetResult(Request, BuildPage().AsSpan(), "text/html; charset=utf-8");
        }

        public object Get(GetIntroDetectionSettings request)
        {
            var introOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;
            var current = introOptions.LibraryIntroDetectionFingerprintOptions ??
                          new LibraryIntroDetectionFingerprintOptionCollection();

            var response = new IntroDetectionSettingsResponse
            {
                GlobalMinutes = introOptions.IntroDetectionFingerprintMinutes,
                IntroSkipEnabled = introOptions.UnlockIntroSkip
            };

            foreach (var library in GetTvLibraries())
            {
                var existing = current.FindByLibraryId(library.ItemId);

                response.Libraries.Add(new IntroDetectionLibrarySetting
                {
                    LibraryId = library.ItemId,
                    LibraryName = library.Name,
                    Override = existing?.Override ?? false,
                    Minutes = existing?.IntroDetectionFingerprintMinutes ??
                              introOptions.IntroDetectionFingerprintMinutes
                });
            }

            // Keep overrides that were configured while the library was in scope, even if it no
            // longer shows up as a TV library, so saving the page cannot silently drop them.
            foreach (var option in current.Overrides().Where(o =>
                         response.Libraries.All(l => !string.Equals(l.LibraryId, o.LibraryId,
                             StringComparison.OrdinalIgnoreCase))))
            {
                response.Libraries.Add(new IntroDetectionLibrarySetting
                {
                    LibraryId = option.LibraryId,
                    LibraryName = option.LibraryName,
                    Override = true,
                    Minutes = option.IntroDetectionFingerprintMinutes
                });
            }

            return response;
        }

        public object Post(SaveIntroDetectionSettings request)
        {
            if (request.Libraries is null)
            {
                return new SaveIntroDetectionSettingsResponse { Success = false };
            }

            var libraryNames = GetTvLibraries()
                .ToDictionary(l => l.ItemId, l => l.Name, StringComparer.OrdinalIgnoreCase);
            var current = Plugin.Instance.GetPluginOptions().IntroSkipOptions
                              .LibraryIntroDetectionFingerprintOptions ??
                          new LibraryIntroDetectionFingerprintOptionCollection();

            var settings = new List<LibraryIntroDetectionFingerprintOption>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in request.Libraries)
            {
                if (string.IsNullOrEmpty(item.LibraryId) || !seenIds.Add(item.LibraryId)) continue;

                if (!libraryNames.TryGetValue(item.LibraryId, out var libraryName))
                {
                    var existing = current.FindByLibraryId(item.LibraryId);
                    if (existing is null) continue;
                    libraryName = existing.LibraryName;
                }

                settings.Add(new LibraryIntroDetectionFingerprintOption
                {
                    LibraryId = item.LibraryId,
                    LibraryName = libraryName,
                    Override = item.Override,
                    IntroDetectionFingerprintMinutes = Math.Max(MinMinutes, Math.Min(MaxMinutes, item.Minutes))
                });
            }

            try
            {
                Plugin.Instance.SaveLibraryIntroDetectionFingerprintOptions(settings);

                _logger?.Info("IntroDetectionSettings - Saved " + settings.Count(o => o.Override) +
                              " library override(s)");

                return new SaveIntroDetectionSettingsResponse { Success = true };
            }
            catch (Exception e)
            {
                _logger?.Error("IntroDetectionSettings - Save failed");
                _logger?.Error(e.Message);
                _logger?.Debug(e.StackTrace);

                return new SaveIntroDetectionSettingsResponse { Success = false };
            }
        }

        private List<VirtualFolderInfo> GetTvLibraries()
        {
            return _libraryManager.GetVirtualFolders()
                .Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null)
                .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static string BuildPage()
        {
            var introOptions = Plugin.Instance.GetPluginOptions().IntroSkipOptions;

            var description = Html(Resources
                    .IntroDetectionSettings_Description_Override_the_global_intro_detection_fingerprint_length_for_individual_TV_libraries__Global_value_is__0__minutes__Valid_range_is_2_to_20_minutes_)
                .Replace("{0}", introOptions.IntroDetectionFingerprintMinutes.ToString());

            return ReadEmbeddedResource()
                .Replace("@@TITLE@@", Html(Resources
                    .IntroDetectionSettings_Title_Per_Library_Intro_Detection_Length))
                .Replace("@@DESCRIPTION@@", description)
                .Replace("@@COLUMN_LIBRARY@@", Html(Resources.IntroDetectionSettings_Column_Library_Library))
                .Replace("@@COLUMN_OVERRIDE@@", Html(Resources.IntroDetectionSettings_Column_Override_Override))
                .Replace("@@COLUMN_MINUTES@@", Html(Resources.IntroDetectionSettings_Column_Minutes_Minutes))
                .Replace("@@SAVE@@", Html(Resources.IntroDetectionSettings_Save_Save))
                .Replace("@@NO_LIBRARIES@@", Html(Resources.IntroDetectionSettings_NoLibraries_No_TV_libraries_found_))
                .Replace("@@DISABLED@@", Html(Resources
                    .IntroDetectionSettings_Disabled_Built_in_intro_skip_enhancement_is_not_enabled_))
                .Replace("@@SAVED@@", Js(Resources
                    .IntroDetectionSettings_Saved_Settings_saved__they_apply_to_the_next_intro_detection_run_))
                .Replace("@@SAVE_FAILED@@", Js(Resources
                    .IntroDetectionSettings_SaveFailed_Failed_to_save_settings_))
                .Replace("@@LOAD_FAILED@@", Js(Resources
                    .IntroDetectionSettings_LoadFailed_Failed_to_load_settings_));
        }

        private static string ReadEmbeddedResource()
        {
            var assembly = typeof(Plugin).GetTypeInfo().Assembly;
            var name = typeof(Plugin).Namespace + ".Web.Resources." + PageResourceName;

            using (var stream = assembly.GetManifestResourceStream(name))
            {
                if (stream is null)
                {
                    throw new InvalidOperationException("Embedded resource not found: " + name);
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string Js(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'");
        }
    }
}
