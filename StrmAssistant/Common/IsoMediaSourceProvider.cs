using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    /// <summary>
    /// Registers the main video extracted from an ISO image as an extra playable media source.
    /// The extraction itself is handled by <see cref="IsoApi"/>; this provider only exposes the
    /// cached file once it is available.
    /// </summary>
    public class IsoMediaSourceProvider : IMediaSourceProvider
    {
        public async Task<List<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            var mediaSources = new List<MediaSourceInfo>();

            if (item is null || !IsoApi.IsIsoPath(item.Path)) return mediaSources;

            var extractedPath = await IsoApi.GetOrExtractAsync(item, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(extractedPath) || !File.Exists(extractedPath)) return mediaSources;

            var container = Path.GetExtension(extractedPath).TrimStart('.').ToLowerInvariant();

            long? size = null;

            try
            {
                size = new FileInfo(extractedPath).Length;
            }
            catch
            {
                // Size is informational only.
            }

            mediaSources.Add(new MediaSourceInfo
            {
                Name = Path.GetFileNameWithoutExtension(item.Path) + " (ISO)",
                Path = extractedPath,
                Protocol = MediaProtocol.File,
                Container = container,
                Size = size,
                RunTimeTicks = item.RunTimeTicks,
                Type = MediaSourceType.Default,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true
            });

            return mediaSources;
        }

        public Task<ILiveStream> OpenMediaSource(string openToken, string playSessionId,
            List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            return Task.FromResult<ILiveStream>(null);
        }
    }
}
