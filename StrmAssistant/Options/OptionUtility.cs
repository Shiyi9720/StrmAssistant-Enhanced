using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;

namespace StrmAssistant.Options
{
    public static class Utility
    {
        private static HashSet<string> _selectedExclusiveFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> ItemExclusiveFeatures =
            new ConcurrentDictionary<long, ConcurrentDictionary<string, byte>>();

        private static HashSet<string> _selectedCatchupTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _selectedIntroSkipPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void InitializeOptionCache()
        {
            UpdateExclusiveControlFeatures();
            UpdateCatchupScope();
            UpdateIntroSkipPreferences();
        }

        public static void UpdateExclusiveControlFeatures(MediaInfoExtractOptions options = null)
        {
            options ??= Plugin.Instance.GetPluginOptions().MediaInfoExtractOptions;

            var featureSet = new HashSet<string>(
                options.ExclusiveControlFeatures?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            if (options.PersistMediaInfoMode == PersistMediaInfoOption.Restore.ToString())
            {
                featureSet.Add(ExclusiveControl.IgnoreFileChange.ToString());
                featureSet.Add(ExclusiveControl.CatchAllBlock.ToString());
            }
            else if (options.PersistMediaInfoMode == PersistMediaInfoOption.None.ToString())
            {
                featureSet.Add(ExclusiveControl.NoPersistIntegration.ToString());
            }

            _selectedExclusiveFeatures = featureSet;
        }

        public static bool IsExclusiveFeatureSelected(long? itemId = null,
            params ExclusiveControl[] featuresToCheck)
        {
            if (itemId.HasValue && ItemExclusiveFeatures.TryGetValue(itemId.Value, out var itemFeatures))
            {
                return featuresToCheck.Any(f => itemFeatures.ContainsKey(f.ToString()));
            }

            return featuresToCheck.Any(f => _selectedExclusiveFeatures.Contains(f.ToString()));
        }

        public static bool IsExclusiveFeatureSelected(params ExclusiveControl[] featuresToCheck)
        {
            return IsExclusiveFeatureSelected(null, featuresToCheck);
        }

        public static void EnableItemExclusiveFeatures(long itemId, params ExclusiveControl[] features)
        {
            var itemFeatures = ItemExclusiveFeatures.GetOrAdd(itemId, _ => new ConcurrentDictionary<string, byte>());

            foreach (var feature in features)
            {
                itemFeatures.TryAdd(feature.ToString(), 0);
            }
        }

        public static void DisableItemExclusiveFeatures(long itemId, params ExclusiveControl[] features)
        {
            if (ItemExclusiveFeatures.TryGetValue(itemId, out var itemFeatures))
            {
                foreach (var feature in features)
                {
                    itemFeatures.TryRemove(feature.ToString(), out _);
                }

                if (itemFeatures.IsEmpty)
                {
                    ItemExclusiveFeatures.TryRemove(itemId, out _);
                }
            }
        }

        public static void ClearItemExclusiveFeatures(long itemId)
        {
            ItemExclusiveFeatures.TryRemove(itemId, out _);
        }

        public static string GetSelectedExclusiveFeatureDescription()
        {
            return string.Join(", ",
                _selectedExclusiveFeatures
                    .Select(feature =>
                        Enum.TryParse(feature.Trim(), true, out ExclusiveControl type)
                            ? type
                            : (ExclusiveControl?)null)
                    .Where(type => type.HasValue)
                    .OrderBy(type => type)
                    .Select(type => type.Value.GetDescription()));
        }


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
