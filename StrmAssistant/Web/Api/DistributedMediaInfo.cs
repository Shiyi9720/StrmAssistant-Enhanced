using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed media info extraction. The request carries no
    /// Emby user token; it is authenticated with the shared key configured on every node.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/MediaInfo", "POST")]
    [Unauthenticated]
    public class DistributedMediaInfoRequest : IReturn<DistributedMediaInfoResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Media path (or URL) as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>File or Http (Emby also uses Http for https URLs).</summary>
        public string Protocol { get; set; }

        public string Container { get; set; }

        public string Name { get; set; }

        /// <summary>Video or Audio.</summary>
        public string MediaType { get; set; }

        public bool ExtractChapters { get; set; }

        /// <summary>Maximum time the worker may spend probing.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedMediaInfoResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        /// <summary>
        /// Serialized media info payload. It is kept as a string so that both sides always use
        /// the same JSON serializer and enum formatting.
        /// </summary>
        public string MediaInfoJson { get; set; }

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
