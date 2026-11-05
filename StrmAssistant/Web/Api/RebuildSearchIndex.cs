using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Emby 4.10 has no standalone search index task. Search results are served from the item
    /// database, so this queues the built-in "Scan media library" task (RefreshLibrary), which
    /// re-indexes the library and refreshes what search returns.
    /// </summary>
    [Route("/StrmAssistant/Search/Rebuild", "POST")]
    [Authenticated]
    public class RebuildSearchIndex : IReturn<RebuildSearchIndexResponse>
    {
    }

    public class RebuildSearchIndexResponse
    {
        public bool Queued { get; set; }
        public string TaskName { get; set; }
    }
}
