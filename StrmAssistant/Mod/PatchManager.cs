using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Mod
{
    public static class PatchManager
    {
        public static Harmony HarmonyMod;
        public static readonly List<PatchTracker> PatchTrackerList = new List<PatchTracker>();

        public static ExclusiveExtract ExclusiveExtract;

        private static readonly ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod> HarmonyMethodCache =
            new ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod>();
        private static readonly ConcurrentDictionary<Tuple<Type, string>, MethodInfo> MethodInfoCache =
            new ConcurrentDictionary<Tuple<Type, string>, MethodInfo>();

        public static void Initialize()
        {
            try
            {
                HarmonyMod = new Harmony("emby.mod");
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Warn("Harmony Init Failed");
                LoggerDebug(e);
            }

            ExclusiveExtract = new ExclusiveExtract();
        }

        public static bool IsPatched(MethodBase methodInfo, Type type)
        {
            if (methodInfo is null || HarmonyMod is null) return false;

            var patchedMethods = Harmony.GetAllPatchedMethods();
            if (!patchedMethods.Contains(methodInfo)) return false;
            var patchInfo = Harmony.GetPatchInfo(methodInfo);

            if (patchInfo is null) return false;

            return patchInfo.Prefixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Postfixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Transpilers.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Finalizers.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type);
        }

        public static bool IsModSuccess()
        {
            return PatchTrackerList.Where(p => p.IsSupported)
                .All(p => p.FallbackPatchApproach == p.DefaultPatchApproach);
        }

        public static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod, string prefix = null,
            string postfix = null, string transpiler = null, string finalizer = null, bool suppress = false)
        {
            if (targetMethod is null)
            {
                Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} Init Failed: {targetMethod?.Name ?? "Unknown"}");
                tracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }

            if (HarmonyMod is null)
            {
                tracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }

            var action = apply ? "Patch" : "Unpatch";

            try
            {
                var prefixMethod = GetHarmonyMethod(tracker.PatchType, prefix);
                var postfixMethod = GetHarmonyMethod(tracker.PatchType, postfix);
                var transpilerMethod = GetHarmonyMethod(tracker.PatchType, transpiler);
                var finalizerMethod = GetHarmonyMethod(tracker.PatchType, finalizer);

                if (apply)
                {
                    if (!IsPatched(targetMethod, tracker.PatchType))
                    {
                        HarmonyMod.Patch(targetMethod, prefixMethod, postfixMethod, transpilerMethod, finalizerMethod);
                    }
                }
                else
                {
                    var prefixInfo = GetMethodInfo(tracker.PatchType, prefix);
                    var postfixInfo = GetMethodInfo(tracker.PatchType, postfix);
                    var transpilerInfo = GetMethodInfo(tracker.PatchType, transpiler);
                    var finalizerInfo = GetMethodInfo(tracker.PatchType, finalizer);

                    if (prefixInfo != null) HarmonyMod.Unpatch(targetMethod, prefixInfo);
                    if (postfixInfo != null) HarmonyMod.Unpatch(targetMethod, postfixInfo);
                    if (transpilerInfo != null) HarmonyMod.Unpatch(targetMethod, transpilerInfo);
                    if (finalizerInfo != null) HarmonyMod.Unpatch(targetMethod, finalizerInfo);
                }

                if (!suppress && Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"{action} {(targetMethod.DeclaringType != null ? targetMethod.DeclaringType.Name + "." : string.Empty)}{targetMethod.Name} for {tracker.PatchType.Name} Success");
                }

                return true;
            }
            catch (Exception he)
            {
                Plugin.Instance.Logger.Warn($"{action} {targetMethod.Name} for {tracker.PatchType.Name} Failed");
                LoggerDebug(he);
                tracker.FallbackPatchApproach = PatchApproach.Reflection;
            }

            return false;
        }

        private static void LoggerDebug(Exception e)
        {
            if (Plugin.Instance.DebugMode)
            {
                Plugin.Instance.Logger.Debug(e.Message);
                Plugin.Instance.Logger.Debug(e.StackTrace);
            }
        }

        private static HarmonyMethod GetHarmonyMethod(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return HarmonyMethodCache.GetOrAdd(Tuple.Create(patchType, patchMethod), tuple =>
            {
                var methodInfo = GetMethodInfo(tuple.Item1, tuple.Item2);
                return methodInfo != null ? new HarmonyMethod(methodInfo) : null;
            });
        }

        private static MethodInfo GetMethodInfo(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return MethodInfoCache.GetOrAdd(Tuple.Create(patchType, patchMethod),
                tuple => AccessTools.Method(tuple.Item1, tuple.Item2));
        }
    }
}
