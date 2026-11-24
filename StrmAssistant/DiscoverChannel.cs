using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant
{
    /// <summary>
    /// Emby Internet Channel adapter. Emby discovers public <see cref="IChannel"/>
    /// implementations in plugin assemblies and constructs them from the container; the runtime
    /// state lives in <see cref="Plugin.DiscoverApi"/>, so this class stays stateless.
    /// </summary>
    public sealed class DiscoverChannel : IChannel, IRequiresMediaInfoCallback
    {
        private static Common.DiscoverApi Api => Plugin.DiscoverApi;

        public string Name => Api?.ChannelName ?? "探索发现";

        public string Description =>
            "豆瓣合集 → Emby 频道：浏览海报/评分、查看已在库状态、在线播放预告片并收藏到 MoviePilot。";

        public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

        public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query,
            CancellationToken cancellationToken)
        {
            var api = Api;
            if (api == null)
            {
                return Task.FromResult(new ChannelItemResult
                {
                    Items = new List<ChannelItemInfo>()
                });
            }

            return api.GetChannelItemsAsync(query?.FolderId, cancellationToken);
        }

        public Task<DynamicImageResponse> GetChannelImage(ImageType type,
            CancellationToken cancellationToken) =>
            Task.FromResult<DynamicImageResponse>(null);

        public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

        public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id,
            CancellationToken cancellationToken)
        {
            var api = Api;
            if (api == null)
            {
                return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
            }

            return api.GetMediaSourcesAsync(id, cancellationToken);
        }
    }
}
