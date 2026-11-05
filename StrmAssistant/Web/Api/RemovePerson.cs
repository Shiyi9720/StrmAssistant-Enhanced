using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/Items/{Id}/RemovePerson", "POST")]
    [Authenticated(Roles = "Admin")]
    public class RemovePerson : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }

        [ApiMember(Name = "PersonName", Description = "Name of the person to remove", IsRequired = true,
            DataType = "string", ParameterType = "query")]
        public string PersonName { get; set; }

        [ApiMember(Name = "PersonRole", Description = "Role of the person to remove", IsRequired = false,
            DataType = "string", ParameterType = "query")]
        public string PersonRole { get; set; }
    }
}
