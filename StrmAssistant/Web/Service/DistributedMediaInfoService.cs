using MediaBrowser.Controller.Api;
using StrmAssistant.Web.Api;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Worker side of distributed media info extraction. Emby discovers services in the plugin
    /// assembly automatically; the shared key is validated inside the API component.
    /// </summary>
    public class DistributedMediaInfoService : BaseApiService
    {
        public Task<object> Post(DistributedMediaInfoRequest request)
        {
            return HandleAsync(request);
        }

        private static async Task<object> HandleAsync(DistributedMediaInfoRequest request)
        {
            var api = Plugin.DistributedExtractApi;

            if (api is null)
            {
                return new DistributedMediaInfoResponse
                {
                    Success = false,
                    Error = "Distributed extraction is not initialized"
                };
            }

            return await api.HandleWorkerRequestAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
