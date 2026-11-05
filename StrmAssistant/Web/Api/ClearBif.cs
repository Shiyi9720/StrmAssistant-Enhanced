using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{Id}/ClearBif", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearBif : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }
}
