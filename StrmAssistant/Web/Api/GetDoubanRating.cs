using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Items/{Id}/DoubanRating", "GET")]
    [Authenticated]
    public class GetDoubanRating : IReturn<DoubanRatingResponse>
    {
        [ApiMember(Name = "Id", Description = "Item Id", IsRequired = true, DataType = "string",
            ParameterType = "path")]
        public string Id { get; set; }
    }

    public class DoubanRatingResponse
    {
        public double? Rating { get; set; }
        public int? RatingCount { get; set; }
        public string DoubanId { get; set; }
    }
}
