using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Items/{Id}/RefreshRecursive", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RefreshRecursive : IReturn<RefreshRecursiveResponse>
    {
        [ApiMember(Name = "Id", Description = "Series or season item id", IsRequired = true,
            DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    public class RefreshRecursiveResponse
    {
        public bool Queued { get; set; }
        public bool AlreadyRunning { get; set; }
    }
}
