using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Queues the plugin's Chinese search index rebuild task. The dashboard task panel calls this
    /// endpoint; the index is rebuilt from the item database in the background.
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
