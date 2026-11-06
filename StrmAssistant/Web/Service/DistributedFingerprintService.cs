using MediaBrowser.Controller.Api;
using StrmAssistant.Web.Api;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Worker side of distributed intro fingerprint extraction. Emby discovers services in the
    /// plugin assembly automatically; the shared key is validated inside the API component.
    /// </summary>
    public class DistributedFingerprintService : BaseApiService
    {
        public Task<object> Post(DistributedFingerprintRequest request)
        {
            return HandleAsync(request);
        }

        private static async Task<object> HandleAsync(DistributedFingerprintRequest request)
        {
            var api = Plugin.DistributedExtractApi;

            if (api is null)
            {
                return new DistributedFingerprintResponse
                {
                    Success = false,
                    Error = "Distributed extraction is not initialized"
                };
            }

            return await api.HandleWorkerFingerprintRequestAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
