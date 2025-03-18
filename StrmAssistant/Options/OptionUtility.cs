using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.IntroSkipOptions;

namespace StrmAssistant.Options
{
    public static class Utility
    {
        private static HashSet<string> _selectedCatchupTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _selectedIntroSkipPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void UpdateCatchupScope()
        {
            var catchupTaskScope = Plugin.Instance.GetPluginOptions().GeneralOptions.CatchupTaskScope;

            _selectedCatchupTasks = new HashSet<string>(
                catchupTaskScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsCatchupTaskSelected(params CatchupTask[] tasksToCheck)
        {
            return tasksToCheck.Any(f => _selectedCatchupTasks.Contains(f.ToString()));
        }

        public static string GetSelectedCatchupTaskDescription()
        {
            return string.Join(", ",
                _selectedCatchupTasks
                    .Select(task =>
                        Enum.TryParse(task.Trim(), true, out CatchupTask type)
                            ? type
                            : (CatchupTask?)null)
                    .Where(type => type.HasValue)
                    .OrderBy(type => type)
                    .Select(type => type.Value.GetDescription()));
        }

        public static void UpdateIntroSkipPreferences()
        {
            var currentPreferences = Plugin.Instance.GetPluginOptions().IntroSkipOptions.IntroSkipPreferences;

            _selectedIntroSkipPreferences = new HashSet<string>(
                currentPreferences?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsIntroSkipPreferenceSelected(params IntroSkipPreference[] preferencesToCheck)
        {
            return preferencesToCheck.Any(f => _selectedIntroSkipPreferences.Contains(f.ToString()));
        }

        public static string GetSelectedIntroSkipPreferenceDescription()
        {
            return string.Join(", ",
                _selectedIntroSkipPreferences
                    .Select(pref =>
                        Enum.TryParse(pref.Trim(), true, out IntroSkipPreference type)
                            ? type
                            : (IntroSkipPreference?)null)
                    .Where(type => type.HasValue)
                    .OrderBy(type => type)
                    .Select(type => type.Value.GetDescription()));
        }

        public static string[] GetValidLibraryIds(string scope)
        {
            var libraryIds = scope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var validLibraryIds = Array.Empty<string>();

            if (libraryIds?.Any() is true)
            {
                var parsedIds = libraryIds.Select(id => long.TryParse(id, out var result) ? result : (long?)null)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .ToArray();

                if (parsedIds.Any())
                {
                    validLibraryIds = BaseItem.LibraryManager
                        .GetInternalItemIds(new InternalItemsQuery { ItemIds = parsedIds })
                        .Select(id => id.ToString())
                        .ToArray();
                }
            }

            return validLibraryIds;
        }
    }
}
