using MediaBrowser.Controller.Api;
using StrmAssistant.Web.Api;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Worker side of distributed HDR Vivid detection. Emby discovers services in the plugin
    /// assembly automatically; the shared key is validated inside the API component.
    /// </summary>
    public class DistributedHdrVividService : BaseApiService
    {
        public Task<object> Post(DistributedHdrVividRequest request)
        {
            return HandleAsync(request);
        }

        private static async Task<object> HandleAsync(DistributedHdrVividRequest request)
        {
            var api = Plugin.DistributedExtractApi;

            if (api is null)
            {
                return new DistributedHdrVividResponse
                {
                    Success = false,
                    Error = "Distributed extraction is not initialized"
                };
            }

            return await api.HandleWorkerHdrVividRequestAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
