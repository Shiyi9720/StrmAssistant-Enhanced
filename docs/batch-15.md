# batch-15：补齐测试覆盖（DvP7 全链路 / 源退避 / 续播序列）

> 注：本文件「本轮确认的事实」中关于 ffprobe 退出码被丢弃、合并多源条目不换算的两条限制，
> 已在 batch-16 修复，详见 `docs/batch-16.md`。本文件保留当时的实测记录不再改写。

## 目的

batch-14 的 63 条用例只覆盖纯解析与计数器。本轮把三处只有代码审阅、没有自动化的链路补上：`DvP7Api.DetectAsync` 真子进程链路、第三方源退避的 HTTP 驱动、多版本续播的多步序列。

## 工程变化

仍然只在 `tests/StrmAssistant.Verification` 内，不进 `StrmAssistant.sln`，不改插件运行时行为。

- csproj 新增链接三个真实源文件：`CustomFfprobeApi.cs`、`StrmUrlHelper.cs`、`VersionPlaybackApi.cs`（仍是链接，不复制）。
- `src/EmbyShims.cs`：新增链接这些文件所需的最小 Emby 类型（BaseItem/Video/Movie、MediaSourceInfo/MediaStream、ISessionManager 与播放事件、IJsonSerializer、HttpRequestOptions/IHttpClient、ProbeResult 等）。
- `src/FfmpegApiShim.cs`：用 `System.Diagnostics.Process` 实现生产 `FfmpegApi.RunAndCaptureAsync` 的职责（启动、捕获 stdout/stderr、超时/取消时杀进程）。**不是 mock 掉进程调用**：静态 ffprobe 真实执行，输出是真实 stdout/stderr，因此 `RunProbeJsonAsync` 与 `DetectAsync` 走的是生产代码。
- `Program.cs`：`TestRunner` 增加 SKIP 计数；新增三组。

## 覆盖

| 组 | 内容 | 条数 |
|---|---|---|
| E | 注入 `HttpMessageHandler` 驱动真实 `SourceBackoff`：403→429→429 阶梯 10m→30m→2h、退避未到期短路（handler 请求数不增）、200 清零后重新从 10m 起算、key 互不干扰 | 27 |
| D | `DvP7Api.DetectAsync`：真实 ffprobe 正常解析（合成 WAV）、非零退出（不存在的输入）、空输出/非 JSON 输出（受控 wrapper）、FIFO 阻塞触发 500ms 超时、取消 token、`-read_intervals %+#300` packet-sample 兜底（首个探测用 wrapper 合成 stream group，packet 探测用真实 ffprobe 读真实媒体） | 31 |
| F | 真实 `VersionPlaybackApi` 多步序列：跨条目 A 40%→B（按片长换算一次）→A 前进后 B 不变→B 看完→回 A；最近未看完记录优先；已看完记录不参与换算；合并多源条目的现状 | 23 |

合计 **144 passed / 0 failed / 0 skipped**（本机 Shiyi 身份，退出码 0，连续两次一致）。ffprobe 路径优先 `STRMASSISTANT_FFPROBE`，其次宿主插件数据目录；找不到时整组打印 SKIP 且不计失败。

## 本轮确认的事实

- 真实 ffprobe 用 `-print_format json` 时，**即使输入不存在或不可读、退出码非零，也会向 stdout 打印一个空 JSON 对象 `{}`**。因此 `DetectAsync` 对这类失败不是返回 null，而是解析出空文档并安全降级为「非 P7」。生产代码不检查 ffprobe 退出码；真正返回 null 的是「stdout 完全为空」或「输出里没有 JSON 对象」两种情况（用受控 wrapper 覆盖）。
- 多版本续播的换算只在**跨条目**（拆分版本/跨库，A、B 是不同的 BaseItem，按 NameKey 分组）时发生。对**同一个条目挂多个 MediaSource**的合并形态，读取次要版本时 `GetPerVersionPlayback` 的候选条件 `!sources.Any(IsSameVersion)` 会把主版本记录排除掉，次要版本拿不到插件条目，客户端仍回退到 Emby 原生断点。这是 batch-13 文档写明的「不属于当前任何版本」规则的直接结果，但对合并多源场景等于没有换算；真机验收时应重点确认是否需要调整。
- 精确命中的版本记录优先于「已看完状态跨版本共享」：B 看完后回 A，A 仍返回自己未看完的位置；共享 played 只作用于**没有自己记录**的版本（`VersionPlaybackApi` 内注释「every version is reported as played」与实际行为不完全一致，属注释/文档表述问题）。

## 仍未覆盖

- 真实 UHD 碟的双轨 stream group：库里没有样本，D5 的首个探测由 wrapper 合成；packet 数据与分类来自真实 ffprobe。
- 真实豆瓣/IntroDB 403/429：`DoubanApi`/`IntroDbApi` 依赖 Emby `IHttpClient` 与 `ProxyApi`，无法直接链接；E 组按生产调用点的状态码策略在测试侧搭了最小驱动，退避状态机本身是真实源码。
- `SourceBackoff` 没有时钟注入，E 组用反射把到期时间回拨来推进阶梯（仅测试，不改生产）。
- Emby 真实 `IJsonSerializer`（ServiceStack）的落盘格式：F 组用 System.Text.Json shim，已验证记录字段写入磁盘。
- 客户端（Web/手机/TV）真实起播位置与旧 JSON 迁移。

## 附：多版本续播开关的配置位置（2026-11-10 真机实测踩到）

`PersistPerVersionResume` 只在配置的 `ExperienceEnhanceOptions.VersionPlaybackOptions` 下生效；
写到顶层 `VersionPlaybackOptions` 既不报错也不生效（插件读的是
`GetPluginOptions()?.ExperienceEnhanceOptions?.VersionPlaybackOptions?.PersistPerVersionResume`），
此时静默退回旧逻辑——旧逻辑按 `MediaSourceId`/`ItemId` 严格匹配，**不会跨条目换算**，
表现为「同名同年的两条独立条目互不共享进度」。

修正位置后真机实测（《逆行人生》244716 2160p / 244717 1080p，同 name|逆行人生|2024）：

- A 播到 29075456000 ticks（40%）后读 B：返回 29074892000，与按片长比
  `72687230000/72688640000` 换算的理论值**完全一致（差 0 tick）**，并落盘为 B 自己的记录；
- 旧逻辑下同样的读取返回空。

结论：跨条目（同片被 Emby 拆成两条）的换算是靠 per-version 模式实现的，不是缺陷；
启用时必须把开关写进 `ExperienceEnhanceOptions` 分支。
