using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/Douban/Recommendations", "GET")]
    [Authenticated]
    public class GetDoubanRecommendations : IReturn<DoubanRecommendationsResponse>
    {
        [ApiMember(Name = "Limit", Description = "Maximum number of recommendations", IsRequired = false,
            DataType = "int", ParameterType = "query")]
        public int? Limit { get; set; }

        [ApiMember(Name = "Kind", Description = "hot or showing", IsRequired = false, DataType = "string",
            ParameterType = "query")]
        public string Kind { get; set; }
    }

    public class DoubanRecommendationsResponse
    {
        public List<DoubanRecommendationInfo> Items { get; set; } = new List<DoubanRecommendationInfo>();
    }

    public class DoubanRecommendationInfo
    {
        public string DoubanId { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string CoverUrl { get; set; }
        public string Subtitle { get; set; }
        public double? Rating { get; set; }
        public string ItemId { get; set; }
        public string ItemType { get; set; }
        public string TrailerUrl { get; set; }
        public string TrailerTitle { get; set; }
        public string TrailerCoverUrl { get; set; }
    }

    [Route("/StrmAssistant/Douban/Trailer", "GET")]
    [Authenticated]
    public class GetDoubanTrailer : IReturn<DoubanTrailerResponse>
    {
        [ApiMember(Name = "DoubanId", Description = "Douban subject id", IsRequired = true,
            DataType = "string", ParameterType = "query")]
        public string DoubanId { get; set; }

        [ApiMember(Name = "SubjectType", Description = "movie or tv", IsRequired = false,
            DataType = "string", ParameterType = "query")]
        public string SubjectType { get; set; }
    }

    public class DoubanTrailerResponse
    {
        public string DoubanId { get; set; }
        public string TrailerUrl { get; set; }
        public string TrailerTitle { get; set; }
        public string CoverUrl { get; set; }
        public string Runtime { get; set; }
    }
}
