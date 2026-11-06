using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed media info extraction. The request carries no
    /// Emby user token; it is authenticated with the shared key configured on every node. The
    /// endpoint only accepts a local path and delegates to the worker's static ffprobe; it never
    /// accepts a command line or arbitrary ffmpeg arguments.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/MediaInfo", "POST")]
    [Unauthenticated]
    public class DistributedMediaInfoRequest : IReturn<DistributedMediaInfoResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Local media path as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>Maximum time the worker may spend probing.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedMediaInfoResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        /// <summary>
        /// Stable reason code used by the master to distinguish a timeout, a worker failure and a
        /// path rejection. Known values include path_not_allowed, timeout and worker_error.
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>
        /// Raw ffprobe JSON produced by the worker's static ffprobe. It is kept as a string so
        /// that the master can deserialize it with the same serializer as the worker.
        /// </summary>
        public string FfprobeJson { get; set; }

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
