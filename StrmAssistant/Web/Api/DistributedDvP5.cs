using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using System.Collections.Generic;

namespace StrmAssistant.Web.Api
{
    /// <summary>
    /// Worker node endpoint used by distributed Dolby Vision Profile 5 chapter image extraction.
    /// The request carries no Emby user token; it is authenticated with the shared key configured
    /// on every node. The worker only accepts a local media file inside one of its library folders
    /// and requires its own custom ffmpeg build with Vulkan + libplacebo.
    /// </summary>
    [Route("/StrmAssistant/DistributedExtract/DvP5", "POST")]
    [Unauthenticated]
    public class DistributedDvP5Request : IReturn<DistributedDvP5Response>
    {
        public string ApiKey { get; set; }

        /// <summary>Local media path as seen by the worker node.</summary>
        public string Path { get; set; }

        /// <summary>Only File is accepted by this endpoint.</summary>
        public string Protocol { get; set; }

        /// <summary>Frames to extract. The worker returns one JPEG per point.</summary>
        public List<DistributedDvP5Point> Points { get; set; } = new List<DistributedDvP5Point>();

        /// <summary>Maximum time the worker may spend extracting.</summary>
        public int TimeoutSeconds { get; set; }
    }

    public class DistributedDvP5Point
    {
        /// <summary>Chapter start position, used by the master to match the returned frame.</summary>
        public long StartPositionTicks { get; set; }

        /// <summary>Frame position in the media, already resolved by the master.</summary>
        public double OffsetSeconds { get; set; }
    }

    public class DistributedDvP5Image
    {
        public long StartPositionTicks { get; set; }

        /// <summary>Base64 encoded JPEG frame.</summary>
        public string Data { get; set; }
    }

    public class DistributedDvP5Response
    {
        public bool Success { get; set; }

        public string Error { get; set; }

        public List<DistributedDvP5Image> Images { get; set; } =
            new List<DistributedDvP5Image>();

        public string NodeName { get; set; }

        public long ElapsedMs { get; set; }
    }
}
