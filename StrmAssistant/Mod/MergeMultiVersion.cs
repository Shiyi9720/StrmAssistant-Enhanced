using HarmonyLib;
using System;
using System.IO;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class MergeMultiVersion : PatchBase<MergeMultiVersion>
    {
        private static MethodInfo _isEligibleForMultiVersion;

        public MergeMultiVersion()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().MergeMultiVersion)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var namingAssembly = Assembly.Load("Emby.Naming");
            var videoListResolverType = namingAssembly.GetType("Emby.Naming.Video.VideoListResolver");
            _isEligibleForMultiVersion = videoListResolverType.GetMethod("IsEligibleForMultiVersion",
                BindingFlags.Static | BindingFlags.NonPublic);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _isEligibleForMultiVersion,
                prefix: nameof(IsEligibleForMultiVersionPrefix));
        }

        [HarmonyPrefix]
        private static bool IsEligibleForMultiVersionPrefix(string folderName, string testFilename, ref bool __result)
        {
            __result = string.Equals(folderName, Path.GetFileName(Path.GetDirectoryName(testFilename)),
                StringComparison.OrdinalIgnoreCase);

            return false;
        }
    }
}
