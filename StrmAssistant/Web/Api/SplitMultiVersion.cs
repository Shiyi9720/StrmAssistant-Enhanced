using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{Id}/SplitMultiVersion", "POST")]
    [Authenticated]
    public class SplitMultiVersion : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }
}
