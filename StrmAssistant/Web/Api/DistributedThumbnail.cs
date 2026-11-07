using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed chapter image extraction. The request carries no
    /// Emby user token; it is authenticated with the shared key configured on every node. Only a
    /// local media file inside one of the worker's library roots is accepted.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/Thumbnail", "POST")]
    [Unauthenticated]
    public class DistributedThumbnailRequest : IReturn<DistributedThumbnailResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Local video path as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>Frame position in the media, in seconds.</summary>
        public double TimeSeconds { get; set; }

        /// <summary>Maximum output width in pixels. 0 keeps the source width.</summary>
        public int Width { get; set; }

        /// <summary>Maximum time the worker may spend extracting.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedThumbnailResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        /// <summary>
        /// Stable reason code used by the master to distinguish a timeout, a worker failure and a
        /// path rejection. Known values include path_not_allowed, timeout and worker_error.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>Base64 encoded JPEG frame.</summary>
        public string Data { get; set; }

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
