using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using StrmAssistant.ScheduledTask;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnhanceMovieDbPerson : PatchBase<EnhanceMovieDbPerson>
    {
        private static Assembly _movieDbAssembly;

        private static MethodInfo _movieDbPersonProviderImportData;
        private static PropertyInfo _nameProperty;
        private static PropertyInfo _alsoKnownAsProperty;
        private static PropertyInfo _biographyProperty;
        private static PropertyInfo _placeOfBirthProperty;

        private static MethodInfo _movieDbSeasonProviderImportData;
        private static MethodInfo _seasonGetMetadata;

        private static PropertyInfo _seasonCreditsProperty;
        private static PropertyInfo _castListProperty;

        private static PropertyInfo _castIdProperty;
        private static PropertyInfo _castOrderProperty;
        private static PropertyInfo _castNameProperty;
        private static PropertyInfo _castCharacterProperty;
        private static PropertyInfo _castProfilePathProperty;

        private static readonly ConcurrentDictionary<Season, List<PersonInfo>> SeasonPersonInfoDictionary =
            new ConcurrentDictionary<Season, List<PersonInfo>>();

        public EnhanceMovieDbPerson()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().EnhanceMovieDbPerson)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _movieDbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");

            if (_movieDbAssembly != null)
            {
                var movieDbPersonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbPersonProvider");
                _movieDbPersonProviderImportData = movieDbPersonProvider.GetMethod("ImportData",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var personResult = movieDbPersonProvider.GetNestedType("PersonResult", BindingFlags.Public);
                _nameProperty = personResult.GetProperty("name");
                _alsoKnownAsProperty = personResult.GetProperty("also_known_as");
                _biographyProperty = personResult.GetProperty("biography");
                _placeOfBirthProperty = personResult.GetProperty("place_of_birth");

                var movieDbSeasonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeasonProvider");
                _movieDbSeasonProviderImportData =
                    movieDbSeasonProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                _seasonGetMetadata = movieDbSeasonProvider.GetMethod("GetMetadata",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteMetadataFetchOptions<SeasonInfo>), typeof(CancellationToken) }, null);
                var seasonRootObject = movieDbSeasonProvider.GetNestedType("SeasonRootObject", BindingFlags.Public);
                _seasonCreditsProperty = seasonRootObject.GetProperty("credits");

                var tmdbCredits = _movieDbAssembly.GetType("MovieDb.TmdbCredits");
                _castListProperty = tmdbCredits.GetProperty("cast");

                var tmdbCast = _movieDbAssembly.GetType("MovieDb.TmdbCast");
                _castIdProperty = tmdbCast.GetProperty("id");
                _castOrderProperty = tmdbCast.GetProperty("order");
                _castNameProperty = tmdbCast.GetProperty("name");
                _castCharacterProperty = tmdbCast.GetProperty("character");
                _castProfilePathProperty = tmdbCast.GetProperty("profile_path");
            }
            else
            {
                Plugin.Instance.Logger.Info("EnhanceMovieDbPerson - MovieDb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _movieDbPersonProviderImportData,
                prefix: nameof(PersonImportDataPrefix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderImportData,
                prefix: nameof(SeasonImportDataPrefix));
            PatchUnpatch(PatchTracker, apply, _seasonGetMetadata, postfix: nameof(SeasonGetMetadataPostfix));
        }

        private static Tuple<string, bool> ProcessPersonInfoAsExpected(string input, string placeOfBirth)
        {
            var isJapaneseFallback = HasMovieDbJapaneseFallback();

            var considerJapanese = isJapaneseFallback && placeOfBirth != null &&
                                   placeOfBirth.IndexOf("Japan", StringComparison.Ordinal) >= 0;

            if (IsChinese(input))
            {
                input = ConvertTraditionalToSimplified(input);
            }

            if (!considerJapanese ? IsChinese(input) : IsChineseJapanese(input))
            {
                return new Tuple<string, bool>(input, true);
            }

            return new Tuple<string, bool>(input, false);
        }

        [HarmonyPrefix]
        private static bool PersonImportDataPrefix(Person item, object info, bool isFirstLanguage)
        {
            if (!RefreshPersonTask.IsRunning) return true;

            var placeOfBirth = _placeOfBirthProperty?.GetValue(info) as string;

            if (_nameProperty?.GetValue(info) is string infoPersonName)
            {
                var updateNameResult = ProcessPersonInfoAsExpected(infoPersonName, placeOfBirth);

                if (updateNameResult.Item2)
                {
                    if (!string.Equals(infoPersonName, CleanPersonName(updateNameResult.Item1),
                            StringComparison.Ordinal))
                        _nameProperty.SetValue(info, updateNameResult.Item1);
                }
                else
                {
                    if (_alsoKnownAsProperty?.GetValue(info) is List<object> alsoKnownAsList)
                    {
                        foreach (var alias in alsoKnownAsList)
                        {
                            if (alias is string aliasString && !string.IsNullOrEmpty(aliasString))
                            {
                                var updateAliasResult = ProcessPersonInfoAsExpected(aliasString, placeOfBirth);
                                if (updateAliasResult.Item2)
                                {
                                    _nameProperty.SetValue(info, updateAliasResult.Item1);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            if (_biographyProperty?.GetValue(info) is string infoBiography)
            {
                var updateBiographyResult = ProcessPersonInfoAsExpected(infoBiography, placeOfBirth);

                if (updateBiographyResult.Item2)
                {
                    if (!string.Equals(infoBiography, updateBiographyResult.Item1, StringComparison.Ordinal))
                        _biographyProperty.SetValue(info, updateBiographyResult.Item1);
                }
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool SeasonImportDataPrefix(Season item, object seasonInfo, string name, int seasonNumber,
            bool isFirstLanguage)
        {
            if (isFirstLanguage)
            {
                var cast =
                    (_castListProperty.GetValue(_seasonCreditsProperty.GetValue(seasonInfo)) as IEnumerable<object>)
                    ?.OrderBy(a => (int)_castOrderProperty.GetValue(a));

                if (cast != null)
                {
                    var personInfoList = new List<PersonInfo>();

                    foreach (var actor in cast)
                    {
                        var id = (int)_castIdProperty.GetValue(actor);
                        var actorName = ((string)_castNameProperty.GetValue(actor)).Trim();
                        var character = ((string)_castCharacterProperty.GetValue(actor)).Trim();
                        var profilePath = (string)_castProfilePathProperty.GetValue(actor);

                        var personInfo = new PersonInfo { Name = actorName, Role = character, Type = PersonType.Actor };

                        if (!string.IsNullOrWhiteSpace(profilePath))
                        {
                            personInfo.ImageUrl = "https://image.tmdb.org/t/p/original" + profilePath;
                        }

                        if (id > 0)
                        {
                            personInfo.SetProviderId(MetadataProviders.Tmdb, id.ToString(CultureInfo.InvariantCulture));
                        }

                        personInfoList.Add(personInfo);
                    }

                    SeasonPersonInfoDictionary[item] = personInfoList;
                }
            }

            return true;
        }

        [HarmonyPostfix]
        private static void SeasonGetMetadataPostfix(RemoteMetadataFetchOptions<SeasonInfo> options,
            CancellationToken cancellationToken, Task<MetadataResult<Season>> __result)
        {
            __result.ContinueWith(task =>
                {
                    if (task.IsCompletedSuccessfully)
                    {
                        var result = task.Result;

                        if (result.Item != null &&
                            SeasonPersonInfoDictionary.TryGetValue(result.Item, out var personInfoList))
                        {
                            foreach (var personInfo in personInfoList)
                            {
                                result.AddPerson(personInfo);
                            }

                            SeasonPersonInfoDictionary.TryRemove(result.Item, out _);
                        }
                    }
                }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .ConfigureAwait(false);
        }
    }
}
