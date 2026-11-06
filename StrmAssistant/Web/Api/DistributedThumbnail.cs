using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed chapter image extraction. The request carries no
    /// Emby user token; it is authenticated with the shared key configured on every node.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/Thumbnail", "POST")]
    [Unauthenticated]
    public class DistributedThumbnailRequest : IReturn<DistributedThumbnailResponse>
    {
        public string ApiKey { get; set; }

        /// <summary>Media path (or URL) as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>File or Http (Emby also uses Http for https URLs).</summary>
        public string Protocol { get; set; }

        public string Container { get; set; }

        public string Name { get; set; }

        /// <summary>Frames to extract. The worker returns one JPEG per point.</summary>
        public List<DistributedThumbnailPoint> Points { get; set; } = new List<DistributedThumbnailPoint>();

        /// <summary>Maximum time the worker may spend extracting.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedThumbnailPoint
    {
        /// <summary>Chapter start position, used by the master to match the returned frame.</summary>
        public long StartPositionTicks { get; set; }

        /// <summary>Frame position in the media, already resolved by the master.</summary>
        public double OffsetSeconds { get; set; }
    }

    public class DistributedThumbnailImage
    {
        public long StartPositionTicks { get; set; }

        /// <summary>Base64 encoded JPEG frame.</summary>
        public string Data { get; set; }
    }

    public class DistributedThumbnailResponse
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        public List<DistributedThumbnailImage> Images { get; set; } =
            new List<DistributedThumbnailImage>();

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
