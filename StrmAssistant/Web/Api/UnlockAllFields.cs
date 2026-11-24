using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Unlocks metadata for an item and, recursively, its children: the whole-item lock
    /// (IsLocked) and every individual field lock (LockedFields) are cleared so a later
    /// refresh may overwrite them. All versions sharing the presentation key are covered.
    /// </summary>
    [Route("/Items/{Id}/UnlockAllFields", "POST")]
    [Authenticated(Roles = "Admin")]
    public class UnlockAllFields : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }
}
