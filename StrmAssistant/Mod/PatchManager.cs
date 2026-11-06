using HarmonyLib;
using MediaBrowser.Model.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// One patch point: the Emby method plus the names of the patch methods to attach to it.
    /// </summary>
    public class PatchSpec
    {
        public PatchSpec(string name, MethodBase target, string prefix = null, string postfix = null,
            string transpiler = null, string finalizer = null)
        {
            Name = name;
            Target = target;
            Prefix = prefix;
            Postfix = postfix;
            Transpiler = transpiler;
            Finalizer = finalizer;
        }

        public string Name { get; }

        public MethodBase Target { get; }

        public string Prefix { get; }

        public string Postfix { get; }

        public string Transpiler { get; }

        public string Finalizer { get; }
    }

    public static class PatchManager
    {
        public static Harmony HarmonyMod;
        public static readonly List<PatchTracker> PatchTrackerList = new List<PatchTracker>();

        public static ExclusiveExtract ExclusiveExtract;

        /// <summary>
        /// False when Harmony is loaded but cannot actually install detours on this runtime
        /// (or when the mandatory ffprobe patches could not be applied). Every exclusive
        /// feature is then reported as unavailable instead of silently doing nothing.
        /// </summary>
        public static bool IsModOperational { get; private set; } = true;

        /// <summary>
        /// Human readable reason why <see cref="IsModOperational"/> is false.
        /// </summary>
        public static string ModFailureReason { get; private set; } = string.Empty;

        private static readonly ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod> HarmonyMethodCache =
            new ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod>();
        private static readonly ConcurrentDictionary<Tuple<Type, string>, MethodInfo> MethodInfoCache =
            new ConcurrentDictionary<Tuple<Type, string>, MethodInfo>();

        private static ILogger Logger => Plugin.Instance?.Logger;

        public static void Initialize()
        {
            try
            {
                HarmonyMod = new Harmony("emby.mod");
            }
            catch (Exception e)
            {
                HarmonyMod = null;
                MarkUnavailable("Harmony initialisation failed: " + e.Message);
                Logger?.Warn("Harmony Init Failed: " + Describe(e));
                LoggerDebug(e);
            }

            if (HarmonyMod is null) return;

            LogHarmonyIdentity();

            if (!HarmonySelfTest())
            {
                MarkUnavailable("the Harmony detour engine does not work on this platform");
                return;
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
            return IsModOperational &&
                   PatchTrackerList.Where(p => p.IsSupported)
                       .All(p => p.FallbackPatchApproach == p.DefaultPatchApproach);
        }

        public static void MarkUnavailable(string reason)
        {
            IsModOperational = false;
            ModFailureReason = reason;
        }

        /// <summary>
        /// Applies a set of patches atomically. If one of them fails, the ones that were
        /// already installed in this call are removed again and the tracker is marked as
        /// unsupported, so Emby keeps its own implementation instead of running half a mod.
        /// </summary>
        public static bool PatchAll(PatchTracker tracker, IReadOnlyList<PatchSpec> specs)
        {
            var applied = new List<PatchSpec>();
            var failed = new List<string>();

            foreach (var spec in specs)
            {
                if (spec.Target is null)
                {
                    failed.Add(spec.Name);
                    continue;
                }

                var patched = PatchUnpatch(tracker, true, spec.Target, spec.Prefix, spec.Postfix, spec.Transpiler,
                    spec.Finalizer, true);

                if (patched && !IsPatched(spec.Target, tracker.PatchType))
                {
                    // Harmony accepted the patch but does not list it as applied: treat it as
                    // failed instead of assuming the exclusive feature is running.
                    Logger?.Warn($"{tracker.PatchType.Name} Patch Failed: {spec.Name} is not registered as patched");
                    patched = false;
                }

                if (patched)
                {
                    applied.Add(spec);
                }
                else
                {
                    failed.Add(spec.Name);
                }
            }

            if (failed.Count == 0) return true;

            for (var i = applied.Count - 1; i >= 0; i--)
            {
                var spec = applied[i];
                PatchUnpatch(tracker, false, spec.Target, spec.Prefix, spec.Postfix, spec.Transpiler, spec.Finalizer,
                    true);
            }

            tracker.FallbackPatchApproach = PatchApproach.None;
            tracker.FailureReason = string.Join(", ", failed);

            Logger?.Warn(string.Format(
                "{0} Patch Failed: {1}/{2} target(s) failed ({3}); rolled back {4} applied patch(es)",
                tracker.PatchType.Name, failed.Count, specs.Count, string.Join(", ", failed), applied.Count));

            return false;
        }

        public static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod, string prefix = null,
            string postfix = null, string transpiler = null, string finalizer = null, bool suppress = false)
        {
            if (targetMethod is null)
            {
                Logger?.Warn($"{tracker.PatchType.Name} Init Failed: Unknown");
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
                    if (prefixMethod is null && postfixMethod is null && transpilerMethod is null &&
                        finalizerMethod is null)
                    {
                        Logger?.Warn(
                            $"{tracker.PatchType.Name} Init Failed: no patch method found for {targetMethod.Name}");
                        tracker.FallbackPatchApproach = PatchApproach.None;
                        tracker.FailureReason = targetMethod.Name + ": patch method not found";

                        return false;
                    }

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
                    Logger?.Debug(
                        $"{action} {(targetMethod.DeclaringType != null ? targetMethod.DeclaringType.Name + "." : string.Empty)}{targetMethod.Name} for {tracker.PatchType.Name} Success");
                }

                return true;
            }
            catch (Exception he)
            {
                // Always log why a patch failed: a swallowed exception is what makes a
                // half-working mod look like a working one in the field.
                Logger?.Warn(
                    $"{action} {targetMethod.Name} for {tracker.PatchType.Name} Failed: {Describe(he)}");
                LoggerDebug(he);
                tracker.FallbackPatchApproach = PatchApproach.Reflection;
                tracker.FailureReason = targetMethod.Name + ": " + Describe(he);
            }

            return false;
        }

        private static void LogHarmonyIdentity()
        {
            try
            {
                var harmonyAssembly = typeof(Harmony).Assembly;
                var location = string.IsNullOrEmpty(harmonyAssembly.Location)
                    ? "<loaded from bytes>"
                    : harmonyAssembly.Location;

                Logger?.Info(string.Format("Harmony {0} loaded from {1} (runtime {2}, {3})",
                    harmonyAssembly.GetName().FullName, location,
                    RuntimeInformation.FrameworkDescription, RuntimeInformation.ProcessArchitecture));

                var copies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => string.Equals(a.GetName().Name, "0Harmony", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (copies.Count > 1)
                {
                    Logger?.Warn("Multiple 0Harmony assemblies are loaded: " +
                                 string.Join("; ", copies.Select(a => a.Location)));
                }
            }
            catch (Exception e)
            {
                LoggerDebug(e);
            }
        }

        /// <summary>
        /// Patches a private method of this class and verifies that the detour is effective.
        /// Harmony can load fine and still be unable to install detours (for example on an
        /// architecture its bundled MonoMod does not support); without this check every
        /// single patch would fail later with a much less obvious message.
        /// </summary>
        private static bool HarmonySelfTest()
        {
            var target = AccessTools.Method(typeof(PatchManager), nameof(SelfTestTarget));
            var prefix = AccessTools.Method(typeof(PatchManager), nameof(SelfTestPrefix));

            if (target is null || prefix is null) return false;

            try
            {
                HarmonyMod.Patch(target, new HarmonyMethod(prefix));

                if (SelfTestTarget() != 2)
                {
                    Logger?.Warn("Harmony self-test failed: the patch was installed but had no effect");
                    return false;
                }
            }
            catch (Exception e)
            {
                Logger?.Warn("Harmony self-test failed: " + Describe(e));
                LoggerDebug(e);

                return false;
            }
            finally
            {
                try
                {
                    if (prefix != null) HarmonyMod.Unpatch(target, prefix);
                }
                catch
                {
                    // ignored
                }
            }

            return SelfTestTarget() == 1;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int SelfTestTarget()
        {
            return 1;
        }

        private static void SelfTestPrefix(ref int __result)
        {
            __result = 2;
        }

        private static string Describe(Exception e)
        {
            var inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            var message = inner.Message ?? string.Empty;

            return inner.GetType().Name + (message.Length > 0 ? ": " + message : string.Empty);
        }

        private static void LoggerDebug(Exception e)
        {
            if (Plugin.Instance == null || !Plugin.Instance.DebugMode) return;

            Logger?.Debug(e.Message);
            Logger?.Debug(e.StackTrace);

            if (e.InnerException != null)
            {
                Logger?.Debug(e.InnerException.Message);
                Logger?.Debug(e.InnerException.StackTrace);
            }
        }

        private static HarmonyMethod GetHarmonyMethod(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            var key = Tuple.Create(patchType, patchMethod);

            if (HarmonyMethodCache.TryGetValue(key, out var cached)) return cached;

            var methodInfo = GetMethodInfo(patchType, patchMethod);

            if (methodInfo is null)
            {
                // A missing patch method used to surface as an ArgumentNullException from
                // ConcurrentDictionary.GetOrAdd, which hid the real problem.
                Logger?.Warn($"Patch method not found: {patchType.Name}.{patchMethod}");

                return null;
            }

            var harmonyMethod = new HarmonyMethod(methodInfo);
            HarmonyMethodCache[key] = harmonyMethod;

            return harmonyMethod;
        }

        private static MethodInfo GetMethodInfo(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            var key = Tuple.Create(patchType, patchMethod);

            if (MethodInfoCache.TryGetValue(key, out var cached)) return cached;

            var methodInfo = AccessTools.Method(patchType, patchMethod);

            if (methodInfo is null)
            {
                Logger?.Warn($"Patch method not found: {patchType.Name}.{patchMethod}");

                return null;
            }

            MethodInfoCache[key] = methodInfo;

            return methodInfo;
        }
    }
}
