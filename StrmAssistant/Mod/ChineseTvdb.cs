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
        private static MethodInfo _findEpisode;
        private static MethodInfo _getEpisodeData;

        private static PropertyInfo _episodeName;
        private static PropertyInfo _episodeOverview;
        private static PropertyInfo _translationName;
        private static PropertyInfo _translationOverview;
        private static PropertyInfo _translationLanguage;

        private static PropertyInfo _tvdbEpisodeTaskResultProperty;
        private static PropertyInfo _tvdbEpisodeTupleItem1Property;

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
                var tvdbMovieProvider = _tvdbAssembly.GetType("Tvdb.TvdbMovieProvider");
                _addMovieInfo = tvdbMovieProvider.GetMethod("AddMovieInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var tvdbSeriesProvider = _tvdbAssembly.GetType("Tvdb.TvdbSeriesProvider");
                _addSeriesInfo = tvdbSeriesProvider.GetMethod("AddSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);

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

                var currentFallbackLanguages = GetTvDbFallbackLanguages();

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
        private static bool GetTranslationPrefix(ref List<object> translations, string[] tvdbLanguages, int field,
            ref bool defaultToFirst)
        {
            if (translations != null)
            {
                var languageOrder =
                    tvdbLanguages.ToDictionary(lang => lang, lang => Array.IndexOf(tvdbLanguages, lang));

                translations.RemoveAll(t =>
                {
                    var language = _translationLanguage?.GetValue(t)?.ToString();
                    return string.IsNullOrEmpty(language) || !languageOrder.ContainsKey(language);
                });

                translations.Sort((t1, t2) =>
                {
                    var language1 = _translationLanguage?.GetValue(t1)?.ToString();
                    var language2 = _translationLanguage?.GetValue(t2)?.ToString();

                    var index1 = languageOrder.GetValueOrDefault(language1, int.MaxValue);
                    var index2 = languageOrder.GetValueOrDefault(language2, int.MaxValue);

                    return index1.CompareTo(index2);
                });

                if (translations.Count == 0) translations = null;
            }

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
            else if (BlockNonFallbackLanguage(instance.Overview))
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
                        else if (BlockNonFallbackLanguage(overview))
                        {
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
        private static void FindEpisodePostfix(object data, EpisodeInfo searchInfo, int? seasonNumber,
            ref object __result)
        {
            if (_episodeName != null && _episodeOverview != null && __result != null)
            {
                var name = _episodeName.GetValue(__result) as string;
                var overview = _episodeOverview.GetValue(__result) as string;

                if (!HasTvdbJapaneseFallback())
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

                if (IsChinese(overview))
                {
                    _episodeOverview.SetValue(tvdbEpisode, ConvertTraditionalToSimplified(overview));
                }
                else if (BlockNonFallbackLanguage(overview))
                {
                    _episodeOverview.SetValue(tvdbEpisode, null);
                }
            }
        }
    }
}
