# 当前主线与 Pro 39 项最终对照

- 日期：2026-11-07
- 分支：`hermesh/s_pm7f7civ_01m6rvpw8e6m735edyn6e8k7qp`（仅提交本文件，未改代码）
- 基线：`port/emby-4.10-stable` 顶端 `c50bf32`（118 提交）
- 对照源：上游公开 Wiki「PRO版的功能」总表（9 组 39 项）+ 上游公开仓库
- 方法：逐项只读核对当前主线代码（类/方法/设置项/API 路由）。**没有反编译 Pro、没有使用 Pro DLL、没有抄 Pro 代码。**
- 结论：**严格口径 35/39（✅）；🟡 3 项、❌ 1 项；按 🟡=0.5 加权 36.5/39 ≈ 93.6%。**

> 统计口径：✅ 有可用实现；🟡 部分（核心可用但缺 Pro 的明确子能力）；❌ 没有找到实现。
> 证据均指当前主线文件；行号以 `c50bf32` 为准。

## 一、39 项逐项对照

| # | 组 | Pro 功能 | 状态 | 证据（一句话） |
|---:|---|---|:---:|---|
| 1 | 搜索 | 重构中文搜索增强，自适应数据库当前的分词器，极度稳定 | ✅ | `SearchIndexService` 用 `ILibraryManager` 自建倒排索引（拼音/简繁/别名/原名），不依赖 DB 分词器；索引未就绪时 `SearchService.cs:80` 回退 Emby 原生搜索 |
| 2 | 搜索 | 完全适配 Emby 4.9 的数据库连接池机制 | ✅ | 插件搜索不直连 SQLite，全部走 `ILibraryManager`/`InternalItemsQuery`（`SearchService.cs:116-203`），天然规避连接池生命周期问题 |
| 3 | 搜索 | 支持搜索词转换，比如繁转简（Apple TV 语音搜索） | ✅ | `SearchOptions.ConversionSearch`（默认开）；`SearchTextHelper.ToSimplified/ToTraditional` + `SearchIndexService.cs:750-755` |
| 4 | 搜索 | 优化中文分词，支持简繁混搜 | ✅ | 索引同时写入简体与繁体 compact key（`SearchIndexService.cs:464-465,750-755`），任一写法均可命中 |
| 5 | 搜索 | 支持屏蔽搜索推荐列表 | ✅ | `UiEnhancementScript.cs:100` 注入 `.searchSuggestions{display:none!important}`；随样式脚本无条件生效，无独立开关 |
| 6 | 网络 | 代理支持“刮削白名单”和“全局代理”模式 | ✅ | `NetworkOptions.ProxyMode`（Global/Whitelist）+ `ProxyApi.cs:62` 的 `IsProxyEnabled`/`IsWhitelisted` |
| 7 | 网络 | 自定义本地发现地址 | ✅ | `NetworkOptions.LocalDiscoveryAddress`；`LocalDiscoveryApi` 覆写 `/System/Info/Public` 并处理 UDP 发现（`LocalDiscoveryApi.cs:107-140`） |
| 8 | 媒体信息 | 提取 ISO 媒体信息 | ✅ | `IsoApi` 直读 ISO9660/UDF 并提取主视频；`DiscMediaInfoApi.cs:53` 支持 ISO/BDMV/VIDEO_TS 探测流与章节；`IsoMediaSourceProvider` 注册可播放源 |
| 9 | 媒体信息 | 音乐持久化 json，可恢复专辑、艺术家、内嵌封面 | ✅ | `MediaInfoApi.cs:530-547` 恢复 `Audio.Album/AlbumArtists/Artists/EmbeddedImage`；`LibraryApi.cs:413-418` 的 `MediaType.Audio` 覆盖音乐 |
| 10 | 媒体信息 | 截图后处理，确保比例最佳（横屏 16:9 / 竖屏 2:3） | ✅ | `VideoThumbnailApi.CropImage/GetCropFilter`（`:458-531`）把超宽裁 16:9、竖图裁 2:3；受 `CropUltraWideScreenshots`/`CropPortraitScreenshots` 控制 |
| 11 | 媒体信息 | 基于标签或关键字的黑名单（媒体信息/声纹/BIF） | ✅ | `MediaInfoBlacklist`(`mediainfo-exclude`)、`FingerprintBlacklist`(`fingerprint-exclude`)、`VideoThumbnailBlacklist`(`videothumbnail-exclude`)，均支持标签+关键字（+容器） |
| 12 | 媒体信息 | 自定义 ffmpeg 解决 DV P5 截图发绿，支持 ISO 截图和章节图 | ✅ | `DvP5Screenshot` 用 vulkan+libplacebo 重截、失败回退 zscale；ISO 由 `IsoApi`/`DiscMediaInfoApi` 提取主视频或自定义 ffmpeg 的 bluray 协议承接（ISO 截图未做真机实测） |
| 13 | 媒体信息 | 分布式媒体信息提取 | ✅ | `DistributedExtractApi` + `/StrmAssistant/DistributedExtract/{MediaInfo,Thumbnail,Fingerprint,HdrVivid,DvP5}` 五个固定端点 |
| 14 | 元数据 | 本地 TMDB - 构建本地元数据刮削源 | 🟡 | `MovieDbApi.cs:141-150` 只把 TMDB API/图片 URL 重写到 `EnableLocalMovieDb`/`LocalMovieDbUrl`；未找到 Wiki 所述 cache/override 目录、被动缓存、图片自动刷入、分级/特别篇排序 |
| 15 | 元数据 | 豆瓣辅助刮削 | 🟡 | `DoubanApi.SupplementAsync` 只补 Overview/Poster/评分/Douban provider id；`DoubanCacheApi.PrebuildRatingAsync` 只预构建评分。未找到演职人员中文化、季表、烂番茄评分、全库应用/单独构建入口 |
| 16 | 元数据 | 区分简中和繁中的海报和徽标 | ✅ | `MetadataEnhanceOptions.DistinguishChinesePosters` + `MovieDbApi.SetImageLanguageParam`（`:547-580`）按内容语言重写图片语言，覆盖 Logo/Backdrop 等所有图片类型 |
| 17 | 元数据 | 新建媒体库默认选择 TMDB 而非 TVDB | ✅ | `MetadataEnhanceOptions.NewLibraryMovieDb` + `LibraryApi.ApplyNewLibraryMovieDbPreference`（`:985`），在 `ItemAdded` 的 `CollectionFolder` 上应用（`Plugin.cs:457-459`） |
| 18 | 元数据 | 备选语言刮削优先繁中并做简转繁 | ✅ | `PreferTraditionalChinese` + `ChineseMetadataOrder.TraditionalFirst` + `ConvertSimplifiedToTraditional`；`LanguageUtility` 负责转换 |
| 19 | 片头 | 各媒体库自定义片头声纹探测时长 | ✅ | `IntroSkipOptions.LibraryIntroDetectionFingerprintOptions` + `GetIntroDetectionFingerprintMinutes`；`IntroDetectionSettings` API 供设置页读写 |
| 20 | 片头 | 并行计算提升声纹处理性能 | ✅ | `ExtractIntroFingerprintTask` 用 `Task.WhenAll`（`:239,:304`）+ `GeneralOptions.IntroDetectionMaxConcurrentCount` 控制并发 |
| 21 | 片头 | 片头片尾刮削（Unified IntroDb） | 🟡 | `IntroDbApi.ApplyToSeasonAsync/ApplyToEpisodeAsync` 已接入指纹探测前查询；`BuildUrl` 虽支持 `isMovie`，但没有电影应用入口/刷新钩子，电影媒体库未接 |
| 22 | 字幕 | 为 Strm 优化 OpenSubtitles 搜索和下载 | ✅ | `SubtitleApi.DownloadSubtitles/CreateSearchRequest`：hash 精确匹配优先，`RequirePerfectSubtitleMatch` 关闭时回退标题搜索（结果排序 `:412-431`，请求构造 `:437-458`）；未解析 strm 目标做真实文件 hash，但标题回退使 strm 可下载 |
| 23 | 字幕 | 外挂字幕扫描同时支持外挂音轨（Emby 4.9） | ❌ | `SubtitleApi.AddExternalTracks` 形参含 `isAudio`，但三处调用（`:160,:178,:189`）全部传 `false`；扫描任务只更新字幕流，未找到外挂音轨扫描/更新路径 |
| 24 | 多版本 | 跨媒体库分开记录播放进度和收藏 | ✅ | `VersionPlaybackApi` 按 `libraryId` 记录 PositionTicks/Played；`VersionFavoriteApi` + `EnableCrossLibraryFavoriteSeparation` 按库记录收藏 |
| 25 | 多版本 | 美化显示多版本，按画质排序 | ✅ | `GeneralOptions.MultiVersionFriendlyNames/MultiVersionSort`；`GetVersionNamesScript` 高亮分辨率/来源/编码/音轨并按画质排序 |
| 26 | 多版本 | 解除同目录合并电影数量 8 的限制，可配置 | ✅ | `GeneralOptions.MaxMultiVersionMergeCount` 默认 8、范围 2-64（`:77`）；`MergeMultiVersionTask.cs:363` 读取并跳过超限分组 |
| 27 | 通知 | 合集项目变更通知 | ✅ | `NotificationApi.CollectionItemUpdateSendNotification`（`:277`）+ `collection.items.added/removed` 事件注册（`CustomNotifications.cs:41-49`） |
| 28 | 通知 | 媒体元数据更新通知 | ✅ | `NotificationApi.MetadataUpdateSendNotification`（`:309`）+ `metadata.update` 事件 |
| 29 | 通知 | 媒体图像更新通知 | ✅ | `NotificationApi.ImageUpdateSendNotification`（`:327`）+ `image.update` 事件 |
| 30 | 体验 | 电视节目显示总集数而非未观看数 | ✅ | `EpisodeStatsService` 统计 Total/In Library；`GetEpisodeStatsScript` 显示 “Total Episodes / In Library” |
| 31 | 体验 | 当前集播完后自动把之前有进度的集标记已播 | ✅ | `PlaySessionMonitor.AutoMarkPreviousEpisodesPlayed`（`:340`）+ `ExperienceEnhanceOptions.AutoMarkPreviousEpisodesPlayed` |
| 32 | 体验 | 按偏好调整显示顺序，支持倒序季集 | ✅ | `ReverseSeasonEpisodeOrder`/`NaturalSort`/`EnforceLibraryOrder`（`ExperienceEnhanceOptions.cs:52,76,83`）+ 对应 UI 脚本 |
| 33 | 体验 | 电视节目页显示合集时考虑季的合集归属 | ✅ | `CollectionService.Get`：series 直接合集 + 其 Season 的合集合并去重（`:74-96`） |
| 34 | 体验 | 支持强制季集显示样式 | ✅ | `ExperienceEnhanceOptions.EpisodeDisplayStyle` + `GetEpisodeDisplayStyleScript` |
| 35 | 体验 | 主屏幕显示媒体库项目数量标记 | ✅ | `GetLibraryBadgeScript`（`UiEnhancementScript.cs:1746`） |
| 36 | 体验 | 按从新到旧显示日志 | ✅ | `GetLogNewestFirstScript`（`UiEnhancementScript.cs:2603`）反转日志/结果数组 |
| 37 | 体验 | 快捷菜单删除个别重复或分裂的演员 | ✅ | `ShortcutMenuHelper.cs:204-218` 菜单项 + `RemovePerson` API + `PersonService` 精确删除单个 Actor（可按角色） |
| 38 | 体验 | 快捷菜单清除章节图和 BIF | ✅ | `ClearChapterImages`/`ClearBif` API + `CleanupService` 对应服务 |
| 39 | 体验 | 快捷菜单清除媒体信息 | ✅ | `ClearMediaInfo` API + `CleanupService.ClearMediaInfoService` |

### 分组说明

- **搜索（5/5）**：实现路线与 Pro 不同——不接管 Emby 数据库 FTS，而是用 `ILibraryManager` 建插件侧倒排索引，索引持久化到插件数据目录，重建不阻塞启动；因此“自适应 DB 分词器”不适用，但“稳定中文搜索”的目标达成。
- **网络（2/2）**：代理模式与本地发现地址都是独立设置项，且 `ProxyApi` 同时包装了外部 HTTP 客户端（刮削/豆瓣/IntroDb）与 OpenSubtitles provider。
- **媒体信息（6/6）**：ISO 直读、音乐持久化、截图后处理、三类黑名单、DV P5 自定义 ffmpeg、分布式固定端点全部有对应实现；其中 ISO 截图和分布式多节点回落未做真机验收。
- **元数据（3/5）**：简繁图片、新建库 TMDB、备选语言三项完整；本地 TMDB 只做到“重写到本地镜像”，豆瓣只做到“评分/简介/海报/预构建评分”。
- **片头（2/3）**：按库时长与并行计算完整；IntroDb 只服务剧集。
- **字幕（1/2）**：OpenSubtitles 下载任务有 strm 友好的标题回退；外挂音轨未接。
- **多版本（3/3）**：跨库进度/收藏、美化排序、合并上限都完整。
- **通知（3/3）**：合集、元数据、图像三类事件均注册并发送。
- **体验增强（10/10）**：10 项都有服务端 API 或前端注入脚本，且都能在设置页找到对应开关。

## 二、汇总

| 组 | ✅ | 🟡 | ❌ | 严格完成度 | 加权（🟡=0.5） |
|---|---:|---:|---:|---:|---:|
| 搜索 | 5 | 0 | 0 | 5/5 | 5.0 |
| 网络 | 2 | 0 | 0 | 2/2 | 2.0 |
| 媒体信息 | 6 | 0 | 0 | 6/6 | 6.0 |
| 元数据 | 3 | 2 | 0 | 3/5 | 4.0 |
| 片头 | 2 | 1 | 0 | 2/3 | 2.5 |
| 字幕 | 1 | 0 | 1 | 1/2 | 1.0 |
| 多版本 | 3 | 0 | 0 | 3/3 | 3.0 |
| 通知 | 3 | 0 | 0 | 3/3 | 3.0 |
| 体验增强 | 10 | 0 | 0 | 10/10 | 10.0 |
| **合计** | **35** | **3** | **1** | **35/39（89.7%）** | **36.5/39（93.6%）** |

结论：9 组中 6 组满分；缺口集中在元数据（本地 TMDB 源、豆瓣深度）、片头（电影 IntroDb）和字幕（外挂音轨）。

## 三、仍缺失 / 部分完成清单（按工作量分档）

### 小

1. **字幕 #23 外挂音轨扫描**（❌）：`SubtitleApi.AddExternalTracks` 已经有 `isAudio` 形参，`GetResolvedMediaStreams` 也支持音频；缺的是把扫描/更新路径的调用点改为 `isAudio: true`，并让 `ScanExternalSubtitleTask` 同时处理音轨。预计 100-200 行 + 一处设置文案。

### 中

2. **片头 #21 电影 IntroDb**（🟡）：`IntroDbApi` 的 `BuildUrl/RequestSourceAsync` 已支持 `isMovie`，缺电影的应用入口、刷新钩子与缓存键。预计 150-250 行。
3. **元数据 #15 豆瓣深度**（🟡）：补演职人员中文化、季表构建、烂番茄评分、全库应用/单独构建入口。预计 300-500 行，且依赖豆瓣接口稳定性。
4. **元数据 #14 本地 TMDB 缓存/覆盖层**（🟡 的“中”半部分）：在现有 URL 重写之外，增加 cache/override 目录、`tmdb-movies2/tmdb-tv/tmdb-collections` 结构、图片自动刷入、统一分级与特别篇排序。

### 大

5. **元数据 #14 完整“构建本地元数据刮削源”**（🟡 的“大”半部分）：完整实现 Wiki 的四种缓存模式（优先缓存/在线刮削并缓存/仅覆盖缓存/关闭）、被动缓存读写、剧集组与覆盖优先级。预计 800+ 行，属于独立子系统。
6. **分布式全量 ffmpeg（rffmpeg 级）**：`docs/distributed-ffmpeg-evaluation.md` 已评估。只对插件自己发起的 ffmpeg/ffprobe 可行；Emby 自身的播放转码/扫描探测/BIF 生成不可行。**已确定不做**，继续走固定端点路线。

## 四、Emby 侧受限与不做项

| 项 | 状态 | 原因 |
|---|---|---|
| 电影 IntroDb 刮削 | 明确不接 | 当前只把 IntroDb 作为剧集指纹探测前的数据源（`ApplyToSeasonAsync/ApplyToEpisodeAsync`）；电影没有接入点，`isMovie` 仅是 API 层预留 |
| 分布式全量 ffmpeg（rffmpeg） | 待评估，不做 | 插件大量调用走 Emby 内部 `IMediaProbeManager`/`IImageExtractionManager`/`AudioFingerprintManager`，替换 `CustomFfprobePath` 不会改道；全局替换会影响播放转码。详见 `docs/distributed-ffmpeg-evaluation.md` |
| 独占媒体信息提取的“硬拦截” | 已用 Harmony mod 实现，但非公开 API | `Mod/ExclusiveExtract.cs` + `PatchManager`（HarmonyX 2.16.1）；Emby 4.10 无公开拦截点，补丁失败会 fail-closed 并禁用选项（`Plugin.cs:1160-1165`） |

## 五、今晚新合入能力核对

| 能力 | 结论 | 证据 / 说明 |
|---|---|---|
| DV P7 双层原盘检测 | ✅ 已实现并接线 | `Common/DvP7Api.cs`（`DetectAsync/Evaluate` 解析 FFmpeg 9 `-show_stream_groups` 的 BL/EL、PID 0x1011/0x1015、side data）；`DiscMediaInfoApi.cs:398-419` 调用并给 BL 打 Dolby Vision 标记；真盘验收见 `docs/dv-p7-pilot.md` 与提交 `dfcfb9e/8935e8f` |
| 分布式 HDR Vivid 端点 | ✅ | `Web/Api/DistributedHdrVivid.cs` + `Web/Service/DistributedHdrVividService.cs` + `DistributedExtractApi.TryDetectHdrVividAsync/HandleWorkerHdrVividRequestAsync` |
| 分布式 DV P5 章节帧端点 | ✅ | `Web/Api/DistributedDvP5.cs` + `Web/Service/DistributedDvP5Service.cs` + `TryExtractDvP5ChapterImagesAsync`；本地 `DvP5Screenshot` 保留为回退 |
| 豆瓣评分预构建缓存 | ✅（但范围仅评分） | `Common/DoubanCacheApi.cs` + `ScheduledTask/DoubanCachePrebuildTask.cs`（按库/批次/流控/临时封禁停止）；`PrebuildRatingAsync` 只写评分缓存，不构建完整豆瓣元数据 |
| 演员刷新偏好补全（批次 13） | ✅ | `ScheduledTask/RefreshPersonTask.cs` + `MetadataEnhanceOptions` 的 RefreshPersonMode/Scope/SkipLocked/SkipMissingTmdbId/SkipRecent/RequireChinese/MaxConcurrent/Interval |
| Iced/Cecil 的 `.deps` 布局 | 🟡 代码与文档已合入，但部署目标有冲突 | `Plugin.cs:147-207` 的 `AssemblyResolve` 优先读 `<plugins>/<AssemblyName>.deps/` 再回退顶层；`docs/plugin-dependency-layout.md` 完整；`csproj` 有 `DeployPluginDependencies`。**但 `csproj:107-116` 的 `DeployPlugin` 仍把 `$(TargetDir)*.dll` 全部复制到 plugins 顶层**，与文档“依赖不再放顶层”相矛盾；若用该目标部署，扫描期 `GetTypes` 异常可能仍在 |
| v57 剧集跨库合并优化（第二十七批） | ⏸ 未合入主线，不跟进 | 评估提交 `bca93a3` 在 `hermesh/s_pm7f7civ_01m6r0458bqh7g28nyq4cbdbg3`，不在 `c50bf32` 祖先链；结论：`MergeSeriesPreference` 硬编码 `LibraryScope`，剧集跨库合并分支不可达，属死代码，不需要按 Pro 的性能更新跟进 |
| 独占提取 mod（额外） | ✅ | `Mod/ExclusiveExtract.cs`、`Mod/PatchManager.cs`、HarmonyX 2.16.1；`Plugin.IsModSupported` 反映补丁实际可安装性 |
| 空间音频标识（额外） | ✅ | `GetAudioEffectScript`（`UiEnhancementScript.cs:2658`）显示 Dolby Atmos / DTS:X |

## 六、建议后续批次（按性价比排序）

1. **字幕 #23 外挂音轨扫描**：复用已有 `AddExternalTracks(isAudio)` 与 `GetResolvedMediaStreams`，是最小的一步补齐，建议优先。
2. **片头 #21 电影 IntroDb**：`IntroDbApi` 的 `isMovie` 通路已存在，补电影入口即可；同时补一个“电影刷新时查询”的触发点。
3. **元数据 #15 豆瓣深度**：先补演职人员中文化与季表，烂番茄评分可后置；需要评估豆瓣接口限流。
4. **元数据 #14 本地 TMDB**：工作量大，建议单独立项；若短期不做，应在设置页文案中明确“仅镜像重写，不含缓存/覆盖源”。
5. **`.deps` 部署目标修正**：不涉及功能，但会影响第二十六批修复的实际效果，建议在下一次发版前处理。

## 七、限制与未验证项

1. **本报告是代码核对，不是运行时验收。** 除 DV P7 真盘试点、`.deps` 本地扫描复现外，其余结论均来自静态代码阅读。
2. **未在真实多库环境验证的部分**：跨库播放进度/收藏分离（#24）、跨库合并与电影合并上限（#26）、分布式三节点下的故障回落（#13）、ISO 截图（#12）、豆瓣/IntroDb 的真实接口限流与数据质量。
3. **上游 Wiki 只作对照源。** 未反编译 Pro；Pro 的具体实现细节（尤其本地 TMDB 的缓存优先级、豆瓣季表格式）是根据公开 Wiki 描述的推断。
4. **“搜索 #1 自适应数据库分词器”是路线差异，不是缺口。** 我们用插件侧索引而非 Emby DB FTS，因此“自适应分词器”不适用；用户可见的稳定中文搜索已达成。
5. **“字幕 #22”标 ✅ 但有保留。** 标题回退能让 strm 下载成功，但没有像 Pro 那样解析 strm 目标做真实文件 hash，命中率可能低于 Pro。
6. **`.deps` 部署目标冲突需要在下一次部署前确认**：若生产使用 `-p:EmbyPluginPath`，应先修正 `DeployPlugin` 的复制范围，否则第二十六批的修复会被抵消。

## 八、关键文件核对索引

| 主题 | 关键文件 | 备注 |
|---|---|---|
| 中文搜索 | `Common/SearchIndexService.cs`, `Common/SearchTextHelper.cs`, `Web/Service/SearchService.cs`, `Options/SearchOptions.cs` | 插件侧索引 + 原生结果合并 |
| 代理 / 本地发现 | `Common/ProxyApi.cs`, `Common/LocalDiscoveryApi.cs`, `Options/NetworkOptions.cs` | 全局/白名单；`/System/Info/Public` 覆写 |
| ISO / 光盘 | `Common/IsoApi.cs`, `Common/DiscMediaInfoApi.cs`, `Common/IsoMediaSourceProvider.cs` | 直读 ISO9660/UDF、BDMV/VIDEO_TS、章节 |
| 媒体信息持久化 | `Common/MediaInfoApi.cs`, `ScheduledTask/PersistMediaInfoTask.cs`, `Common/LibraryApi.cs` | 音乐专辑/艺术家/内嵌封面 |
| 截图后处理 / DV P5 | `Common/VideoThumbnailApi.cs`, `Common/DvP5Screenshot.cs`, `Common/FfmpegApi.cs` | 16:9/2:3 裁剪、vulkan+libplacebo |
| 黑名单 | `Common/ItemBlacklist.cs`, `Common/MediaInfoBlacklist.cs`, `Common/FingerprintBlacklist.cs`, `Common/VideoThumbnailBlacklist.cs` | 标签 + 关键字 |
| 分布式 | `Common/DistributedExtractApi.cs`, `Web/Api/Distributed*.cs`, `Web/Service/Distributed*Service.cs`, `Options/DistributedExtractOptions.cs` | 五个固定端点 |
| 本地 TMDB / 图片语言 | `Common/MovieDbApi.cs`, `Options/MetadataEnhanceOptions.cs` | 仅 URL 重写；简繁图片语言 |
| 豆瓣 | `Common/DoubanApi.cs`, `Common/DoubanCacheApi.cs`, `ScheduledTask/DoubanCachePrebuildTask.cs`, `Web/Service/DoubanService.cs` | 评分缓存 + 简介/海报补充 |
| 演员刷新 | `ScheduledTask/RefreshPersonTask.cs`, `Options/MetadataEnhanceOptions.cs` | 模式/范围/跳过策略/并发 |
| 片头 / IntroDb | `Options/IntroSkipOptions.cs`, `ScheduledTask/ExtractIntroFingerprintTask.cs`, `Common/IntroDbApi.cs`, `Common/FingerprintApi.cs` | 按库时长、并行、剧集 IntroDb |
| 字幕 | `Common/SubtitleApi.cs`, `ScheduledTask/DownloadSubtitleTask.cs`, `ScheduledTask/ScanExternalSubtitleTask.cs` | OpenSubtitles 下载；外挂音轨缺失 |
| 多版本 | `Common/VersionPlaybackApi.cs`, `Common/VersionFavoriteApi.cs`, `ScheduledTask/MergeMultiVersionTask.cs`, `Options/GeneralOptions.cs` | 跨库进度/收藏、合并上限 |
| 通知 | `Common/NotificationApi.cs`, `Notification/CustomNotifications.cs`, `Options/NotificationOptions.cs` | 三类事件 |
| 体验增强 | `Web/Helper/UiEnhancementScript.cs`, `Web/Helper/ShortcutMenuHelper.cs`, `Web/Service/EpisodeStatsService.cs`, `Web/Service/CollectionService.cs`, `IntroSkip/PlaySessionMonitor.cs` | 前端注入 + 服务端 API |
| 独占提取 | `Mod/ExclusiveExtract.cs`, `Mod/PatchManager.cs`, `Plugin.cs` | HarmonyX 2.16.1，fail-closed |
| DV P7 | `Common/DvP7Api.cs`, `Common/DiscMediaInfoApi.cs` | FFmpeg 9 stream groups |
| 依赖布局 | `Plugin.cs`（AssemblyResolve）, `StrmAssistant.csproj`, `docs/plugin-dependency-layout.md` | `.deps` 子目录；部署目标有冲突 |
