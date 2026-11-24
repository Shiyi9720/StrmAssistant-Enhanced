using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Removes duplicate people rows (same name and person type, case-insensitive) from one
    /// item. The first row wins, so a role/order already written by Emby is kept.
    /// </summary>
    [Route("/Items/{Id}/DedupePeople", "POST")]
    [Authenticated(Roles = "Admin")]
    public class DedupePeople : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }
}
