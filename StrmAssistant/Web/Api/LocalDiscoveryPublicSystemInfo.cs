using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.System;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Drop-in replacement for Emby's GET /System/Info/Public while a custom local discovery
    /// address is configured. It is only moved in front of Emby's own route when the option is
    /// set; otherwise Emby handles the request exactly as before. The payload is produced by
    /// Emby itself, only LocalAddresses is rewritten.
    /// </summary>
    [Route("/System/Info/Public", "GET", Summary = "Gets public information about the server")]
    [Unauthenticated]
    public class GetLocalDiscoveryPublicSystemInfo : IReturn<PublicSystemInfo>
    {
    }
}