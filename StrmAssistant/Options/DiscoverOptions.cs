using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;

namespace StrmAssistant.Options
{
    /// <summary>
    /// 探索发现 Internet Channel. Every switch defaults to off; turning the channel on is an
    /// explicit decision because it adds a top-level entry to every client.
    /// </summary>
    public class DiscoverOptions : EditableOptionsBase
    {
        [DisplayName("探索发现频道")]
        public override string EditorTitle => "探索发现频道";

        [DisplayName("启用探索发现频道")]
        [Description("在 Emby 客户端「频道」里增加一个豆瓣合集频道：浏览海报/评分/年份/简介，" +
                     "区分已在库与缺失。默认关闭。")]
        public bool Enabled { get; set; } = false;

        [DisplayName("频道名称")]
        [Description("显示在客户端频道列表里的名称。注意：Emby 用「Channel 「+名称」生成频道实体 ID，" +
                     "改名后旧频道可能残留，需要在 Emby 里删除旧频道或重启后清理。默认 探索发现。")]
        [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
        public string ChannelName { get; set; } = "探索发现";

        [DisplayName("豆瓣合集")]
        [Description("每行一个：合集标识|名称|数量。合集标识来自豆瓣合集页地址中的 subject_collection/<标识>；" +
                     "名称与数量可省略，数量默认 20、最大 100。默认留空。")]
        [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
        public string Collections { get; set; } = string.Empty;

        [DisplayName("合集缓存分钟数")]
        [Description("同一合集在该时间内复用上次拉取的结果。Emby 的「刷新 Internet Channel」计划任务" +
                     "完成后会立即清空缓存。默认 30。")]
        [Required, MinValue(1), MaxValue(1440)]
        [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
        public int CacheMinutes { get; set; } = 30;

        [DisplayName("在线播放预告片")]
        [Description("点击频道条目时解析豆瓣预告片并直接在线播放（不下载、不落盘）。默认关闭。")]
        [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
        public bool TrailerEnabled { get; set; } = false;

        [DisplayName("收藏到 MoviePilot")]
        [Description("在频道条目详情页点心形收藏时，向 MoviePilot 发送一次订阅。默认关闭。")]
        [VisibleCondition(nameof(Enabled), SimpleCondition.IsTrue)]
        public bool SubscribeEnabled { get; set; } = false;

        [DisplayName("MoviePilot 地址")]
        [Description("例如 http://192.168.31.51:3000，不要带结尾斜杠。默认留空。")]
        [VisibleCondition(nameof(SubscribeEnabled), SimpleCondition.IsTrue)]
        public string MoviePilotUrl { get; set; } = string.Empty;

        [DisplayName("MoviePilot Token")]
        [Description("MoviePilot 的 API Token（apikey/token 查询参数）。默认留空。")]
        [VisibleCondition(nameof(SubscribeEnabled), SimpleCondition.IsTrue)]
        public string MoviePilotToken { get; set; } = string.Empty;

        [DisplayName("订阅方式")]
        [Description("webhook：向配置地址 POST 一个 library.subscribe JSON 事件（任务要求的方式）；" +
                     "api：调用 MoviePilot 的 /api/v1/subscribe/。默认 webhook。")]
        [VisibleCondition(nameof(SubscribeEnabled), SimpleCondition.IsTrue)]
        public DiscoverSubscribeMode SubscribeMode { get; set; } = DiscoverSubscribeMode.Webhook;
    }

    public enum DiscoverSubscribeMode
    {
        [Description("webhook（library.subscribe）")]
        Webhook,

        [Description("MoviePilot API")]
        Api
    }
}
