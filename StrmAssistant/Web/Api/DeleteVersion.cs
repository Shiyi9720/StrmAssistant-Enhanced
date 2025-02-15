using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{Id}/DeleteVersion", "POST")]
    [Authenticated]
    public class DeleteVersion : IReturnVoid, IReturn
    {
        public string Id { get; set; }
    }
}
