using MediaBrowser.Controller.Api;
using StrmAssistant.Common;
using StrmAssistant.Web.Api;
using System.Threading.Tasks;

namespace StrmAssistant.Web.Service
{
    /// <summary>
    /// Answers GET /System/Info/Public with Emby's own payload while replacing LocalAddresses by
    /// the configured local discovery address. Only used when that option is set at startup.
    /// </summary>
    public class LocalDiscoverySystemService : BaseApiService
    {
        public async Task<object> Get(GetLocalDiscoveryPublicSystemInfo request)
        {
            var info = await ApplicationHost
                .GetPublicSystemInfo(Request.RemoteIp, AuthorizationContext.GetAuthorizationInfo(Request),
                    Request.CancellationToken)
                .ConfigureAwait(false);

            try
            {
                var address = LocalDiscoveryApi.GetEffectiveAddress();

                // Emby only exposes local addresses to authenticated callers; keep that rule.
                if (info != null && !string.IsNullOrEmpty(address) && info.LocalAddresses != null &&
                    info.LocalAddresses.Length > 0)
                {
                    info.LocalAddresses = new[] { address };
                }
            }
            catch
            {
                // Keep the server's own address when the option cannot be read.
            }

            return ToOptimizedResult(info);
        }
    }
}