using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Poster relay for the 探索发现 channel. Emby downloads a channel item's image itself, but
    /// Douban's CDN answers 418 when the request has no Referer/User-Agent, so the item points at
    /// this endpoint and the plugin adds the headers. Only Douban hosts are accepted.
    /// </summary>
    [Unauthenticated]
    public class DiscoverImageService : IService, IRequiresRequest
    {
        private const int MaxBytes = 8 * 1024 * 1024;

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private static readonly string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        private readonly IHttpResultFactory _resultFactory;

        public DiscoverImageService(IHttpResultFactory resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public IRequest Request { get; set; }

        public async Task<object> Get(GetDiscoverImage request)
        {
            if (!DiscoverImageProxy.TryNormalize(request?.Url, out var uri))
            {
                return Text("unsupported image url");
            }

            try
            {
                using var message = new HttpRequestMessage(HttpMethod.Get, uri);
                message.Headers.TryAddWithoutValidation("Referer", "https://m.douban.com/");
                message.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

                using var response = await Http
                    .SendAsync(message, HttpCompletionOption.ResponseContentRead)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Plugin.Instance?.Logger?.Debug("DiscoverImage - upstream " + (int)response.StatusCode +
                                                   " for " + uri);
                    return Text("upstream image unavailable");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes == null || bytes.Length == 0 || bytes.Length > MaxBytes)
                {
                    return Text("upstream image unavailable");
                }

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType) ||
                    !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    contentType = "image/jpeg";
                }

                return _resultFactory.GetResult(Request, new ReadOnlyMemory<byte>(bytes), contentType);
            }
            catch (Exception e)
            {
                Plugin.Instance?.Logger?.Debug("DiscoverImage - relay failed: " + e.Message);
                return Text("upstream image unavailable");
            }
        }

        private object Text(string value) =>
            _resultFactory.GetResult(Request, Encoding.UTF8.GetBytes(value), "text/plain");
    }
}
