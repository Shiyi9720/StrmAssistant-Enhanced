using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class ChineseTvdb : PatchBase<ChineseTvdb>
    {
        private static Assembly _tvdbAssembly;
        private static MethodInfo _convertToTvdbLanguages;
        private static MethodInfo _getTranslation;
        private static MethodInfo _addMovieInfo;
        private static MethodInfo _addSeriesInfo;
        private static MethodInfo _getTvdbSeason;
        private static MethodInfo _findEpisode;
        private static MethodInfo _getEpisodeData;

        private static PropertyInfo _seasonName;
        private static PropertyInfo _episodeName;
        private static PropertyInfo _episodeOverview;
        private static PropertyInfo _translationName;
        private static PropertyInfo _translationOverview;
        private static PropertyInfo _translationLanguage;
        private static PropertyInfo _translationIsPrimary;
        private static PropertyInfo _translationIsAlias;

        private static PropertyInfo _tvdbSeasonTaskResultProperty;
        private static PropertyInfo _tvdbEpisodeTaskResultProperty;
        private static PropertyInfo _tvdbEpisodeTupleItem1Property;

        private static readonly ThreadLocal<bool?> ConsiderJapanese = new ThreadLocal<bool?>();

        public ChineseTvdb()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseTvdb)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _tvdbAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Tvdb");

            if (_tvdbAssembly != null)
            {
                var entryPoint = _tvdbAssembly.GetType("Tvdb.EntryPoint");
                _convertToTvdbLanguages = entryPoint.GetMethod("ConvertToTvdbLanguages",
                    BindingFlags.Instance | BindingFlags.Public, null, new[] { typeof(ItemLookupInfo) }, null);
                var translations = _tvdbAssembly.GetType("Tvdb.Translations");
                _getTranslation =
                    translations.GetMethod("GetTranslation", BindingFlags.Instance | BindingFlags.NonPublic);
                var nameTranslation = _tvdbAssembly.GetType("Tvdb.NameTranslation");
                _translationName = nameTranslation.GetProperty("name");
                _translationOverview = nameTranslation.GetProperty("overview");
                _translationLanguage = nameTranslation.GetProperty("language");
                _translationIsPrimary = nameTranslation.GetProperty("IsPrimary");
                _translationIsAlias = nameTranslation.GetProperty("isAlias");
                var tvdbMovieProvider = _tvdbAssembly.GetType("Tvdb.TvdbMovieProvider");
                _addMovieInfo = tvdbMovieProvider.GetMethod("AddMovieInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tvdbSeriesProvider = _tvdbAssembly.GetType("Tvdb.TvdbSeriesProvider");
                _addSeriesInfo = tvdbSeriesProvider.GetMethod("AddSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tvdbSeasonProvider= _tvdbAssembly.GetType("Tvdb.TvdbSeasonProvider");
                _getTvdbSeason =
                    tvdbSeasonProvider.GetMethod("GetTvdbSeason", BindingFlags.Instance | BindingFlags.Public);
                var tvdbSeason = _tvdbAssembly.GetType("Tvdb.TvdbSeason");
                _seasonName = tvdbSeason.GetProperty("name");
                var tvdbEpisodeProvider = _tvdbAssembly.GetType("Tvdb.TvdbEpisodeProvider");
                _findEpisode = tvdbEpisodeProvider.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "FindEpisode" && m.GetParameters().Length == 3);
                _getEpisodeData =
                    tvdbEpisodeProvider.GetMethod("GetEpisodeData", BindingFlags.Instance | BindingFlags.Public);
                var tvdbEpisode = _tvdbAssembly.GetType("Tvdb.TvdbEpisode");
                _episodeName = tvdbEpisode.GetProperty("name");
                _episodeOverview = tvdbEpisode.GetProperty("overview");
            }
            else
            {
                Plugin.Instance.Logger.Info("ChineseTvdb - Tvdb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _convertToTvdbLanguages,
                postfix: nameof(ConvertToTvdbLanguagesPostfix));
            PatchUnpatch(PatchTracker, apply, _getTranslation, prefix: nameof(GetTranslationPrefix),
                postfix: nameof(GetTranslationPostfix));
            PatchUnpatch(PatchTracker, apply, _addMovieInfo, postfix: nameof(AddInfoPostfix));
            PatchUnpatch(PatchTracker, apply, _addSeriesInfo, postfix: nameof(AddInfoPostfix));
            PatchUnpatch(PatchTracker, apply, _getTvdbSeason, postfix: nameof(GetTvdbSeasonPostfix));
            PatchUnpatch(PatchTracker, apply, _findEpisode, postfix: nameof(FindEpisodePostfix));
            PatchUnpatch(PatchTracker, apply, _getEpisodeData, postfix: nameof(GetEpisodeDataPostfix));
        }

        [HarmonyPostfix]
        private static void ConvertToTvdbLanguagesPostfix(ItemLookupInfo lookupInfo, ref string[] __result)
        {
            if (lookupInfo.MetadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var list = __result.ToList();
                var index = list.FindIndex(l => string.Equals(l, "eng", StringComparison.OrdinalIgnoreCase));

                var currentFallbackLanguages =
                    GetTvdbFallbackLanguages().Where(l => (ConsiderJapanese.Value ?? true) || l != "jpn");

                foreach (var fallbackLanguage in currentFallbackLanguages)
                {
                    if (!list.Contains(fallbackLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        if (index >= 0)
                        {
                            list.Insert(index, fallbackLanguage);
                            index++;
                        }
                        else
                        {
                            list.Add(fallbackLanguage);
                        }
                    }
                }

                __result = list.ToArray();
            }
        }
        
        [HarmonyPrefix]
        private static bool GetTranslationPrefix(ref List<object> translations, ref string[] tvdbLanguages, int field,
            ref bool defaultToFirst)
        {
            if (translations != null && translations.Count > 0)
            {
                if (field == 0)
                {
                    translations.RemoveAll(t =>
                    {
                        var isAliasValue = _translationIsAlias?.GetValue(t)?.ToString();
                        var isAlias = !string.IsNullOrEmpty(isAliasValue) ? bool.Parse(isAliasValue) : (bool?)null;
                        return isAlias is true;
                    });
                }

                if (HasTvdbJapaneseFallback())
                {
                    var considerJapanese = translations.Any(t =>
                    {
                        var language = _translationLanguage?.GetValue(t)?.ToString();
                        var isPrimary = _translationIsPrimary?.GetValue(t) as bool?;

                        return language == "jpn" && isPrimary is true;
                    });

                    tvdbLanguages = tvdbLanguages.Where(l => considerJapanese || l != "jpn").ToArray();
                }

                if (field == 0)
                {
                    var cnLanguages = new HashSet<string> { "zho", "zhtw", "yue" };
                    var trans = translations;
                    Array.Sort(tvdbLanguages, (lang1, lang2) =>
                    {
                        var translation1 =
                            trans.FirstOrDefault(t => _translationLanguage.GetValue(t)?.ToString() == lang1);
                        var translation2 =
                            trans.FirstOrDefault(t => _translationLanguage.GetValue(t)?.ToString() == lang2);

                        var name1 = translation1 != null ? _translationName.GetValue(translation1) as string : null;
                        var name2 = translation2 != null ? _translationName.GetValue(translation2) as string : null;

                        var cn1 = cnLanguages.Contains(lang1);
                        var cn2 = cnLanguages.Contains(lang2);

                        if (cn1 && cn2)
                        {
                            if (IsChinese(name1) && !IsChinese(name2)) return -1;
                            if (!IsChinese(name1) && IsChinese(name2)) return 1;
                            return 0;
                        }

                        if (cn1) return -1;
                        if (cn2) return 1;

                        return 0;
                    });
                }

                var languageOrder = tvdbLanguages.Select((l, index) => (l, index))
                    .ToDictionary(x => x.l, x => x.index);

                translations.Sort((t1, t2) =>
                {
                    var language1 = _translationLanguage?.GetValue(t1)?.ToString();
                    var language2 = _translationLanguage?.GetValue(t2)?.ToString();

                    var index1 = languageOrder.GetValueOrDefault(language1, int.MaxValue);
                    var index2 = languageOrder.GetValueOrDefault(language2, int.MaxValue);

                    return index1.CompareTo(index2);
                });
            }

            if (translations?.Count == 0) translations = null;

            return true;
        }

        [HarmonyPostfix]
        private static void AddInfoPostfix(MetadataResult<BaseItem> metadataResult)
        {
            var instance = metadataResult.Item;

            if (IsChinese(instance.Name))
            {
                instance.Name = ConvertTraditionalToSimplified(instance.Name);
            }

            if (IsChinese(instance.Overview))
            {
                instance.Overview = ConvertTraditionalToSimplified(instance.Overview);
            }
            else if (BlockTvdbNonFallbackLanguage(instance.Overview))
            {
                instance.Overview = null;
            }
        }

        [HarmonyPostfix]
        private static void GetTranslationPostfix(List<object> translations, string[] tvdbLanguages, int field,
            bool defaultToFirst, ref object __result)
        {
            if (__result != null && !defaultToFirst)
            {
                var name = _translationName.GetValue(__result) as string;

                switch (field)
                {
                    case 0:
                    {
                        if (IsChinese(name))
                        {
                            _translationName.SetValue(__result, ConvertTraditionalToSimplified(name));
                        }
                        else if (BlockTvdbNonFallbackLanguage(name))
                        {
                            _translationName.SetValue(__result, null);
                        }

                        break;
                    }
                    case 1:
                    {
                        var overview = _translationOverview.GetValue(__result) as string;

                        if (IsChinese(overview))
                        {
                            overview = ConvertTraditionalToSimplified(overview);
                            _translationOverview.SetValue(__result, overview);
                        }
                        else if (BlockTvdbNonFallbackLanguage(overview))
                        {
                            overview = null;
                            _translationOverview.SetValue(__result, null);
                        }

                        if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(overview))
                        {
                            _translationName.SetValue(__result, overview);
                        }

                        break;
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetTvdbSeasonPostfix(SeasonInfo id, IDirectoryService directoryService,
            CancellationToken cancellationToken, Task __result)
        {
            if (_tvdbSeasonTaskResultProperty == null)
                _tvdbSeasonTaskResultProperty = __result.GetType().GetProperty("Result");

            var tvdbSeason = _tvdbSeasonTaskResultProperty?.GetValue(__result);

            if (tvdbSeason != null)
            {
                var name = _seasonName.GetValue(tvdbSeason) as string;

                if (IsChinese(name))
                {
                    _seasonName.SetValue(tvdbSeason, ConvertTraditionalToSimplified(name));
                }
                else if (id.IndexNumber.HasValue && (string.IsNullOrEmpty(name) || BlockTvdbNonFallbackLanguage(name)))
                {
                    _seasonName.SetValue(tvdbSeason, $"第 {id.IndexNumber} 季");
                }
            }
        }

        [HarmonyPostfix]
        private static void FindEpisodePostfix(object data, EpisodeInfo searchInfo, int? seasonNumber,
            ref object __result)
        {
            if (_episodeName != null && _episodeOverview != null && __result != null)
            {
                var name = _episodeName.GetValue(__result) as string;
                var overview = _episodeOverview.GetValue(__result) as string;

                var considerJapanese = HasTvdbJapaneseFallback() && (IsJapanese(name) || IsJapanese(overview));
                ConsiderJapanese.Value = considerJapanese;

                if (!considerJapanese)
                {
                    if (!IsChinese(name)) _episodeName.SetValue(__result, null);
                    if (!IsChinese(overview)) _episodeOverview.SetValue(__result, null);
                }
                else
                {
                    if (!IsChineseJapanese(name)) _episodeName.SetValue(__result, null);
                    if (!IsChineseJapanese(overview)) _episodeOverview.SetValue(__result, null);
                }
            }
        }

        [HarmonyPostfix]
        private static void GetEpisodeDataPostfix(EpisodeInfo searchInfo, bool fillExtendedInfo,
            IDirectoryService directoryService, CancellationToken cancellationToken, Task __result)
        {
            if (_tvdbEpisodeTaskResultProperty == null)
                _tvdbEpisodeTaskResultProperty = __result.GetType().GetProperty("Result");

            var result = _tvdbEpisodeTaskResultProperty?.GetValue(__result);

            if (_tvdbEpisodeTupleItem1Property == null)
                _tvdbEpisodeTupleItem1Property = result?.GetType().GetProperty("Item1");

            var tvdbEpisode = _tvdbEpisodeTupleItem1Property?.GetValue(result);

            if (tvdbEpisode != null)
            {
                var name = _episodeName.GetValue(tvdbEpisode) as string;
                var overview = _episodeOverview.GetValue(tvdbEpisode) as string;

                if (IsChinese(name))
                {
                    _episodeName.SetValue(tvdbEpisode, ConvertTraditionalToSimplified(name));
                }
                else if (searchInfo.IndexNumber.HasValue &&
                         (string.IsNullOrEmpty(name) || BlockTvdbNonFallbackLanguage(name)))
                {
                    _episodeName.SetValue(tvdbEpisode, $"第 {searchInfo.IndexNumber} 集");
                }

                if (IsChinese(overview))
                {
                    _episodeOverview.SetValue(tvdbEpisode, ConvertTraditionalToSimplified(overview));
                }
                else if (BlockTvdbNonFallbackLanguage(overview))
                {
                    _episodeOverview.SetValue(tvdbEpisode, null);
                }
            }
        }
    }
}
