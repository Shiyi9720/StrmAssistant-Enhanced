using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace StrmAssistant.Mod
{
    public static class PatchManager
    {
        public static Harmony HarmonyMod;

        public static List<PatchApproachTracker> PatchTrackerList =new List<PatchApproachTracker>();

        public static void Initialize()
        {
            try
            {
                HarmonyMod = new Harmony("emby.mod");
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("Harmony Init Failed");
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }

            EnableImageCapture.Initialize();
            EnhanceChineseSearch.Initialize();
            MergeMultiVersion.Initialize();
            ExclusiveExtract.Initialize();
            ChineseMovieDb.Initialize();
            ChineseTvdb.Initialize();
            EnhanceMovieDbPerson.Initialize();
            AltMovieDbConfig.Initialize();
            EnableProxyServer.Initialize();
            PreferOriginalPoster.Initialize();
            UnlockIntroSkip.Initialize();
            PinyinSortName.Initialize();
            EnhanceNfoMetadata.Initialize();
            HidePersonNoImage.Initialize();
            EnforceLibraryOrder.Initialize();
            BeautifyMissingMetadata.Initialize();
            EnhanceMissingEpisodes.Initialize();
            ChapterChangeTracker.Initialize();
            MovieDbEpisodeGroup.Initialize();
            NoBoxsetsAutoCreation.Initialize();
            EnhanceNotificationSystem.Initialize();
        }

        public static bool IsPatched(MethodBase methodInfo, Type type)
        {
            var patchedMethods = Harmony.GetAllPatchedMethods();
            if (!patchedMethods.Contains(methodInfo)) return false;
            var patchInfo = Harmony.GetPatchInfo(methodInfo);

            return patchInfo.Prefixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Postfixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Transpilers.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type);
        }

        public static bool WasCalledByMethod(Assembly assembly, string callingMethodName)
        {
            var stackFrames = new StackTrace(1, false).GetFrames();
            if (stackFrames != null && stackFrames.Select(f => f.GetMethod()).Any(m =>
                    m?.DeclaringType?.Assembly == assembly && m?.Name == callingMethodName))
                return true;

            return false;
        }

        public static bool? IsHarmonyModSuccess()
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;

            return PatchTrackerList.Where(p => p.IsSupported)
                .All(p => p.FallbackPatchApproach == PatchApproach.Harmony);
        }
    }
}
