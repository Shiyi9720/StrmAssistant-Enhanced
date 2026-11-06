# 分布式全量 ffmpeg（rffmpeg 级）路线定夺

- 日期：2026-11-07
- 目标：Emby 4.10.2 稳定版 `4.10.2.0`
- 结论：**推荐路线 A，先做“固定操作端点 + 失败静默回落本地”的渐进式覆盖；本批不建议路线 B。**
- 本文只做只读评估。第二步只实现“媒体信息提取走 worker”的最小切片，不接管 Emby 播放转码。

## 一、路线 A 与路线 B

### 路线 A（轻）：插件自发的 ffprobe/ffmpeg 固定操作优先下发 worker

**定义**

插件自己发起、能够被识别为固定媒体操作的 ffprobe/ffmpeg 调用，优先交给 worker 执行；worker 只接受类型化参数和固定操作，不接收任意 argv。请求失败、超时、没有在线节点、路径不合法或结果不可用时，调用点静默继续原有本地流程。Emby 自身的播放转码、内置探测和内置 BIF 生成不在本路线范围内。

**能做什么**

| 能力 | 现状 | 路线 A 可达到的状态 |
| --- | --- | --- |
| 媒体信息 ffprobe | 已有 worker 端点，但使用 Emby `IMediaProbeManager`，且没有媒体库根约束 | 增强现有端点：只接受本地文件、校验 worker 媒体库根、静态 ffprobe 返回原始 JSON；master 解析后落库 |
| 章节图 | 已有固定 `Thumbnail` 端点，失败回落 Emby `ThumbnailGenerator` | 保持并补齐统一开关、并发和统计 |
| 片头声纹 | 已有固定 `Fingerprint` 端点，失败回落 Emby `AudioFingerprintManager` | 保持并补齐统一开关、并发和统计 |
| HDR Vivid | 已有第二十五批固定 `HdrVivid` 端点 | 复用现有端点，不再新增同类端点 |
| Dolby Vision P5 章节帧 | 已有第二十五批固定 `DvP5` 端点 | 复用现有端点，不再新增同类端点 |
| DV P7 探测 | 本地静态 ffprobe `-show_stream_groups` | 新增固定 `DvP7` 端点或复用参数严格的 probe 操作；失败回落本地 |
| ISO/BDMV/远程碟片探测 | 本地静态 ffprobe，支持 `bluray:` / http bridge | 单独设计带输入协议校验的固定端点；不与普通媒体信息端点混用 |
| 截图裁剪 | 本地自定义 ffmpeg | 可新增固定 `CropImage` 端点，回传 JPEG |
| 自定义 ffmpeg 自检 | 本地执行 `-filters` / 测试命令 | 是否远程执行价值低，建议保留本地 |
| BIF | 插件只调用 Emby `ThumbnailGenerator`，没有自己的 BIF ffmpeg 命令 | **当前不覆盖**；若要做，需要单独设计 worker 侧 BIF 生成端点，不能靠现有 probe 端点自然覆盖 |

**要改哪些文件**

| 文件 | 改动 |
| --- | --- |
| `StrmAssistant/Web/Api/DistributedMediaInfo.cs` | 媒体信息端点请求收敛为 `ApiKey`、`Path`、`TimeoutSeconds`；响应增加错误码和原始 ffprobe JSON |
| `StrmAssistant/Common/DistributedExtractApi.cs` | 复用 `TryWorkersAsync` / `SendAsync` / 节点轮询；增加媒体库根校验、固定操作专用超时、并发和回落原因 |
| `StrmAssistant/Common/CustomFfprobeApi.cs` | 提供带超时的静态 ffprobe 执行入口；保留 120 秒旧重载，新增 10..600 秒可控重载 |
| `StrmAssistant/Common/LibraryApi.cs` | `OrchestrateMediaInfoProcessAsync` 根据新开关尝试 worker，失败继续 `RefreshSingleItem` |
| `StrmAssistant/Common/VideoThumbnailApi.cs` | 章节图、DV P5、截图裁剪的调用点逐步接入统一策略 |
| `StrmAssistant/Common/FingerprintApi.cs` | 声纹调用点接入统一策略 |
| `StrmAssistant/Common/MediaInfoApi.cs` | HDR Vivid 调用点接入统一策略 |
| `StrmAssistant/Common/DvP7Api.cs` | DV P7 静态探测接入固定 worker 操作 |
| `StrmAssistant/Common/DiscMediaInfoApi.cs` | ISO/BDMV 远程探测另行设计协议和路径校验 |
| `StrmAssistant/Options/DistributedExtractOptions.cs` | 每个操作一个默认关闭的标量开关；新增 1..4 的 worker 并发上限；禁止集合属性 |
| `StrmAssistant/ScheduledTask/ExtractMediaInfoTask.cs` | 任务结束输出本地/worker/回落计数 |
| `StrmAssistant/Properties/Resources*.resx`、`Resources.Designer.cs` | 新增资源键三份 resx 与 Designer 同步 |

**工作量估计**

- 媒体信息切片：约 250–400 行，主要为端点收敛、静态 ffprobe 超时、路径校验、master 解析落库、统计和资源；1 个可独立合入的切片。
- 章节图、声纹、HDR Vivid、DV P5：已有主体实现，补齐开关/并发/统计约 150–250 行；可分 2–3 个切片。
- DV P7、ISO/BDMV、截图裁剪：每个约 150–350 行，涉及新的固定操作 DTO、worker 能力检查和二进制/磁盘格式差异；建议分别切片。
- BIF：不建议纳入路线 A 的第一阶段；需要单独验证 Emby 内部 BIF 生成算法和缓存路径，工作量与风险都高于普通固定操作。

**风险**

- master 与 worker 的媒体库根、挂载路径必须通过现有 `PathMapping` 对齐；路径映射错误会表现为 `path_not_allowed`。
- worker 的静态 ffprobe 下载或可用性可能不同；不可用时必须回落本地，不能把空结果当成“无媒体流”。
- ffprobe 对 ISO/BDMV 需要 `bluray:`/HTTP bridge，和普通文件端点不是同一类输入，不能混在一个宽松端点里。
- 章节图/声纹/DV P5 有较大回传体积，需要并发上限、响应大小和超时保护。
- BIF 由 Emby 内部 `ThumbnailGenerator` 生成，插件没有直接 ffmpeg 调用点，路线 A 不能“顺手”覆盖。

**与现有 worker 端点体系是否冲突**

不冲突。继续使用 `/StrmAssistant/DistributedExtract/*` 固定路由、共享密钥、`IHttpClient` JSON POST、节点轮询和健康退避；不引入“任意 ffmpeg 命令”端点。媒体信息增强的是现有 `MediaInfo` 路由，不应新增重复路由。

**降级路径**

每个调用点都保持现有本地流程为最终路径：master 只有在 worker 返回可用结果并成功落库时才跳过本地；其他情况原样执行本地 Emby/插件逻辑。降级原因至少区分 `no_worker`、`timeout`、`worker_error`、`path_not_allowed`。

### 路线 B（重）：进一步覆盖 Emby 播放转码（TranscodingJob）

**定义**

不仅分发插件自己的 ffprobe/ffmpeg，还要让 Emby 播放转码产生的 `TranscodingJob` 走 worker。需要在 Emby 启动 ffmpeg 的路径上做拦截/替换，通常需要 Harmony 补丁或替换 Emby 的编码器配置。

**能做什么**

- 理论上可以把播放转码进程迁到 worker，降低 master CPU/GPU 压力。
- 对支持共享缓存、相同路径挂载和统一 ffmpeg 参数的部署，可能获得接近 rffmpeg 的体验。

**要改哪些文件**

- 需要新的 `Mod/` Harmony 补丁，拦截 Emby 的 `TranscodingJob` / ffmpeg 启动路径；本批硬性禁止修改 `Mod/` 和 Harmony 文件，因此不能在本批实施。
- 需要接管 ffmpeg 参数、进程生命周期、退出码、stderr、进度回传、HLS 分片/temp 目录和清理。
- 需要处理 `cache/temp` 的共享挂载或路径映射，否则 worker 写出的分片 master 读不到。
- 需要与现有 `Mod/ExclusiveExtract` 的 ffprobe patching、插件自定义 ffmpeg/probe 路径共存，避免双重拦截。

**工作量估计**

- 仅 PoC 就需要 500–1000 行以上，并依赖 Emby 内部实现和 Harmony 版本兼容性。
- 生产可用还需要 HLS/DASH、转码进度、失败重试、节点选择、共享存储、清理和回滚，远高于路线 A。

**风险**

- 直接位于播放热路径，任何拦截失败都可能导致播放失败或 ffmpeg 进程泄漏。
- Emby 的 `TranscodingJob` 不是稳定公开扩展点；Harmony 补丁随 Emby 版本变化风险高。
- worker 必须访问与 master 相同的媒体路径和 temp/cache 路径；没有共享存储时不可行。
- 可能与 `Mod/ExclusiveExtract`、Emby 原生编码器配置和现有 worker 端点互相干扰。
- 回退点不清晰：一旦已经改写 Emby 的 ffmpeg 启动，失败后很难保证无缝回到本地转码。

**与现有 worker 端点体系是否冲突**

高风险冲突。现有体系是“固定操作 + JSON 回传”，不承担进程级流式转码；路线 B 需要新的长连接/分片/进度语义，不能简单复用现有 `SendAsync`。在没有独立部署设计前，不建议把它塞进 `/StrmAssistant/DistributedExtract/*`。

**降级路径**

理论上必须在 Harmony 拦截层检测 worker 不可用并回到 Emby 原生 ffmpeg；但由于启动参数、temp 路径和播放会话状态已经被改写，无法保证和路线 A 一样的“静默、无副作用回落”。这也是路线 B 的主要不可接受风险。

## 二、推荐结论

**推荐路线 A，本批不实施路线 B。**

理由：

1. **收益/风险比明显更好。** 路线 A 只影响插件自己的提取任务，失败可以回到原有本地路径；路线 B 影响播放转码主链路，失败会直接影响用户播放。
2. **现有基础设施已经具备。** `DistributedExtractApi` 已有节点轮询、健康退避、共享密钥、路径映射和固定端点；第二十五批的 HDR Vivid / DV P5 已证明固定操作模式可扩展。
3. **可以按切片合入。** 先把媒体信息做成最小闭环，再逐个增加章节图、声纹、HDR Vivid、DV P5、DV P7；每一步都能独立回滚。
4. **路线 B 与本批硬性约束冲突。** 路线 B 需要 Harmony/`Mod/` 拦截，而本批明确禁止修改这些文件；同时会引入播放回归和进程生命周期风险。
5. **“全量”不等于接管 Emby 所有 ffmpeg。** 先把插件直接控制的媒体操作覆盖完整，已经能覆盖绝大多数追更/计划任务场景；播放转码应作为独立产品决策。

## 三、路线 A 的完整执行点清单

下表按当前代码的实际调用链列出。状态含义：**已有**表示端点/主体已存在；**本切片**表示第二步只覆盖媒体信息；**后续**表示留给后续切片。

| # | 执行点（文件:方法） | 本地实际执行 | 当前状态 | 建议 |
| --- | --- | --- | --- | --- |
| 1 | `Common/LibraryApi.cs:OrchestrateMediaInfoProcessAsync` → `Common/DistributedExtractApi.cs:TryExtractAsync` → `Web/Api/DistributedMediaInfo.cs` | `_providerManager.RefreshSingleItem` | 已有 worker 入口，但未受独立开关控制，worker 侧不是静态 ffprobe/媒体库根校验 | **本切片增强** |
| 2 | `Common/MediaInfoApi.cs:DetectHdrVividAsync` → `DetectHdrVividCoreAsync` → `DistributedExtractApi.TryDetectHdrVividAsync` | `FfmpegApi.RunAndCaptureAsync` 使用本地 ffprobe | 第二十五批已有固定 `HdrVivid` 端点 | 后续统一开关/统计；不新建端点 |
| 3 | `Common/VideoThumbnailApi.cs:RefreshThumbnailImages` → `DistributedExtractApi.TryExtractChapterImagesAsync` | Emby `ThumbnailGenerator.RefreshThumbnailImages` | 已有固定 `Thumbnail` 端点；BIF/已有缩略图集仍走本地 | 后续统一开关/并发/统计 |
| 4 | `Common/VideoThumbnailApi.cs:PostProcessChapterImages` → `ReExtractDolbyVisionProfile5Async` → `DistributedExtractApi.TryExtractDvP5ChapterImagesAsync` | `DvP5Screenshot.ExtractSingleImageAsync` + `FfmpegApi.RunAsync` | 第二十五批已有固定 `DvP5` 端点 | 后续统一开关/统计；不新建端点 |
| 5 | `Common/FingerprintApi.cs:CreateTitleFingerprint` / `TryDistributeSeasonFingerprintsAsync` → `DistributedExtractApi.TryCreateTitleFingerprintAsync` | 反射 Emby `AudioFingerprintManager.CreateTitleFingerprint` | 已有固定 `Fingerprint` 端点 | 后续统一开关/并发/统计 |
| 6 | `Common/VideoThumbnailApi.cs:CropImage` | `FfmpegApi.RunAsync` 执行 `ScreenshotCrop` | 本地 | 后续新增固定 `CropImage` 端点 |
| 7 | `Common/DvP7Api.cs:DetectAsync` | `CustomFfprobeApi.RunProbeJsonAsync` + `-show_stream_groups` | 本地 | 后续新增固定 `DvP7` 端点，返回原始 JSON，master 解析 |
| 8 | `Common/DiscMediaInfoApi.cs:ExtractAsync` → `ResolveRemoteProbeAsync` → `ProbeWithEmbyAsync` / `CustomFfprobeApi.ProbeAsync` | 本地 Emby probe 或静态 ffprobe（含 `bluray:`/http） | 本地 | 后续作为独立碟片切片；不复用普通文件媒体信息端点 |
| 9 | `Common/FfmpegApi.cs:CheckCustomFfmpeg` | 本地 `-filters` / 测试命令 | 本地 | 建议保留本地，远程执行价值低 |
| 10 | Emby `ThumbnailGenerator` 生成 BIF | Emby 内部 ffmpeg | 本地，插件无直接调用点 | 后续单独评估；路线 A 当前不覆盖 |
| 11 | Emby 播放 `TranscodingJob` | Emby 内部 ffmpeg | 本地，路线 B 才涉及 | 本批不覆盖 |

补充说明：

- `FfmpegApi.RunAndCaptureAsync` / `RunAsync` 只是执行器；是否走 worker 由上面的调用点决定，不能直接在执行器里把任意命令转发出去。
- 远程 ISO/BDMV 的 `bluray:`、http bridge 和路径协议与普通本地文件不同，必须单独做输入校验，避免 SSRF 和任意协议读取。
- BIF 没有出现在上表的“插件自有 ffmpeg 调用”列，是因为插件只调用 Emby `ThumbnailGenerator`；当前分布式章节图逻辑也明确绕过了 BIF 模式。

## 四、与第二十五批两个 worker 端点的复用方式

第二十五批已有的 HDR Vivid 和 DV P5 固定端点应作为模板，而不是重新实现一套分发框架：

1. **复用路由和传输层。** 继续使用 `POST /StrmAssistant/DistributedExtract/{Operation}`、共享密钥、`IHttpClient` JSON 请求/响应和 `SendAsync` 的超时/状态码处理。
2. **复用节点调度。** 继续使用 `TryWorkersAsync` 的轮询、总超时、节点失败计数和退避；新操作只提供请求工厂、成功判定、错误提取和应用回调。
3. **复用安全边界。** 继续使用 `SecretEquals`、`ParseProtocol`、`IsAllowedMediaPath`、`GetMediaRoots`、`NormalizeRoot`、`IsPathUnderRoot` 和 `MapPath`。新端点默认只接受本地文件，拒绝 http/smb/strm/iso/img 和目录碟片。
4. **复用结果应用模式。** worker 返回可验证的固定结果；master 负责解析/落库。失败绝不抛出到调用者，调用者继续本地流程。
5. **不新增重复端点。** 媒体信息增强现有 `/MediaInfo`；HDR Vivid 和 DV P5 不改路由；只有真正的新操作（例如 DV P7、CropImage）才新增固定路由。
6. **统一设置和统计。** 新开关、并发上限和统计放在现有分布式分组下；每个操作保持默认关闭，避免升级后行为突变。

## 五、后续切片顺序建议

1. **媒体信息**：本批实现。先解决静态 ffprobe JSON、路径约束、超时、并发、回落原因和统计。
2. **统一分发基础设施**：把并发限制和回落原因从媒体信息抽出为可复用组件，接入章节图、声纹、HDR Vivid、DV P5。
3. **DV P7**：固定端点返回 `-show_streams -show_stream_groups` JSON；worker 只需路径校验和静态 ffprobe。
4. **截图裁剪**：固定 `CropImage` 端点，限制图片大小和格式。
5. **ISO/BDMV 远程探测**：单独设计协议白名单、SSRF 防护、临时文件清理和大小限制。
6. **BIF**：单独评估 Emby 内部生成链路；不建议在没有稳定方案前承诺。
7. **播放转码**：若产品仍要求，另立路线 B 评估，不使用本批的固定操作端点硬套。

## 六、未覆盖项与边界

- 本批不接管 Emby 播放转码、Emby 内置库扫描探测和 Emby 内部 BIF 生成。
- 本批不新增任意命令执行端点，不接受原始 argv。
- 本批不修改 `Mod/`、Harmony、豆瓣、刷新人物或搜索相关代码。
- 本批只实现媒体信息链路；章节图、声纹、HDR Vivid、DV P5、DV P7、ISO/BDMV、截图裁剪和 BIF 均留给后续切片。
- 生产环境的 worker 路径映射、静态 ffprobe 可用性和网络吞吐仍需部署后验证；本文中的性能判断是工程估算，不是实测结果。
