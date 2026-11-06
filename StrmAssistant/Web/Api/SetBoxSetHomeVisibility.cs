using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/BoxSets/{Id}/HomeVisibility", "POST")]
    [Authenticated(Roles = "Admin")]
    public class SetBoxSetHomeVisibility : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Collection library item id", IsRequired = true,
            DataType = "string", ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "Hidden", Description = "Hide the collection library on the home screen",
            IsRequired = true, DataType = "bool", ParameterType = "query")]
        public bool Hidden { get; set; }
    }
}
