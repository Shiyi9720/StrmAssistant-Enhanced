using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Emby's VideoListResolver only groups files of the same folder into versions when
    /// they are at most 8 (Emby.Naming.Video.VideoListResolver.GetVideosGroupedByVersion:
    /// "if (flag &amp;&amp; videos.Count &lt;= 8)"). That hard cap is why a folder with more than
    /// 8 versions shows them as separate movies instead of one multi-version entry.
    ///
    /// A transpiler replaces the single ldc.i4.8 constant in that method with a call to
    /// <see cref="GetMaxMultiVersionCount"/>. The helper returns the original 8 while the
    /// multi-version feature is off, so disabling the option restores Emby's native behavior
    /// even if the patch cannot be removed (for example when the method was already JITted).
    /// </summary>
    public class UnlockMultiVersionLimit : PatchBase<UnlockMultiVersionLimit>
    {
        private const int EmbyDefaultMaxVersions = 8;
        private const int UnlockedMaxVersions = 64;

        private static MethodInfo _getVideosGroupedByVersion;

        public UnlockMultiVersionLimit()
        {
            Initialize();

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                Plugin.Instance.Logger.Warn("UnlockMultiVersionLimit - unavailable: " +
                                            PatchTracker.FailureReason);

                return;
            }

            if (Plugin.Instance.GetPluginOptions().ExperienceEnhanceOptions.MergeMultiVersion)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var namingAssembly = Assembly.Load("Emby.Naming");
            var videoListResolver = namingAssembly.GetType("Emby.Naming.Video.VideoListResolver");

            _getVideosGroupedByVersion = videoListResolver?.GetMethod("GetVideosGroupedByVersion",
                BindingFlags.Instance | BindingFlags.NonPublic);

            if (_getVideosGroupedByVersion is null)
            {
                throw new MissingMethodException(
                    "Emby.Naming.Video.VideoListResolver.GetVideosGroupedByVersion");
            }
        }

        protected override void Prepare(bool apply)
        {
            var specs = new[]
            {
                new PatchSpec("GetVideosGroupedByVersion", _getVideosGroupedByVersion,
                    transpiler: nameof(GetVideosGroupedByVersionTranspiler))
            };

            if (apply)
            {
                PatchAll(PatchTracker, specs);
                return;
            }

            foreach (var spec in specs)
            {
                if (spec.Target is null) continue;

                PatchUnpatch(PatchTracker, false, spec.Target, spec.Prefix, spec.Postfix, spec.Transpiler,
                    spec.Finalizer, true);
            }
        }

        // NOTE: deliberately NOT an iterator (no yield). A yield-based transpiler makes the
        // compiler emit a nested state machine that implements IEnumerable<CodeInstruction>;
        // Assembly.GetTypes() resolves interface implementations eagerly. Emby scans every
        // plugin DLL with Assembly.Load(bytes) + GetTypes() before Plugin's AssemblyResolve
        // hook is installed, so that nested type failed the whole scan with
        // ReflectionTypeLoadException: "Could not load 0Harmony". Building a List keeps
        // HarmonyLib out of every type's base/interface metadata (method signature types are
        // resolved lazily), and the transpiler behaves identically.
        private static IEnumerable<CodeInstruction> GetVideosGroupedByVersionTranspiler(
            IEnumerable<CodeInstruction> instructions)
        {
            var getMaxCount = AccessTools.Method(typeof(UnlockMultiVersionLimit),
                nameof(GetMaxMultiVersionCount));

            var result = new List<CodeInstruction>();
            var replaced = false;

            foreach (var instruction in instructions)
            {
                // The method contains exactly one ldc.i4.8: the "videos.Count <= 8" limit.
                if (!replaced && instruction.opcode == OpCodes.Ldc_I4_8)
                {
                    var call = new CodeInstruction(OpCodes.Call, getMaxCount);
                    call.labels.AddRange(instruction.labels);
                    call.blocks.AddRange(instruction.blocks);

                    result.Add(call);

                    replaced = true;

                    continue;
                }

                result.Add(instruction);
            }

            if (!replaced)
            {
                Plugin.Instance?.Logger.Warn(
                    "UnlockMultiVersionLimit - the 8-version constant was not found; " +
                    "the Emby.Naming implementation may have changed");
            }

            return result;
        }

        private static int GetMaxMultiVersionCount()
        {
            return Plugin.Instance?.GetPluginOptions()?.ExperienceEnhanceOptions.MergeMultiVersion == true
                ? UnlockedMaxVersions
                : EmbyDefaultMaxVersions;
        }
    }
}
