using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Clears both the generated chapter thumbnails and the BIF thumbnail sets of one video.
    /// The chapter entries (names and timestamps) are kept, matching the separate
    /// ClearChapterImages / ClearBif commands but in a single action.
    /// </summary>
    [Route("/Items/{Id}/ClearChapterImagesAndBif", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ClearChapterImagesAndBif : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }
}
