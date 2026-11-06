using MediaBrowser.Controller.Api;
using StrmAssistant.Web.Api;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Worker side of distributed chapter image extraction. Emby discovers services in the plugin
    /// assembly automatically; the shared key is validated inside the API component.
    /// </summary>
    public class DistributedThumbnailService : BaseApiService
    {
        public Task<object> Post(DistributedThumbnailRequest request)
        {
            return HandleAsync(request);
        }

        private static async Task<object> HandleAsync(DistributedThumbnailRequest request)
        {
            var api = Plugin.DistributedExtractApi;

            if (api is null)
            {
                return new DistributedThumbnailResponse
                {
                    Success = false,
                    Error = "Distributed extraction is not initialized"
                };
            }

            return await api.HandleWorkerThumbnailRequestAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
