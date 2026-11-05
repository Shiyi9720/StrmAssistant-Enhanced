using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Items/{Id}/VersionPlayback", "GET")]
    [Authenticated]
    public class GetVersionPlayback : IReturn<VersionPlaybackResponse>
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }
}
