using MediaBrowser.Controller.Api;
using StrmAssistant.Web.Api;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Worker side of distributed Dolby Vision Profile 5 chapter image extraction. Emby discovers
    /// services in the plugin assembly automatically; the shared key is validated inside the API
    /// component.
    /// </summary>
    public class DistributedDvP5Service : BaseApiService
    {
        public Task<object> Post(DistributedDvP5Request request)
        {
            return HandleAsync(request);
        }

        private static async Task<object> HandleAsync(DistributedDvP5Request request)
        {
            var api = Plugin.DistributedExtractApi;

            if (api is null)
            {
                return new DistributedDvP5Response
                {
                    Success = false,
                    Error = "Distributed extraction is not initialized"
                };
            }

            return await api.HandleWorkerDvP5RequestAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }
}
