using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using System.Reflection;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnableDeepDelete : PatchBase<EnableDeepDelete>
    {
        private static MethodInfo _deleteItem;

        public EnableDeepDelete()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().EnableDeepDelete)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var libraryManager =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Library.LibraryManager");
            _deleteItem = libraryManager.GetMethod("DeleteItem",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(BaseItem), typeof(DeleteOptions), typeof(BaseItem), typeof(bool) }, null);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _deleteItem, prefix: nameof(DeleteItemPrefix));
        }

        [HarmonyPrefix]
        private static bool DeleteItemPrefix(BaseItem item, DeleteOptions options, BaseItem parent,
            bool notifyParentItem)
        {
            if (options.DeleteFileLocation)
            {
                var mountPaths = Plugin.LibraryApi.PrepareDeepDelete(item, false);

                if (mountPaths.Count > 0)
                {
                    Task.Run(() => Plugin.LibraryApi.ExecuteDeepDelete(mountPaths)).ConfigureAwait(false);
                }
            }

            return true;
        }
    }
}
