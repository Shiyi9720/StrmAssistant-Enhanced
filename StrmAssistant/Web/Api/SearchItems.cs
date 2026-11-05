using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Search entry point used by the injected web client script. It accepts the same filter
    /// values as Emby's /Users/{id}/Items endpoint so that the search page can keep its request
    /// unchanged apart from the URL. When the Chinese index is not available the response has
    /// Available = false and the script falls back to Emby's own search.
    /// </summary>
    [Route("/StrmAssistant/Search", "GET")]
    [Authenticated]
    public class SearchItems : IReturn<SearchItemsResponse>
    {
        public string Q { get; set; }

        public string SearchTerm { get; set; }

        public string ParentId { get; set; }

        public int? Limit { get; set; }

        public int? StartIndex { get; set; }

        public string IncludeItemTypes { get; set; }

        public string ExcludeItemTypes { get; set; }

        public string Fields { get; set; }

        public int? ImageTypeLimit { get; set; }

        public bool? EnableImages { get; set; }

        public bool? EnableUserData { get; set; }
    }

    public class SearchItemsResponse : QueryResult<BaseItemDto>
    {
        public bool Available { get; set; }
    }
}
