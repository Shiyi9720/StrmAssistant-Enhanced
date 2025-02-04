using HarmonyLib;
using MediaBrowser.Controller.Entities;
using StrmAssistant.Common;
using System.Linq;
using System.Reflection;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnforceLibraryOrder : PatchBase<EnforceLibraryOrder>
    {
        private static MethodInfo _getUserViews;

        public EnforceLibraryOrder()
        {
            Initialize();

            if (Plugin.Instance.ExperienceEnhanceStore.GetOptions().UIFunctionOptions.EnforceLibraryOrder)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
            var userViewManager =
                embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.Library.UserViewManager");
            _getUserViews = userViewManager.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "GetUserViews" &&
                                     (m.GetParameters().Length == 3 || m.GetParameters().Length == 4));
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getUserViews, nameof(GetUserViewsPrefix));
        }

        [HarmonyPrefix]
        private static bool GetUserViewsPrefix(User user)
        {
            user.Configuration.OrderedViews = LibraryApi.AdminOrderedViews;

            return true;
        }
    }
}
