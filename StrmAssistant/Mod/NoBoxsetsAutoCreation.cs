using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using System;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    /// <summary>
    /// Hides the auto-created Collections (BoxSets) library from user views and, when the
    /// library has been removed, prevents Emby from creating it again. Collections that the
    /// user creates manually are not affected.
    /// </summary>
    public class NoBoxsetsAutoCreation : PatchBase<NoBoxsetsAutoCreation>
    {
        private static MethodInfo _ensureLibraryFolder;
        private static MethodInfo _getUserViews;

        public NoBoxsetsAutoCreation()
        {
            Initialize();

            if (PatchTracker.FallbackPatchApproach != PatchApproach.Harmony)
            {
                return;
            }

            Patch();
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");

            var collectionManager = embyServerImplementationsAssembly.GetType(
                "Emby.Server.Implementations.Collections.CollectionManager");
            _ensureLibraryFolder = collectionManager?.GetMethod("EnsureLibraryFolder",
                BindingFlags.Instance | BindingFlags.NonPublic);

            var userViewManager = embyServerImplementationsAssembly.GetType(
                "Emby.Server.Implementations.Library.UserViewManager");
            // The API layer calls the three-parameter overload directly; filtering its result
            // covers both /Users/{id}/Views and the home sections.
            _getUserViews = userViewManager?
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GetUserViews" && m.GetParameters().Length == 3);

            if (_ensureLibraryFolder is null || _getUserViews is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.FailureReason =
                    (_ensureLibraryFolder is null ? "EnsureLibraryFolder" : "GetUserViews") +
                    " could not be located";

                Plugin.Instance.Logger.Warn(
                    "NoBoxsetsAutoCreation - The Emby methods to patch could not be located");
            }
        }

        protected override void Prepare(bool apply)
        {
            var specs = new[]
            {
                new PatchSpec("EnsureLibraryFolder", _ensureLibraryFolder,
                    prefix: nameof(EnsureLibraryFolderPrefix)),
                new PatchSpec("GetUserViews", _getUserViews, postfix: nameof(GetUserViewsPostfix))
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

        /// <summary>
        /// Skips CollectionManager.EnsureLibraryFolder, which is the only path that creates the
        /// "Collections" library automatically.
        /// </summary>
        [HarmonyPrefix]
        private static bool EnsureLibraryFolderPrefix()
        {
            return false;
        }

        [HarmonyPostfix]
        private static void GetUserViewsPostfix(ref Folder[] __result)
        {
            if (__result is null || __result.Length == 0) return;

            if (__result.Any(IsBoxSetsLibrary))
            {
                __result = __result.Where(i => !IsBoxSetsLibrary(i)).ToArray();
            }
        }

        private static bool IsBoxSetsLibrary(Folder folder)
        {
            return folder is CollectionFolder library &&
                   string.Equals(library.CollectionType, CollectionType.BoxSets.ToString(),
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
