using Emby.Media.Common.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.ModOptions;

namespace StrmAssistant.Options
{
    public static class Utility
    {
        private static HashSet<string> _selectedExclusiveFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _selectedCatchupTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _selectedIntroSkipPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string[] _includeItemTypes = Array.Empty<string>();

        public static void UpdateExclusiveControlFeatures(string currentScope)
        {
            _selectedExclusiveFeatures = new HashSet<string>(
                currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => !(f == ExclusiveControl.CatchAllAllow.ToString() &&
                                  currentScope.Contains(ExclusiveControl.CatchAllBlock.ToString()))) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsExclusiveFeatureSelected(params ExclusiveControl[] featuresToCheck)
        {
            return featuresToCheck.Any(f => _selectedExclusiveFeatures.Contains(f.ToString()));
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

        public static void UpdateCatchupScope(string currentScope)
        {
            _selectedCatchupTasks = new HashSet<string>(
                currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
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

        public static void UpdateIntroSkipPreferences(string currentPreferences)
        {
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

        public static void UpdateSearchScope(string currentScope)
        {
            var searchScope = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                              Array.Empty<string>();

            var includeItemTypes = new List<string>();

            foreach (var scope in searchScope)
            {
                if (Enum.TryParse(scope, true, out SearchItemType type))
                {
                    switch (type)
                    {
                        case SearchItemType.Book:
                            includeItemTypes.AddRange(new[] { "Book" });
                            break;
                        case SearchItemType.Collection:
                            includeItemTypes.AddRange(new[] { "BoxSet" });
                            break;
                        case SearchItemType.Episode:
                            includeItemTypes.AddRange(new[] { "Episode" });
                            break;
                        case SearchItemType.Game:
                            includeItemTypes.AddRange(new[] { "Game", "GameSystem" });
                            break;
                        case SearchItemType.Genre:
                            includeItemTypes.AddRange(new[] { "MusicGenre", "GameGenre", "Genre" });
                            break;
                        case SearchItemType.LiveTv:
                            includeItemTypes.AddRange(new[] { "LiveTvChannel", "LiveTvProgram", "LiveTvSeries" });
                            break;
                        case SearchItemType.Movie:
                            includeItemTypes.AddRange(new[] { "Movie" });
                            break;
                        case SearchItemType.Music:
                            includeItemTypes.AddRange(new[] { "Audio", "MusicVideo" });
                            break;
                        case SearchItemType.MusicAlbum:
                            includeItemTypes.AddRange(new[] { "MusicAlbum" });
                            break;
                        case SearchItemType.Person:
                            includeItemTypes.AddRange(new[] { "Person" });
                            break;
                        case SearchItemType.MusicArtist:
                            includeItemTypes.AddRange(new[] { "MusicArtist" });
                            break;
                        case SearchItemType.Photo:
                            includeItemTypes.AddRange(new[] { "Photo" });
                            break;
                        case SearchItemType.PhotoAlbum:
                            includeItemTypes.AddRange(new[] { "PhotoAlbum" });
                            break;
                        case SearchItemType.Playlist:
                            includeItemTypes.AddRange(new[] { "Playlist" });
                            break;
                        case SearchItemType.Series:
                            includeItemTypes.AddRange(new[] { "Series" });
                            break;
                        case SearchItemType.Season:
                            includeItemTypes.AddRange(new[] { "Season" });
                            break;
                        case SearchItemType.Studio:
                            includeItemTypes.AddRange(new[] { "Studio" });
                            break;
                        case SearchItemType.Tag:
                            includeItemTypes.AddRange(new[] { "Tag" });
                            break;
                        case SearchItemType.Trailer:
                            includeItemTypes.AddRange(new[] { "Trailer" });
                            break;
                    }
                }
            }

            _includeItemTypes = includeItemTypes.ToArray();
        }

        public static string[] GetSearchScope()
        {
            return _includeItemTypes;
        }
    }
}
