using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    [Route("/StrmAssistant/DiscoverImage", "GET",
        Summary = "Proxies a Douban poster with the Referer the CDN requires")]
    public class GetDiscoverImage : IReturnVoid, IReturn
    {
        [ApiMember(Name = "Url", Description = "Douban image URL", IsRequired = true,
            DataType = "string", ParameterType = "query")]
        public string Url { get; set; }
    }
}
