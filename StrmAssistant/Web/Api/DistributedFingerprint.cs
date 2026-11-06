using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed intro fingerprint extraction. The request carries
    /// no Emby user token; it is authenticated with the shared key configured on every node.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/Fingerprint", "POST")]
    [Unauthenticated]
    public class DistributedFingerprintRequest : IReturn<DistributedFingerprintResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Media path (or URL) as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>File or Http (Emby also uses Http for https URLs).</summary>
        public string Protocol { get; set; }

        public string Name { get; set; }

        /// <summary>Length of the audio sample used for the fingerprint, in minutes.</summary>
        public int Minutes { get; set; }

        /// <summary>Maximum time the worker may spend extracting.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedFingerprintResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        /// <summary>Base64 encoded raw chromaprint data (the content of the .fp file).</summary>
        public string Data { get; set; }

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
