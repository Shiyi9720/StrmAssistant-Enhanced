# 本地 TMDB 元数据源（第一切片）


## 目录结构

用户指定 `LocalMetadataRoot` 后，插件在启动和保存选项时创建以下结构（不存在则创建，失败只记日志）：

```text
<LocalMetadataRoot>/
├── cache/
│   ├── tmdb-movies2/<tmdbid>/movie.json
│   ├── tmdb-tv/<tmdbid>/series.json
│   ├── tmdb-tv/<tmdbid>/season-1.json
│   ├── tmdb-tv/<tmdbid>/season-1-episode-1.json
│   ├── tmdb-movies2/<tmdbid>/tags.json
│   ├── tmdb-tv/<tmdbid>/tags.json
│   └── tmdb-collections/<tmdbid>/collection.json
└── override/
    ├── tmdb-movies2/<tmdbid>/movie.json
    ├── tmdb-tv/<tmdbid>/series.json
    ├── tmdb-tv/<tmdbid>/season-1.json
    ├── tmdb-tv/<tmdbid>/season-1-episode-1.json
    ├── tmdb-movies2/<tmdbid>/tags.json
    ├── tmdb-tv/<tmdbid>/tags.json
    └── tmdb-collections/<tmdbid>/collection.json
```

每个 tmdbid 目录下可包含 `images/`：

```text
<tmdbid>/images/poster.jpg
<tmdbid>/images/fanart.jpg
<tmdbid>/images/clearlogo.png
<tmdbid>/images/season-5.jpg
<tmdbid>/images/episode-5-1.jpg
```

季和集只写海报（集的 `still_path` 也按海报处理）。季集图片按媒体库季集号命名（即 `episodegroup.json` 映射后的本地季集号），例如 `season-5.jpg`、`episode-5-1.jpg`；电影、剧集、合集支持 `poster`、`fanart`、`clearlogo`、`landscape`、`banner`、`discart`、`clearart`，具体类型取决于 TMDB 响应中实际存在的图片字段。图片由 `LocalMetadataAutoRefreshImages` 控制（默认开）；已存在的图片不会重复下载。

覆盖图片读取先按媒体库季集号查找；找不到时回退到旧缓存使用的 TMDB 默认季集号，并在 Debug 日志标注命中来源。

元数据文件保存的是 TMDB 详情响应 JSON。它正是 Emby 的 TMDB provider 解析的输入形状；缓存内容是经过插件后处理（备选语言、简繁转换、图片语言改写、剧集组还原等）后的最终响应，因此重建媒体库时可直接复用。新写入的被动缓存和 `MovieDbCache` 条目带有 `_meta` 字段，记录来源、请求、语言、是否转换以及是否改写；没有 `_meta` 的旧缓存仍按原始 JSON 读取。

`tags.json` 使用 `{"tags": ["测试标签1", "测试标签2"]}` 格式，电影和剧集在入库/刷新时把自定义标签合并到现有标签（去重，不替换 TMDB 自带标签）。没有 override 文件时读取被动 cache；合集不处理。该功能复用 `EnableLocalMetadataSource` 总开关。

同一 tmdbid 只保留一份 JSON；多版本条目共享它。如果不同媒体库以不同语言同时刮削同一 tmdbid，以最后一次成功写入为准（与官方单份缓存语义一致）。

## 模式与优先级

读取发生在 `MovieDbApi.MovieDbHttpClient.SendAsync`，即 provider 收到 HTTP 响应之前；写入发生在同一请求成功、`PostProcessAsync` 完成之后。

| 模式 | override | 被动 cache | 联网 | cache 写入 |
|---|---|---|---|---|
| `CacheFirst`（默认） | P1 读取 | P2 读取 | P3 | 写 |
| `OnlineWithCache` | P1 读取 | 不读 | P2 | 写 |
| `OverrideOnly` | P1 读取 | 不读 | P2 | 不写 |
| `Off` | 不使用 | 不使用 | P1 | 不写 |

原有 `MovieDbCache`（`EnableMovieDbCache`）是独立的网络响应磁盘缓存；它与本功能的 passive cache 不共用目录、不改变本表语义。

## 调用点

| 位置 | 作用 |
|---|---|
| `StrmAssistant/Options/PluginOptions.cs` | 四个标量设置：开关、根目录、模式、图片自动缓存 |
| `StrmAssistant/Options/LocalMetadataMode.cs` | 四种模式及本地化描述 |
| `StrmAssistant/Plugin.cs` | 启动时初始化目录；保存选项时重新初始化并记录配置；启用时确保 TMDB provider 已注入包装客户端 |
| `Common/MovieDbApi.cs` | 解析 movie/tv/season/episode/collection 详情 URL；先查 override/cache；成功后调用 `CaptureAsync` 写被动缓存 |
| `Common/LocalMetadataSource.cs` | 目录、路径映射、读取优先级、JSON/图片落盘、统计与锁 |

## 并发与幂等

- 同一 tmdbid 的多版本条目天然映射到同一个目录和文件。
- 写文件按路径哈希分片加 `SemaphoreSlim` 锁，先写同目录临时文件，再原子替换目标文件。
- 写入前比较现有字节；内容相同则跳过，避免重复刷新和重复计数。
- 读取只使用完整文件；写失败不会影响在线刮削，异常统一降级为日志。
- 图片下载失败只记录失败计数，不阻断元数据返回。

## 统计日志

刮削活动停止 5 秒后自动输出一条汇总日志：

```text
Local metadata source (scrape summary) - Override hits: X, Cache hits: Y, Online requests: Z, Metadata writes: M, Image writes: N, Failures: F
```

每个 override/cache 命中和写盘也有 Debug 级别明细。统计按活动批次重置，不会无限累计。

## 后续切片

1. 剧集组 `episodegroup.json` 的读取、季集映射与入库。
2. 覆盖缓存图片自动刷入 Emby（本切片只写被动缓存图片，不注入媒体项）。
3. 统一内容分级（US/JP fallback 映射）。
4. 自定义特别篇播出顺序（`episodeorder.json`）。
5. `patches/` 字段级合并补丁与 `external_ids.json`。
