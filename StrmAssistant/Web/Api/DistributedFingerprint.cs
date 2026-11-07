using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed intro fingerprint extraction. The request carries
    /// no Emby user token; it is authenticated with the shared key configured on every node. Only
    /// a local media file inside one of the worker's library roots is accepted.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/Fingerprint", "POST")]
    [Unauthenticated]
    public class DistributedFingerprintRequest : IReturn<DistributedFingerprintResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Local media path as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>Start of the audio sample, in seconds. The master decides this value.</summary>
        public double StartSeconds { get; set; }

        /// <summary>Length of the audio sample, in seconds. The master decides this value.</summary>
        public double DurationSeconds { get; set; }

        /// <summary>Maximum time the worker may spend extracting.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedFingerprintResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        /// <summary>
        /// Stable reason code used by the master to distinguish a timeout, a worker failure and a
        /// path rejection. Known values include path_not_allowed, timeout and worker_error.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>
        /// Base64 encoded raw chromaprint data (the content of the .fp file). Kept inside a JSON
        /// envelope so the shared HTTP transport does not need a binary protocol.
        /// </summary>
        public string Data { get; set; }

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
