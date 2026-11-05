using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Library;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;

namespace StrmAssistant.Web.Service
{
    public class VersionPlaybackService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;

        public VersionPlaybackService(ILibraryManager libraryManager)
        {
            _libraryManager = libraryManager;
        }

        public object Get(GetVersionPlayback request)
        {
            var user = GetUserForRequest(null);
            var item = _libraryManager.GetItemById(request.Id);

            if (user is null || item is null) return new VersionPlaybackResponse();

            return new VersionPlaybackResponse
            {
                Items = Plugin.VersionPlaybackApi.GetVersionPlayback(user.InternalId, item)
            };
        }
    }
}
