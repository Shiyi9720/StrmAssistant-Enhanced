# 分布式全量 ffmpeg（rffmpeg 级）· 可行性评估（第二十四批）

- 日期：2026-11-06
- 分支：`hermesh/s_pm7f7civ_01m6r0458bqh7g28nyq4cbdbg3`（基于 `port/emby-4.10-stable` 的 `4ef0556`，含第十八、十九、二十批）
- 结论：**对“插件自己发起的 ffmpeg/ffprobe 操作”可行，且有清晰路径；对“Emby 自己的 ffmpeg 操作（播放转码、库扫描内置探测、BIF 生成等）”不可行。**
  本批只做只读评估，不新增、不修改任何功能代码，不动 resx，只提交本文件。
- 与 Pro 的关系：本报告只依据上游公开 Wiki / 公开 Release 资料，**没有反编译 Pro、没有使用 Pro DLL、没有抄 Pro 代码**。

## 一、结论摘要

1. **Pro 的“全量”不是“把 Emby 的 ffmpeg 全部搬走”**，而是把**插件自己调用**的 ffprobe / ffmpeg 可执行文件换成一个本地封装器 `rffmpeg`，由它通过 SSH 把命令送到 worker 节点执行。公开 Wiki 明确写着“该自定义 ffprobe 用于插件追更任务和计划任务；Emby 本身仍使用原生 ffprobe”。
2. 公开 Wiki 的“进阶节点”部分说明：**分布式截图（含 Dolby Vision、ISO）与片头声纹**也走 rffmpeg，但前提是把 Emby 的 `cache/temp` 通过 NFS 以**相同路径**挂进 worker，并且声纹要求 `chromaprint: True` + `rffmpeg backend: True`，DV/ISO 截图还要求设置 `VK_ICD_FILENAMES` 启用 Vulkan CPU 模拟。
3. 我们当前分支已经有一条**固定操作型 HTTP worker 通道**，覆盖三类操作：媒体信息、章节截图、片头声纹。它和 rffmpeg 是两条不同路线：
   - 我们：固定端点 + 类型化请求 + JSON/base64 回传，master 落库；不要求共享存储。
   - Pro：通用可执行文件替换 + SSH + 共享 temp；不要求 worker 装插件，但要求路径一致/可访问。
4. **“全量 rffmpeg”不建议作为我们下一批的主线**。原因不是做不到，而是：
   - 插件里大量调用走的是 Emby 的 `IMediaProbeManager` / `IImageExtractionManager` / `AudioFingerprintManager`，这些内部最终用 Emby 自己的 ffprobe/ffmpeg，**不会因为我们把 `CustomFfprobePath` 指到 rffmpeg 就自动改道**；
   - 要让它们改道，要么重写探测/截图/声纹流程（行为漂移风险大），要么全局替换 Emby 的 encoding 配置（会影响播放转码，不可接受）；
   - 还要额外承担静态 ssh 二进制、NFS 共享 temp、节点路径一致、密钥分发等部署成本。
5. **推荐路径**：在现有固定端点上继续扩展剩余“媒体相关”的固定操作（优先 HDR Vivid 检测、Dolby Vision P5 章节帧），保持“失败静默回落本地”。rffmpeg 兼容层作为可选的第二步，只服务已经自建 rffmpeg 的用户；**不做“任意 ffmpeg 命令下发”端点**。

## 二、评估依据

1. 上游公开 Wiki：`分布式媒体信息提取 (Distributed Extract)`、`视频截图增强 (Image Capture)`（链接见文末）。
2. 上游公开 Release 仓库 `sjtuross/StrmAssistant.Releases` 的 `rffmpeg/` 目录说明与 `static-ffprobe` 目录。
3. 当前分支代码（只读）：
   - `Common/DistributedExtractApi.cs`（1293 行）
   - `Web/Api/DistributedMediaInfo.cs`、`DistributedThumbnail.cs`、`DistributedFingerprint.cs`
   - `Web/Service/Distributed*Service.cs`
   - `Common/LibraryApi.cs`、`MediaInfoApi.cs`、`VideoThumbnailApi.cs`、`FingerprintApi.cs`、`FfmpegApi.cs`、`CustomFfprobeApi.cs`、`DiscMediaInfoApi.cs`、`DvP5Screenshot.cs`
   - `Options/DistributedExtractOptions.cs`、`Options/MediaInfoExtractOptions.cs`
4. 本机验证：只做了代码阅读与公开资料检索。**没有生产 Emby / worker 环境，没有做任何运行时实测**；涉及运行时的事实均标注“未验证”。

## 三、Pro 的“全量”到底包括什么（仅公开资料）

公开 Wiki 把分布式提取分成两个方案：

| 方案 | 机制 | 覆盖范围 | 明确不覆盖 |
|---|---|---|---|
| 中央共享 | 中央服务保存视频/音频哈希 + 媒体信息 JSON；插件提供 `POST /Items/{Id}/SyncMediaInfo`；请求体为空则真实提取，有 JSON 则直接恢复，本地已有媒体信息则优先本地 | 媒体信息（音频内嵌封面可以） | **不做视频截图**；请求/返回的 JSON 会清除外挂字幕与片头片尾信息 |
| 负载均衡（rffmpeg） | 把 `ffprobe` / `ffmpeg` 软链到本地封装器 `rffmpeg`，由它通过 SSH 在远程服务器或 Docker 中执行；支持多节点、按权重分配 | 插件发起的 ffprobe 媒体信息；进阶配置后还包括**视频截图（含 DV、ISO）**与**片头声纹** | Emby 自身的播放转码与内置 ffprobe 仍走原生；**无路径转换** |

rffmpeg 进阶节点的公开要求（原文要点）：

- NFS 共享 Emby 配置目录下的 `cache/temp`，并在 worker 中以**与 Emby 相同的路径**挂载（示例 `/config/cache/temp`），保证 worker 与 Emby 同时可读写；
- `SSH_UID` / `SSH_GID` 与 Emby 运行用户一致；
- 分布式片头声纹需同时满足 `chromaprint: True` 与 `rffmpeg backend: True`；
- DV/ISO 截图需设置 `VK_ICD_FILENAMES` 启用 Vulkan CPU 模拟（x64: `lvp_icd.x86_64.json`，arm64: `lvp_icd.aarch64.json`）；
- 自检日志特征：`Custom ffprobe uses rffmpeg backend: True`、`Custom ffmpeg uses rffmpeg backend: True`、`Custom ffmpeg supports chromaprint: True`。

因此，Pro 的“全量”可以准确表述为：**插件自身发起的 ffprobe / ffmpeg 操作全部可远程执行**，包括媒体信息、截图（含 DV/ISO）、片头声纹；**不包括 Emby 自己的播放转码、库扫描内置探测和 Emby 内部生成的 BIF**。公开 Wiki 没有提到转码分发。

> 说明：以上均来自公开 Wiki 与 Release 仓库说明，没有查看 Pro 程序集；Pro 的内部实现细节（如何注入路径、如何进行节点选择）未验证。

## 四、现状盘点

### 4.1 已经走分布式的操作

| 操作 | master 入口 | 端点 | worker 实现 | 结果如何回传 |
|---|---|---|---|---|
| 媒体信息提取（含章节列表） | `LibraryApi.cs:786` → `DistributedExtractApi.TryExtractAsync`（`:132`） | `POST /StrmAssistant/DistributedExtract/MediaInfo` | `HandleWorkerRequestAsync`（`:410`）调用 `IMediaProbeManager.GetMediaInfo`，过滤外挂字幕后返回 `MediaInfoPayload` | JSON 字符串；master 在 `TryApply`（`:883`）写 `SaveMediaStreams` / `SaveChapters` / `UpdateItems` |
| 章节截图 | `VideoThumbnailApi.cs:110-140` → `TryExtractChapterImagesAsync`（`:170`） | `POST /StrmAssistant/DistributedExtract/Thumbnail` | `HandleWorkerThumbnailRequestAsync`（`:539`）调用 `IImageExtractionManager.ExtractVideoImage` | JPEG 逐张 base64；master 写入 `chapters/*.jpg` 并 `SaveChapters` |
| 片头声纹 | `FingerprintApi.cs:118-122`、批量路径 `:418-436` → `TryCreateTitleFingerprintAsync`（`:233`） | `POST /StrmAssistant/DistributedExtract/Fingerprint` | `HandleWorkerFingerprintRequestAsync`（`:708`）用 `CreateFfMpegRunner("CreateTitleFingerprint")` 跑 chromaprint（16 kHz、raw） | 原始 `.fp` 数据 base64；master 只写 `.fp`，序列检测与片头/片尾标记仍在本地 |

公共约束（决定了哪些操作“接不进来”）：

- **协议白名单**：worker 只接受 `File`（要求文件存在）或 `Http`（Emby 把 http/https 都表示为 `Http`）；见 `DistributedExtractApi.cs:575-585`、`:749-759`。`smb://`、`bluray:` 不在其中。
- **可分发判定**：`iso` / `img`、`Bluray` / `Dvd` 容器、以及目录形态的碟片一律不分发（`IsDistributable`，`:1198-1210`）。
- **超时**：master 总预算默认 120 秒（`DistributedExtractOptions.TimeoutSeconds`），worker 端钳制 10–600 秒（`:587-589`、`:761-763`）。
- **节点健康**：连续失败 2 次后按 30/60/120/240/480 秒退避，上限 600 秒；round-robin 选择节点（`:309`、`:1159-1182`）。
- **鉴权**：`[Unauthenticated]` 端点，请求体带 `ApiKey`，worker 用 `CryptographicOperations.FixedTimeEquals` 恒时比较（`:1267-1276`）。
- **回落**：`TryWorkersAsync` 对传输失败、worker 拒绝、payload 不可用一律返回 `false`/`null`，调用方继续走本地路径（`:293-404`）。

### 4.2 仍然留在本地的 ffmpeg / ffprobe 调用（逐个点名）

| # | 文件:行 | 方法 | 用来做什么 | 为什么目前不走分布式 |
|---|---|---|---|---|
| 1 | `Common/MediaInfoApi.cs:243`、`:281-283` | `DetectHdrVividAsync` | 用 ffprobe 读首个视频帧的 `side_data_list`，识别 CUVA / HDR Vivid / 2094-10 | 走的是 Emby `FfmpegConfiguration.ProbePath`，没有对应 worker 端点；不属于媒体信息 JSON 的一部分 |
| 2 | `Common/CustomFfprobeApi.cs:404-405`（`ProbeAsync`） | 静态 ffprobe（带 bluray 协议） | 探测 ISO / BDMV，入口在 `DiscMediaInfoApi.cs:240`（`bluray:` 前缀）与 `:279`（http 直读） | worker 协议只允许 File/Http，且 `IsDistributable` 明确排除 iso/img；bluray/smb 输入无法下发 |
| 3 | `Common/DiscMediaInfoApi.cs:309` | `_mediaProbeManager.GetMediaInfo` | 碟片结构、MLPS 音轨/字幕语言等本地探测 | 与上一条同属碟片路径，现有 worker 不接收 |
| 4 | `Common/VideoThumbnailApi.cs:161` | Emby `ThumbnailGenerator.RefreshThumbnailImages` | 章节图 / BIF 的本地生成 | 分布式只覆盖“章节图模式”（`ThumbnailImagesIntervalSeconds == -1` 且无本地缩略图集，见 `:104-108`）；BIF 模式、已有本地缩略图集、非可分发条目、worker 失败都会落到这里 |
| 5 | `Common/VideoThumbnailApi.cs:318`；`Common/DvP5Screenshot.cs:137`、`:148` | `DvP5Screenshot.ExtractSingleImageAsync` | 用自定义 ffmpeg（Vulkan + libplacebo）重截 Dolby Vision P5 章节图，失败再退 zscale | 现有 thumbnail 端点固定调用 Emby 的 `IImageExtractionManager`，没有“用 worker 自定义 ffmpeg 截 DV P5”这一档 |
| 6 | `Common/VideoThumbnailApi.cs:383` | `CropImage` | 用 ffmpeg 裁剪超宽/竖版章节图 | 只处理已有图片，不读媒体文件；远程化收益近似为零 |
| 7 | `Common/VideoThumbnailApi.cs:172-275` | `PostProcessChapterImages` | 截图质量/最大宽高（Emby `IImageProcessor`，不是 ffmpeg） | CPU 图像处理，不是 ffmpeg 操作 |
| 8 | `Common/FingerprintApi.cs:129` | Emby `AudioFingerprintManager.CreateTitleFingerprint` | 本地 chromaprint 声纹 | 分布式关闭、条目不可分发、worker 失败时的回落路径 |
| 9 | `Common/FfmpegApi.cs:279`、`:286` | `CheckCustomFfmpeg` | 自检自定义 ffmpeg 是否有 `libplacebo`、能否初始化 Vulkan | 这是“本机能力自检”，必须在本机做；worker 的能力要单独自检 |
| 10 | `Common/CustomFfprobeApi.cs:177` | `ValidateAsync` | `ffprobe -version` 校验 | 同上，本机二进制校验 |
| 11 | `Common/FfmpegApi.cs:143`、`:202` | `RunAndCaptureAsync` / `RunAsync` | 上述所有自定义 ffmpeg/ffprobe 调用的公共执行器 | 本身只是执行器，是否远程取决于调用点 |
| 12 | `ScheduledTask/ExtractMediaInfoTask.cs:100` | 调用 `DetectHdrVividAsync` | 媒体信息任务收尾时检测 HDR Vivid | 同 #1 |
| 13 | `ScheduledTask/ExtractIntroFingerprintTask.cs:162`、`Common/QueueManager.cs:579` | 调用 `FingerprintApi.CreateTitleFingerprint` | 片头声纹任务入口 | 内部已先尝试分布式，失败回落本地 |
| 14 | Emby 自身 | 播放转码、库扫描中的内置 ffprobe、BIF 生成 | 播放/扫描 | 插件无公开拦截点；不属于本插件“自己的 ffmpeg” |

补充：

- `FfmpegApi.WrapFfmpegManager`（`FfmpegApi.cs:27-32`）只是给插件经手的 ffprobe/ffmpeg 命令注入自定义参数，不是独立调用点。
- `Common/ImageExtractionTimeout.cs` 是给 Emby 图片提取加超时的包装器，最终仍调用 Emby 的 ffmpeg。
- 本分支没有转码、字幕转码、OCR 相关的 ffmpeg 调用点（全仓 grep 未发现 `Process.Start` 除 `chmod` 外的用法）。

### 4.3 结论：还差什么才算“全量”

按“插件自己发起的操作”口径，当前未覆盖的是：

1. HDR Vivid 检测（纯 ffprobe 读帧 side data）；
2. Dolby Vision P5 章节帧（自定义 ffmpeg + Vulkan/libplacebo）；
3. ISO / BDMV / 远程 `smb://` 碟片探测（静态 ffprobe + bluray 协议）；
4. BIF 模式下的缩略图生成（Emby 内部，不是插件自己的调用）；
5. 截图后处理（裁剪是 ffmpeg，但只处理图片；缩放是 Emby 图像处理器）。

其中 1、2 适合接进现有固定端点；3 需要新的输入协议与安全约束；4 不在插件可控范围；5 不值得远程化。

## 五、rffmpeg 模式是否适用

### 5.1 两条通道的对比

| 维度 | 现有 HTTP worker 端点 | rffmpeg / SSH |
|---|---|---|
| 下发内容 | 固定操作 + 类型化 JSON（Path/Protocol/Points/Minutes…） | 任意 ffprobe/ffmpeg 命令行 |
| 鉴权 | 共享密钥（请求体，恒时比较） | SSH 密钥 |
| worker 依赖 | 必须运行同版本插件 | 只需 ffmpeg/ffprobe 与 sshd；截图/声纹需共享 temp |
| 路径要求 | 支持 `PathMapping` 前缀映射；File 必须存在，或 Http URL | **无路径转换**，路径必须在节点可直接访问 |
| 结果回传 | JSON / base64 内联，无共享存储 | 输出文件落在 worker；必须共享 temp（Pro 用 NFS）或另行取回 |
| 文件传输 | 不传媒体文件，只传路径/URL | 不传媒体文件 |
| 失败回退 | 调用方直接走本地路径（已实现） | 取决于封装器；公开 Wiki 未说明失败回落 |
| 安全性 | 攻击面固定（见第六节） | 等价于“任意 ffmpeg 命令 + SSH 凭据”，攻击面大得多 |

### 5.2 插件侧的远程执行通道可行性

**HTTP 通道（现有）**：可以扩展为新的固定操作端点，这一点已被现有三类操作验证（代码层面），无需新增依赖。

**SSH 通道（rffmpeg 式）**：

- `netstandard2.1` 的 BCL **没有 SSH 客户端**。可选：
  1. 调用外部 `ssh` 进程（`Process.Start`）。插件已经有 `Process.Start` 的先例（`CustomFfprobeApi.cs:369` 调 `chmod`）；
  2. 引入 SSH.NET 之类的库（新增依赖，需要评估 ILRepack/加载与许可证）。
- **容器里是否自带 `ssh`：未验证。** 能作为参考的公开事实是：Pro 的 Wiki 在 Linux 环境要求“放入静态 SSH 二进制文件；Windows/macOS 不需要”，说明不能假设 Emby 容器内一定有可用的 ssh 客户端。本机没有生产 Emby 容器，无法实测。
- rffmpeg 二进制的来源：公开 Release 仓库 `sjtuross/StrmAssistant.Releases/rffmpeg/{linux_x64,linux_arm64,osx_x64,osx_arm64,win_x64}`，上游项目为 `aleksasiriski/ffmpegof`，静态 ssh 来自 `binary-manu/static-cross-openssh`。我们已有从同一 Release 仓库下载静态 ffprobe 的先例（`CustomFfprobeApi.cs:37-38`、`:248-298`），复用下载机制在工程上可行；但**许可证与再分发**需要单独确认（上游 rffmpeg 为 GPL 系）。

### 5.3 大文件怎么到 worker、结果怎么回来

- **媒体文件不传输**。两条路线都假设 worker 能直接读到同一份媒体：
  - 我们：`PathMapping` 把 master 路径前缀映射成 worker 路径（`DistributedExtractApi.cs:1234-1265`）；
  - Pro：明确“此过程无路径转换功能”，要求两端路径一致或节点可直接访问。
- **结果回传**：
  - 我们：章节图/指纹以 base64 内联返回，不依赖共享目录；代价是响应体随图片数量和分辨率增长（具体体积与耗时**未实测**）。
  - Pro：ffmpeg 直接把文件写到 worker 的 temp，因此必须把 Emby 的 `cache/temp` 以相同路径 NFS 挂进 worker（公开 Wiki 的进阶配置）。
- **对 rffmpeg 式“全量”的含义**：如果只替换可执行文件而不共享 temp，截图/声纹这类“要落文件”的操作会失败或写到错误位置；这不是插件代码能单独解决的部署问题。

### 5.4 rffmpeg 适用性结论

- **技术上可行**，但它是“**替换执行器 + 共享存储**”的架构，不是“新增一个插件功能”。
- 对本插件，rffmpeg 只能自动接管**显式使用 `CustomFfprobePath` / `CustomFfmpegPath`** 的调用（ISO 探测、DV P5、截图裁剪、HDR Vivid 若改走自定义路径）；`IMediaProbeManager`、`IImageExtractionManager`、`AudioFingerprintManager` 仍会用 Emby 自己的二进制。
- 要让 rffmpeg 覆盖媒体信息/章节图/声纹，必须重写这些流程或全局替换 Emby 的 encoding 配置；前者工作量和行为漂移都大，后者会影响播放转码，**不建议**。
- 因此：**rffmpeg 适合作为“可选兼容层”，不适合作为我们的主实现**。

## 六、安全与回退

### 6.1 现有固定端点的攻击面（如实记录）

- 三个 worker 端点都是 `[Unauthenticated]`，只靠请求体里的共享密钥；worker URL 示例是 `http://192.168.1.10:8096`，密钥在明文 HTTP 中传输，**可被局域网嗅探/重放**。
- `Path` 没有媒体根目录白名单：持有密钥的人可以让 worker probe **任意存在的本地文件**，并取回其媒体流信息/章节；thumbnail 端点还能把任意本地视频的帧写成 base64 返回。这不是任意文件读取，但属于信息泄露。
- `Protocol = Http` 允许任意 http/https URL，worker 会去请求，构成**受限于媒体解析的 SSRF**。
- 没有请求时间戳/nonce，没有速率限制、并发上限、节点配额；单次可占用 worker 最长 600 秒。
- master 完全信任 worker 返回值（无签名）；但写入路径由 master 从条目自身推导，worker 不能借返回值指定任意写盘路径。

这些是“固定操作”的攻击面。**任意 ffmpeg 命令下发会把攻击面从“读媒体元数据/截图”升级为“以 Emby 进程身份执行任意命令”**，性质完全不同。

### 6.2 如果要做通用命令下发，至少需要

1. **拒绝原始 argv**：只接受“操作 ID + 类型化参数”（probe / thumbnail / fingerprint / dvp5 / hdrvivid / crop），命令模板由 worker 代码固定。这是最有效的一条，也是本报告推荐的路线。
2. 如果一定要接近 rffmpeg 的通用性：
   - 可执行文件只能取 worker 自己配置的 ffmpeg/ffprobe 路径，**不允许请求方指定**；
   - 不经 shell，逐参数传递；
   - 参数白名单 + 危险过滤器黑名单（`lavfi`、`movie=`、`amovie=`、`subtitles=`、`concat:`、`file:` 等都能读写额外文件）；
   - 输入路径规范化（解析符号链接、拒绝 `..`）并限制在媒体库根目录；
   - 输出只能写进每次请求独立的临时子目录，返回后删除；限制响应大小与并发数；
   - 超时 10–600 秒、每节点并发上限、每密钥速率限制。
3. **传输与密钥**：HTTPS，或至少 HMAC-SHA256（method + path + timestamp + nonce + body）+ 5 分钟时间窗；每节点独立密钥，可轮换；共享密钥只放配置，不进日志。
4. worker 以非特权用户运行，媒体目录只读挂载；审计日志记录操作、路径、耗时、结果。

结论：**共享密钥本身远远不够**。在固定操作模式下，现有“密钥 + 路径/协议约束 + 超时”可以接受（仍需补 HTTPS/限流）；在通用命令模式下，即使加上上述全部措施，风险仍显著高于收益。

### 6.3 回退保证

- 现有设计已经满足“worker 失败必须安静回退本地”：`TryWorkersAsync` 的所有失败分支都返回 `false`，`TryExtractChapterImagesAsync` / `TryCreateTitleFingerprintAsync` 返回 `false`/`null`，调用方继续执行本地逻辑；worker 侧异常被捕获并转成 `Success = false`。
- 新增固定操作时必须沿用同一模式：**先远程、失败即本地，远程异常不改变条目状态**。
- rffmpeg 式的风险在于“回退点不在插件里”：一旦 `CustomFfmpegPath` 指向封装器，封装器失败时插件拿到的只是“ffmpeg 执行失败”。若采用，必须要求封装器在节点全部不可用时**快速非零退出**，并由插件侧保留原始 ffmpeg 路径作为显式回退；公开 Wiki 未说明 rffmpeg 是否自带本地回退，**未验证**。

## 七、实施路径（建议顺序）

### 第一步（推荐，低风险，约 250–350 行）：把剩余“媒体相关”操作接进固定端点

1. **HDR Vivid 检测**
   - 新增 `Web/Api/DistributedHdrVivid.cs`（约 45 行）与 `Web/Service/DistributedHdrVividService.cs`（约 35 行），请求字段：`ApiKey/Path/Protocol/TimeoutSeconds`，响应：`Success/Detected/Error/NodeName/ElapsedMs`。
   - `Common/DistributedExtractApi.cs` 增加 `TryDetectHdrVividAsync`（master，约 80 行）与 `HandleWorkerHdrVividRequestAsync`（worker，约 70 行，复用 `FfmpegApi.RunAndCaptureAsync` + 现有 CUVA/HDR Vivid/2094-10 判定）。
   - `Common/MediaInfoApi.cs:243` 在本地探测前先尝试分布式；失败继续原有逻辑。
   - `Options/DistributedExtractOptions.cs` 增加 `EnableDistributedHdrVivid`（默认 false）+ 3 份 resx + Designer（新增约 12 条文案）。
2. **Dolby Vision P5 章节帧**
   - 在 `DistributedThumbnailRequest` 增加 `Profile` 字段（`"default"` / `"dvp5"`），worker 在 `dvp5` 分支调用 `DvP5Screenshot` 的等价逻辑（把现有私有方法抽成可复用，约 20–40 行）。
   - master 在 `TryExtractChapterImagesAsync` 检测 `ExtendedVideoSubTypes.DoviProfile50` 时带上 `Profile = "dvp5"`（约 10 行）。
   - worker 没配自定义 ffmpeg / 不支持 Vulkan 时返回 `Success = false`，master 回落本地（无需新开关）。
3. **不建议同批做**：ISO/BDMV 远程探测。它需要新的输入协议（`smb://`、`bluray:`）、worker 侧静态 ffprobe、以及对 SSRF 的额外约束，收益集中在少数碟片用户。

要点：只扩展现有端点语义，不引入“任意命令”；所有新操作沿用“失败返回 false → 本地执行”。

### 第二步（可选，约 250–400 行 + 部署文档）：rffmpeg 兼容层

- 新增 `Common/RffmpegApi.cs`：定位/下载 rffmpeg 与静态 ssh、生成 `rffmpeg.yaml`、`status` / `add -w` 节点管理、自检（`Custom ffprobe/ffmpeg uses rffmpeg backend`、`chromaprint`）、把 `CustomFfprobePath` / `CustomFfmpegPath` 指向封装器；保留原始路径用于回退。
- 设置页增加 `UseRffmpegBackend`、rffmpeg 路径、SSH 私钥路径、节点列表；3 份 resx。
- 文档必须写明：节点需能直接访问媒体路径（无路径转换）；截图/声纹需要 NFS 共享 `cache/temp` 且路径一致；DV/ISO 需 `VK_ICD_FILENAMES`。
- 仅覆盖显式走自定义路径的调用；不承诺接管 Emby 的 `IMediaProbeManager` / `IImageExtractionManager` / `AudioFingerprintManager`。

### 第三步（不建议实施）：通用 ffmpeg 命令端点

- 若未来确有需求，必须默认关闭、显式开启，并满足 6.2 的全部约束；实现量约 400–600 行，且需要持续的安全维护。
- 在当前“固定操作已经覆盖绝大多数媒体场景”的前提下，收益不足以抵消“以 Emby 进程身份执行任意命令”的风险。

### 替代方案（如果不做全量）

- 保持现有三端点，只补第一步的 HDR Vivid 与 DV P5。
- ISO/BDMV 与 BIF 继续本地执行；这两类本来就是少数场景，且 BIF 由 Emby 内部生成，插件无法接管。
- 如果目标是“减少重复提取/共享结果”，应单独评估 Pro 的“中央共享”方案（`SyncMediaInfo` 式媒体信息 JSON 共享），它与 ffmpeg 分发是两条独立的产品线。

## 八、未验证项（不得当作结论）

1. 生产 Emby 容器内是否存在可用的 `ssh` 客户端、是否可执行外部二进制：**未验证**。
2. 本插件在 Emby 进程内调用 rffmpeg 封装器的实际行为（权限、UID/GID、SELinux/AppArmor、容器内 `/config/bin` 挂载）：**未验证**。
3. rffmpeg 在节点全部不可用时是否会自动回退本地 ffmpeg：公开 Wiki 未说明，**未验证**。
4. 章节图 base64 回传的实际体积与耗时、worker 并发下的吞吐：**未做性能实测**。
5. Pro 内部如何选择节点、如何注入可执行文件路径：未查看 Pro 程序集，**未验证**。
6. 生产环境现有分布式三端点的实际使用情况与失败率：无生产访问权限，**未验证**。

## 九、本批验证

- 只读评估：未修改任何功能代码、设置项、resx 或工程文件；仅新增本文件。
- 代码事实均来自当前分支源码（行号见正文）。
- 公开资料均来自上游 Wiki / Release 仓库（链接见下）。
- 未执行 `dotnet build`（本批无代码改动）；本地环境无生产 Emby/worker，未做运行时验证。

## 十、参考资料

- StrmAssistant Wiki：分布式媒体信息提取（Distributed Extract）
  https://github.com/sjtuross/StrmAssistant/wiki/%E5%88%86%E5%B8%83%E5%BC%8F%E5%AA%92%E4%BD%93%E4%BF%A1%E6%81%AF%E6%8F%90%E5%8F%96-(Distributed-Extract)
- StrmAssistant Wiki：视频截图增强（Image Capture）
  https://github.com/sjtuross/StrmAssistant/wiki/%E8%A7%86%E9%A2%91%E6%88%AA%E5%9B%BE%E5%A2%9E%E5%BC%BA-(Image-Capture)
- StrmAssistant.Releases：rffmpeg 目录
  https://github.com/sjtuross/StrmAssistant.Releases/tree/main/rffmpeg
- 上游 rffmpeg 项目（ffmpegof）
  https://github.com/aleksasiriski/ffmpegof
