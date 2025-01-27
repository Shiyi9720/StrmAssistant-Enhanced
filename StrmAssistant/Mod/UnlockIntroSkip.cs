using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Configuration;
using StrmAssistant.Common;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public static class UnlockIntroSkip
    {
        private static readonly PatchApproachTracker PatchApproachTracker =
            new PatchApproachTracker(nameof(UnlockIntroSkip));

        private static MethodInfo _isIntroDetectionSupported;
        private static MethodInfo _createQueryForEpisodeIntroDetection;
        private static MethodInfo _onFailedToFindIntro;
        private static MethodInfo _detectSequences;
        private static PropertyInfo _confidenceProperty;

        private static readonly AsyncLocal<bool> LogZeroConfidence = new AsyncLocal<bool>();

        public static void Initialize()
        {
            try
            {
                var embyProviders = Assembly.Load("Emby.Providers");
                var audioFingerprintManager = embyProviders.GetType("Emby.Providers.Markers.AudioFingerprintManager");
                _isIntroDetectionSupported = audioFingerprintManager.GetMethod("IsIntroDetectionSupported",
                    BindingFlags.Public | BindingFlags.Instance);
                var markerScheduledTask = embyProviders.GetType("Emby.Providers.Markers.MarkerScheduledTask");
                _createQueryForEpisodeIntroDetection = markerScheduledTask.GetMethod(
                    "CreateQueryForEpisodeIntroDetection",
                    BindingFlags.Public | BindingFlags.Static);

                var sequenceDetection = embyProviders.GetType("Emby.Providers.Markers.SequenceDetection");
                _detectSequences = sequenceDetection.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "DetectSequences" && m.GetParameters().Length == 8);
                var sequenceDetectionResult = embyProviders.GetType("Emby.Providers.Markers.SequenceDetectionResult");
                _confidenceProperty =
                    sequenceDetectionResult.GetProperty("Confidence", BindingFlags.Public | BindingFlags.Instance);
                _onFailedToFindIntro = audioFingerprintManager.GetMethod("OnFailedToFindIntro",
                    BindingFlags.NonPublic | BindingFlags.Static);
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("UnlockIntroSkip - Patch Init Failed");
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
                PatchApproachTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (HarmonyMod == null) PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;

            if (PatchApproachTracker.FallbackPatchApproach != PatchApproach.None &&
                Plugin.Instance.IntroSkipStore.GetOptions().UnlockIntroSkip)
            {
                Patch();
            }
        }

        public static void Patch()
        {
            EnableImageCapture.PatchIsShortcut();

            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_isIntroDetectionSupported, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Patch(_isIntroDetectionSupported,
                            prefix: new HarmonyMethod(typeof(UnlockIntroSkip).GetMethod(
                                "IsIntroDetectionSupportedPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                            postfix: new HarmonyMethod(typeof(UnlockIntroSkip).GetMethod(
                                "IsIntroDetectionSupportedPostfix", BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch IsIntroDetectionSupported Success by Harmony");
                    }
                    if (!IsPatched(_createQueryForEpisodeIntroDetection, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Patch(_createQueryForEpisodeIntroDetection,
                            postfix: new HarmonyMethod(typeof(UnlockIntroSkip).GetMethod(
                                "CreateQueryForEpisodeIntroDetectionPostfix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch CreateQueryForEpisodeIntroDetection Success by Harmony");
                    }
                    if (!IsPatched(_detectSequences, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Patch(_detectSequences,
                            postfix: new HarmonyMethod(typeof(UnlockIntroSkip).GetMethod(
                                "DetectSequencesPostfix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch DetectSequences Success by Harmony");
                    }
                    if (!IsPatched(_onFailedToFindIntro, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Patch(_onFailedToFindIntro,
                            prefix: new HarmonyMethod(typeof(UnlockIntroSkip).GetMethod(
                                "OnFailedToFindIntroPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug("Patch OnFailedToFindIntro Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Patch UnlockIntroSkip Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;
                }
            }
        }

        public static void Unpatch()
        {
            EnableImageCapture.UnpatchIsShortcut();

            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (IsPatched(_isIntroDetectionSupported, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Unpatch(_isIntroDetectionSupported,
                            AccessTools.Method(typeof(UnlockIntroSkip), "IsIntroDetectionSupportedPrefix"));
                        HarmonyMod.Unpatch(_isIntroDetectionSupported,
                            AccessTools.Method(typeof(UnlockIntroSkip), "IsIntroDetectionSupportedPostfix"));
                        Plugin.Instance.Logger.Debug("Unpatch IsIntroDetectionSupported Success by Harmony");
                    }
                    if (IsPatched(_createQueryForEpisodeIntroDetection, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Unpatch(_createQueryForEpisodeIntroDetection,
                            AccessTools.Method(typeof(UnlockIntroSkip), "CreateQueryForEpisodeIntroDetectionPostfix"));
                        Plugin.Instance.Logger.Debug("Unpatch CreateQueryForEpisodeIntroDetection Success by Harmony");
                    }
                    if (IsPatched(_detectSequences, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Unpatch(_detectSequences,
                            AccessTools.Method(typeof(UnlockIntroSkip), "DetectSequencesPostfix"));
                        Plugin.Instance.Logger.Debug("Unpatch DetectSequences Success by Harmony");
                    }
                    if (IsPatched(_onFailedToFindIntro, typeof(UnlockIntroSkip)))
                    {
                        HarmonyMod.Unpatch(_onFailedToFindIntro,
                            AccessTools.Method(typeof(UnlockIntroSkip), "OnFailedToFindIntroPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch OnFailedToFindIntro Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Unpatch UnlockIntroSkip Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                }
            }
        }

        [HarmonyPrefix]
        private static bool IsIntroDetectionSupportedPrefix(Episode item, LibraryOptions libraryOptions,
            ref bool __result, out bool __state)
        {
            __state = false;

            if (item.IsShortcut)
            {
                EnableImageCapture.PatchIsShortcutInstance(item);
                __state = true;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsIntroDetectionSupportedPostfix(Episode item, LibraryOptions libraryOptions,
            ref bool __result, bool __state)
        {
            if (__state)
            {
                EnableImageCapture.UnpatchIsShortcutInstance(item);
            }
        }

        [HarmonyPostfix]
        private static void CreateQueryForEpisodeIntroDetectionPostfix(LibraryOptions libraryOptions, ref InternalItemsQuery __result)
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope;

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
            {
                __result.ParentIds = Plugin.FingerprintApi.GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            }
            else
            {
                if (FingerprintApi.LibraryPathsInScope.Any())
                {
                    __result.PathStartsWithAny = FingerprintApi.LibraryPathsInScope.ToArray();
                }
                
                var blackListSeasons = Plugin.FingerprintApi.GetAllBlacklistSeasons().ToArray();
                if (blackListSeasons.Any())
                {
                    __result.ExcludeParentIds = blackListSeasons;
                }
            }
        }

        [HarmonyPostfix]
        private static void DetectSequencesPostfix(object __result)
        {
            if (_confidenceProperty.GetValue(__result) is double confidence && confidence == 0)
            {
                LogZeroConfidence.Value = true;
            }
        }

        [HarmonyPrefix]
        private static bool OnFailedToFindIntroPrefix(Episode episode)
        {
            if (LogZeroConfidence.Value)
            {
                BaseItem.ItemRepository.LogIntroDetectionFailureFailure(episode.InternalId,
                    episode.DateModified.ToUnixTimeSeconds());

                _ = Plugin.LibraryApi.SerializeMediaInfo(episode.InternalId, true, "Zero Fingerprint Confidence",
                    CancellationToken.None);
            }

            return false;
        }
    }
}
