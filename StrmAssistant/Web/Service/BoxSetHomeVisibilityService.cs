using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using StrmAssistant.Web.Api;

namespace StrmAssistant.Web.Service
{
    public class BoxSetHomeVisibilityService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public BoxSetHomeVisibilityService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public void Post(SetBoxSetHomeVisibility request)
        {
            if (!(_libraryManager.GetItemById(request.Id) is CollectionFolder library) ||
                !string.Equals(library.CollectionType, CollectionType.BoxSets.ToString(),
                    System.StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var compactId = library.Id.ToString("N");
            var formattedId = library.Id.ToString();

            Plugin.BoxSetHomeVisibilityStore.SetHidden(compactId, request.Hidden);

            if (!string.Equals(compactId, formattedId, System.StringComparison.OrdinalIgnoreCase))
            {
                Plugin.BoxSetHomeVisibilityStore.SetHidden(formattedId, request.Hidden);
            }
        }
    }
}
