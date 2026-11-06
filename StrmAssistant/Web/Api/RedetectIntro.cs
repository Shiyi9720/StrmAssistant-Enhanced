using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Items/{Id}/RedetectIntro", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RedetectIntro : IReturn<RedetectIntroResponse>
    {
        [ApiMember(Name = "Id", Description = "Series or season item id", IsRequired = true,
            DataType = "string", ParameterType = "path")]
        public string Id { get; set; }
    }

    public class RedetectIntroResponse
    {
        public bool Queued { get; set; }
        public bool AlreadyRunning { get; set; }
    }
}
