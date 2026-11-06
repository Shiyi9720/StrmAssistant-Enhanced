using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed HDR Vivid detection. The request carries no Emby
    /// user token; it is authenticated with the shared key configured on every node. The worker
    /// only accepts a local media file inside one of its library folders.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/HdrVivid", "POST")]
    [Unauthenticated]
    public class DistributedHdrVividRequest : IReturn<DistributedHdrVividResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Local media path as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>Only File is accepted by this endpoint.</summary>
        public string Protocol { get; set; }

        /// <summary>Maximum time the worker may spend probing.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedHdrVividResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        /// <summary>True when the first video frame carries CUVA / HDR Vivid metadata.</summary>
        public bool Detected { get; set; }

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
