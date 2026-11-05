using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Items/{Id}/EpisodeStats", "GET")]
    [Authenticated]
    public class GetEpisodeStats : IReturn<EpisodeStatsResponse>
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }

    public class EpisodeStatsResponse
    {
        public bool IsSeries { get; set; }
        public int Total { get; set; }
        public int InLibrary { get; set; }
    }
}
