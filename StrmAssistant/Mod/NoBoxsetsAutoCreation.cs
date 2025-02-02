using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Library;
using System;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public static class NoBoxsetsAutoCreation
    {
        private static readonly PatchApproachTracker PatchApproachTracker =
            new PatchApproachTracker(nameof(NoBoxsetsAutoCreation));

        private static MethodInfo _ensureLibraryFolder;
        private static MethodInfo _getUserViews;

        public static void Initialize()
        {
            if (Plugin.Instance.ApplicationHost.ApplicationVersion >= new Version("4.8.4.0"))
            {
                try
                {
                    var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                    var collectionManager =
                        embyServerImplementationsAssembly.GetType(
                            "Emby.Server.Implementations.Collections.CollectionManager");
                    _ensureLibraryFolder = collectionManager.GetMethod("EnsureLibraryFolder",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    var userViewManager =
                        embyServerImplementationsAssembly.GetType(
                            "Emby.Server.Implementations.Library.UserViewManager");
                    _getUserViews = userViewManager.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                        .FirstOrDefault(m => m.Name == "GetUserViews" &&
                                             (m.GetParameters().Length == 3 || m.GetParameters().Length == 4));
                }
                catch (Exception e)
                {
                    Plugin.Instance.Logger.Warn("NoBoxsetsAutoCreation - Patch Init Failed");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    PatchApproachTracker.FallbackPatchApproach = PatchApproach.None;
                }

                if (HarmonyMod == null) PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;

                if (PatchApproachTracker.FallbackPatchApproach != PatchApproach.None &&
                    Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.NoBoxsetsAutoCreation)
                {
                    Patch();
                }
            }
            else
            {
                PatchApproachTracker.FallbackPatchApproach = PatchApproach.None;
                PatchApproachTracker.IsSupported= false;
            }
        }

        public static void Patch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (!IsPatched(_ensureLibraryFolder, typeof(NoBoxsetsAutoCreation)))
                    {
                        HarmonyMod.Patch(_ensureLibraryFolder,
                            prefix: new HarmonyMethod(typeof(NoBoxsetsAutoCreation).GetMethod("EnsureLibraryFolderPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch EnsureLibraryFolder Success by Harmony");
                    }
                    if (!IsPatched(_getUserViews, typeof(NoBoxsetsAutoCreation)))
                    {
                        HarmonyMod.Patch(_getUserViews,
                            prefix: new HarmonyMethod(typeof(NoBoxsetsAutoCreation).GetMethod("GetUserViewsPrefix",
                                BindingFlags.Static | BindingFlags.NonPublic)));
                        Plugin.Instance.Logger.Debug(
                            "Patch GetUserViews Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Patch EnsureLibraryFolder Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    PatchApproachTracker.FallbackPatchApproach = PatchApproach.Reflection;
                }
            }
        }

        public static void Unpatch()
        {
            if (PatchApproachTracker.FallbackPatchApproach == PatchApproach.Harmony)
            {
                try
                {
                    if (IsPatched(_ensureLibraryFolder, typeof(NoBoxsetsAutoCreation)))
                    {
                        HarmonyMod.Unpatch(_ensureLibraryFolder,
                            AccessTools.Method(typeof(NoBoxsetsAutoCreation), "EnsureLibraryFolderPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch EnsureLibraryFolder Success by Harmony");
                    }
                    if (IsPatched(_getUserViews, typeof(NoBoxsetsAutoCreation)))
                    {
                        HarmonyMod.Unpatch(_getUserViews,
                            AccessTools.Method(typeof(NoBoxsetsAutoCreation), "GetUserViewsPrefix"));
                        Plugin.Instance.Logger.Debug("Unpatch GetUserViews Success by Harmony");
                    }
                }
                catch (Exception he)
                {
                    Plugin.Instance.Logger.Debug("Unpatch EnsureLibraryFolder Failed by Harmony");
                    Plugin.Instance.Logger.Debug(he.Message);
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                }
            }
        }

        [HarmonyPrefix]
        private static bool EnsureLibraryFolderPrefix()
        {
            return false;
        }

        [HarmonyPrefix]
        private static bool GetUserViewsPrefix(UserViewQuery query, User user, ref Folder[] folders)
        {
            folders = folders.Where(i => !(i is CollectionFolder library) ||
                                         library.CollectionType != CollectionType.BoxSets.ToString())
                .ToArray();

            return true;
        }
    }
}
