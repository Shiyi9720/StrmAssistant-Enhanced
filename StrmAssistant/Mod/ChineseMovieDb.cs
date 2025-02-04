using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class ChineseMovieDb : PatchBase<ChineseMovieDb>
    {
        private static Assembly _movieDbAssembly;
        private static MethodInfo _genericMovieDbInfoIsCompleteMovie;
        private static MethodInfo _genericMovieDbInfoProcessMainInfoMovie;
        private static MethodInfo _genericMovieDbInfoIsCompleteSeries;
        private static MethodInfo _genericMovieDbInfoProcessMainInfoSeries;
        private static MethodInfo _getTitleMovieData;

        private static MethodInfo _getMovieDbMetadataLanguages;
        private static MethodInfo _mapLanguageToProviderLanguage;
        private static MethodInfo _getImageLanguagesParam;
        private static FieldInfo _cacheTime;

        private static MethodInfo _movieDbSeriesProviderIsComplete;
        private static MethodInfo _movieDbSeriesProviderImportData;
        private static MethodInfo _ensureSeriesInfo;
        private static MethodInfo _getTitleSeriesInfo;
        private static PropertyInfo _nameSeriesInfoProperty;
        private static PropertyInfo _alternativeTitleSeriesInfoProperty;
        private static PropertyInfo _alternativeTitleListProperty;
        private static PropertyInfo _alternativeTitle;
        private static PropertyInfo _alternativeTitleCountryCode;
        private static PropertyInfo _genresProperty;
        private static PropertyInfo _genreNameProperty;

        private static MethodInfo _movieDbSeasonProviderIsComplete;
        private static MethodInfo _movieDbSeasonProviderImportData;
        private static PropertyInfo _nameSeasonInfoProperty;
        private static PropertyInfo _overviewSeasonInfoProperty;

        private static MethodInfo _movieDbEpisodeProviderIsComplete;
        private static MethodInfo _movieDbEpisodeProviderImportData;
        private static PropertyInfo _nameEpisodeInfoProperty;
        private static PropertyInfo _overviewEpisodeInfoProperty;

        private static PropertyInfo _seriesInfoTaskResultProperty;

        private static readonly AsyncLocal<string> CurrentLookupLanguageCountryCode = new AsyncLocal<string>();
        private static readonly TimeSpan OriginalCacheTime = TimeSpan.FromHours(6);
        private static readonly TimeSpan NewCacheTime = TimeSpan.FromHours(48);

        public ChineseMovieDb()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMovieDb)
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
                var genericMovieDbInfo = _movieDbAssembly.GetType("MovieDb.GenericMovieDbInfo`1");

                var genericMovieDbInfoMovie = genericMovieDbInfo.MakeGenericType(typeof(Movie));
                _genericMovieDbInfoIsCompleteMovie = genericMovieDbInfoMovie.GetMethod("IsComplete",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _genericMovieDbInfoProcessMainInfoMovie = genericMovieDbInfoMovie.GetMethod("ProcessMainInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var completeMovieData = _movieDbAssembly.GetType("MovieDb.MovieDbProvider")
                    .GetNestedType("CompleteMovieData", BindingFlags.NonPublic);
                _getTitleMovieData = completeMovieData.GetMethod("GetTitle");

                var genericMovieDbInfoSeries = genericMovieDbInfo.MakeGenericType(typeof(Series));
                _genericMovieDbInfoIsCompleteSeries = genericMovieDbInfoSeries.GetMethod("IsComplete",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _genericMovieDbInfoProcessMainInfoSeries = genericMovieDbInfoSeries.GetMethod("ProcessMainInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _getMovieDbMetadataLanguages = movieDbProviderBase.GetMethod("GetMovieDbMetadataLanguages",
                    BindingFlags.Public | BindingFlags.Instance);
                _mapLanguageToProviderLanguage = movieDbProviderBase.GetMethod("MapLanguageToProviderLanguage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _getImageLanguagesParam = movieDbProviderBase.GetMethod("GetImageLanguagesParam",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string[]) }, null);
                _cacheTime = movieDbProviderBase.GetField("CacheTime", BindingFlags.Public | BindingFlags.Static);

                var movieDbSeriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider");
                _movieDbSeriesProviderIsComplete =
                    movieDbSeriesProvider.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _movieDbSeriesProviderImportData =
                    movieDbSeriesProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                _ensureSeriesInfo = movieDbSeriesProvider.GetMethod("EnsureSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var seriesRootObject = movieDbSeriesProvider.GetNestedType("SeriesRootObject", BindingFlags.Public);
                _getTitleSeriesInfo = seriesRootObject.GetMethod("GetTitle");
                _nameSeriesInfoProperty = seriesRootObject.GetProperty("name");
                _alternativeTitleSeriesInfoProperty = seriesRootObject.GetProperty("alternative_titles");
                _alternativeTitleListProperty = _movieDbAssembly.GetType("MovieDb.TmdbAlternativeTitles")
                    .GetProperty("results");
                var tmdbTitleType = _movieDbAssembly.GetType("MovieDb.TmdbTitle");
                _alternativeTitle = tmdbTitleType.GetProperty("title");
                _alternativeTitleCountryCode = tmdbTitleType.GetProperty("iso_3166_1");
                _genresProperty = seriesRootObject.GetProperty("genres");
                _genreNameProperty = _movieDbAssembly.GetType("MovieDb.TmdbGenre").GetProperty("name");

                var movieDbSeasonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeasonProvider");
                _movieDbSeasonProviderIsComplete =
                    movieDbSeasonProvider.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _movieDbSeasonProviderImportData =
                    movieDbSeasonProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                var seasonRootObject = movieDbSeasonProvider.GetNestedType("SeasonRootObject", BindingFlags.Public);
                _nameSeasonInfoProperty = seasonRootObject.GetProperty("name");
                _overviewSeasonInfoProperty = seasonRootObject.GetProperty("overview");

                var movieDbEpisodeProvider = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeProvider");
                _movieDbEpisodeProviderIsComplete =
                    movieDbEpisodeProvider.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _movieDbEpisodeProviderImportData =
                    movieDbEpisodeProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                var episodeRootObject = movieDbProviderBase.GetNestedType("RootObject", BindingFlags.Public);
                _nameEpisodeInfoProperty = episodeRootObject.GetProperty("name");
                _overviewEpisodeInfoProperty = episodeRootObject.GetProperty("overview");
            }
            else
            {
                Plugin.Instance.Logger.Info("ChineseMovieDb - MovieDb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _genericMovieDbInfoIsCompleteMovie, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _genericMovieDbInfoProcessMainInfoMovie,
                prefix: nameof(ProcessMainInfoMoviePrefix));
            /* Not actually needed because a non-generic class is only patched with the last generic concrete type
             * It's fine here because the patch logic is the same and the intention is to patch the generic method
             * https://github.com/pardeike/Harmony/issues/201#issuecomment-821980884
            PatchUnpatch(PatchTracker, apply, _genericMovieDbInfoIsCompleteSeries, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _genericMovieDbInfoProcessMainInfoSeries,
                prefix: nameof(ProcessMainInfoSeriesPrefix));*/
            PatchUnpatch(PatchTracker, apply, _getMovieDbMetadataLanguages, postfix: nameof(MetadataLanguagesPostfix));
            PatchUnpatch(PatchTracker, apply, _getImageLanguagesParam, postfix: nameof(GetImageLanguagesParamPostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeriesProviderIsComplete, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeriesProviderImportData, prefix: nameof(SeriesImportDataPrefix));
            PatchUnpatch(PatchTracker, apply, _ensureSeriesInfo, postfix: nameof(EnsureSeriesInfoPostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderIsComplete, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderImportData, prefix: nameof(SeasonImportDataPrefix));
            PatchUnpatch(PatchTracker, apply, _movieDbEpisodeProviderIsComplete, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbEpisodeProviderImportData,
                prefix: nameof(EpisodeImportDataPrefix));
        }

        public static void PatchCacheTime()
        {
            _cacheTime?.SetValue(null, NewCacheTime);
            Plugin.Instance.Logger.Debug("Patch CacheTime Success by Reflection");
        }

        public static void UnpatchCacheTime()
        {
            _cacheTime?.SetValue(null, OriginalCacheTime);
            Plugin.Instance.Logger.Debug("Unpatch CacheTime Success by Reflection");
        }

        private static bool IsUpdateNeeded(string name, bool isEpisode)
        {
            var isJapaneseFallback = HasMovieDbJapaneseFallback();

            return string.IsNullOrEmpty(name) || !isJapaneseFallback
                ? !IsChinese(name)
                : !(IsChineseJapanese(name) && (!isEpisode || !IsDefaultChineseEpisodeName(name)));
        }

        [HarmonyPrefix]
        private static bool ProcessMainInfoMoviePrefix(MetadataResult<Movie> resultItem, object settings,
            string preferredCountryCode, object movieData, bool isFirstLanguage)
        {
            var item = resultItem.Item;

            if (_getTitleMovieData != null && IsUpdateNeeded(item.Name, false))
            {
                item.Name = _getTitleMovieData.Invoke(movieData, null) as string;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool IsCompletePrefix(BaseItem item, ref bool __result, out bool __state)
        {
            __state = false;

            if (item is Movie || item is Series || item is Season || item is Episode)
            {
                __state = true;

                __result = !HasMovieDbJapaneseFallback()
                    ? IsChinese(item.Name) && IsChinese(item.Overview)
                    : IsChineseJapanese(item.Name) && IsChineseJapanese(item.Overview);

                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsCompletePostfix(BaseItem item, ref bool __result, bool __state)
        {
            if (__state)
            {
                if (IsChinese(item.Name))
                {
                    item.Name = ConvertTraditionalToSimplified(item.Name);
                }

                if (IsChinese(item.Overview))
                {
                    item.Overview = ConvertTraditionalToSimplified(item.Overview);
                }
                else if (BlockNonFallbackLanguage(item.Overview))
                {
                    item.Overview = null;
                }
            }
        }

        [HarmonyPrefix]
        private static bool ProcessMainInfoSeriesPrefix(MetadataResult<Series> resultItem, object settings,
            string preferredCountryCode, object movieData, bool isFirstLanguage)
        {
            var item = resultItem.Item;

            if (_getTitleMovieData != null && IsUpdateNeeded(item.Name, false))
            {
                item.Name = _getTitleMovieData.Invoke(movieData, null) as string;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool SeriesImportDataPrefix(MetadataResult<Series> seriesResult, object seriesInfo,
            string preferredCountryCode, object settings, bool isFirstLanguage)
        {
            var item = seriesResult.Item;

            if (_getTitleSeriesInfo != null && IsUpdateNeeded(item.Name, false))
            {
                item.Name = _getTitleSeriesInfo.Invoke(seriesInfo, null) as string;
            }

            if (_genresProperty != null && _genreNameProperty != null && isFirstLanguage &&
                string.Equals(CurrentLookupLanguageCountryCode.Value, "CN", StringComparison.OrdinalIgnoreCase))
            {
                if (_genresProperty.GetValue(seriesInfo) is IList genres)
                {
                    foreach (var genre in genres)
                    {
                        var genreValue = _genreNameProperty.GetValue(genre)?.ToString();
                        if (!string.IsNullOrEmpty(genreValue))
                        {
                            if (string.Equals(genreValue, "Sci-Fi & Fantasy",
                                    StringComparison.OrdinalIgnoreCase))
                                _genreNameProperty.SetValue(genre, "科幻奇幻");

                            if (string.Equals(genreValue, "War & Politics",
                                    StringComparison.OrdinalIgnoreCase))
                                _genreNameProperty.SetValue(genre, "战争政治");
                        }
                    }
                }
            }

            return true;
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoPostfix(string tmdbId, string language, CancellationToken cancellationToken,
            Task __result)
        {
            if (WasCalledByMethod(_movieDbAssembly, "FetchImages")) return;

            var lookupLanguageCountryCode = !string.IsNullOrEmpty(language) && language.Contains('-')
                ? language.Split('-')[1]
                : null;

            CurrentLookupLanguageCountryCode.Value = lookupLanguageCountryCode;

            if (_seriesInfoTaskResultProperty == null)
                _seriesInfoTaskResultProperty = __result.GetType().GetProperty("Result");

            var seriesInfo = _seriesInfoTaskResultProperty?.GetValue(__result);
            if (seriesInfo != null && _nameSeriesInfoProperty != null)
            {
                var name = _nameSeriesInfoProperty.GetValue(seriesInfo) as string;

                if (!HasMovieDbJapaneseFallback()
                        ? !IsChinese(name)
                        : !IsChineseJapanese(name) &&
                          _alternativeTitleSeriesInfoProperty != null &&
                          _alternativeTitleListProperty != null &&
                          _alternativeTitleCountryCode != null && _alternativeTitle != null)
                {
                    var alternativeTitles = _alternativeTitleSeriesInfoProperty.GetValue(seriesInfo);
                    if (_alternativeTitleListProperty.GetValue(alternativeTitles) is IList altTitles)
                    {
                        foreach (var altTitle in altTitles)
                        {
                            var iso3166Value = _alternativeTitleCountryCode.GetValue(altTitle)?.ToString();
                            var titleValue = _alternativeTitle.GetValue(altTitle)?.ToString();
                            if (!string.IsNullOrEmpty(iso3166Value) && !string.IsNullOrEmpty(titleValue) &&
                                lookupLanguageCountryCode != null && string.Equals(iso3166Value,
                                    lookupLanguageCountryCode, StringComparison.OrdinalIgnoreCase))
                            {
                                _nameSeriesInfoProperty.SetValue(seriesInfo, titleValue);
                                break;
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool SeasonImportDataPrefix(Season item, object seasonInfo, string name, int seasonNumber,
            bool isFirstLanguage)
        {
            if (_nameSeasonInfoProperty != null && IsUpdateNeeded(item.Name, false))
            {
                item.Name = _nameSeasonInfoProperty.GetValue(seasonInfo) as string;
            }

            if (_overviewSeasonInfoProperty != null && IsUpdateNeeded(item.Overview, false))
            {
                item.Overview = _overviewSeasonInfoProperty.GetValue(seasonInfo) as string;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool EpisodeImportDataPrefix(MetadataResult<Episode> result, EpisodeInfo info, object response,
            object settings, bool isFirstLanguage)
        {
            var item = result.Item;

            if (_nameEpisodeInfoProperty != null && IsUpdateNeeded(item.Name, true))
            {
                var nameValue = _nameEpisodeInfoProperty.GetValue(response) as string;

                if (string.IsNullOrEmpty(item.Name) || !HasMovieDbJapaneseFallback() ||
                    IsChinese(item.Name) && IsDefaultChineseEpisodeName(item.Name) && IsJapanese(nameValue) &&
                     !IsDefaultJapaneseEpisodeName(nameValue))
                {
                    item.Name = nameValue;
                }
            }

            if (_overviewEpisodeInfoProperty != null && IsUpdateNeeded(item.Overview, true))
            {
                item.Overview = _overviewEpisodeInfoProperty.GetValue(response) as string;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void MetadataLanguagesPostfix(object __instance, ItemLookupInfo searchInfo,
            string[] providerLanguages, ref string[] __result)
        {
            if (searchInfo.MetadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var list = __result.ToList();
                var index = list.FindIndex(l => string.Equals(l, "en", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(l, "en-us", StringComparison.OrdinalIgnoreCase));

                var currentFallbackLanguages = GetMovieDbFallbackLanguages();

                foreach (var fallbackLanguage in currentFallbackLanguages)
                {
                    if (!list.Contains(fallbackLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        var mappedLanguage = (string)_mapLanguageToProviderLanguage.Invoke(__instance,
                            new object[] { fallbackLanguage, null, false, providerLanguages });

                        if (!string.IsNullOrEmpty(mappedLanguage))
                        {
                            if (index >= 0)
                            {
                                list.Insert(index, mappedLanguage);
                                index++;
                            }
                            else
                            {
                                list.Add(mappedLanguage);
                            }
                        }
                    }
                }

                __result = list.ToArray();
            }
        }

        [HarmonyPostfix]
        private static void GetImageLanguagesParamPostfix(ref string __result)
        {
            var list = __result.Split(',').ToList();

            if (list.Any(i => i.StartsWith("zh")) && !list.Contains("zh"))
            {
                list.Insert(list.FindIndex(i => i.StartsWith("zh")) + 1, "zh");
            }

            __result = string.Join(",", list.ToArray());
        }
    }
}
