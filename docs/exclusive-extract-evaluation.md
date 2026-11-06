# 独占媒体信息提取 · 可行性评估（第十七批）

- 日期：2026-11-06
- 分支：`hermesh/s_pm7f7civ_01m6qpcp3av5ma1qhpnnjgfxym`
- 结论：**在不依赖 mod 的前提下做不到 Wiki 定义的“独占”**，本批不新增功能代码，只提交本评估报告。

## 一、结论摘要

Wiki 对“独占媒体信息提取”的定义是：**接管全部媒体库的媒体信息提取（ffprobe）与截图（ffmpeg），只允许本插件执行提取，默认关闭**（对应 resx 文案
`ModOptions_ExclusiveExtract_Only_allow_this_plugin_to_extract_media_info__ffprobe__and_capture_image__ffmpeg___Default_is_OFF_`）。

要做到这一点，插件必须能在 Emby 决定“是否探测 / 是否截图”的那一刻**阻止内置流程**，并能接管写入。Emby 4.10 的公开插件 API 没有提供这样的拦截点：

- 没有注册/替换 `IMediaProbeManager` 的 API；
- 没有可取消的“刷新前 / 探测前”事件；
- `ILibraryManager.ItemAdded`、`IProviderManager.RefreshCompleted` 都发生在 Emby 探测完成之后，只能用于“事后覆盖”；
- 决定探测与截图的核心逻辑位于 `Emby.Providers` / `Emby.Server.MediaEncoding` 的内部类型中，插件编译期引用不到。

插件事后重新探测并覆盖结果在技术上可行（插件确实能调用 `IMediaProbeManager.GetMediaInfo`、`IImageExtractionManager.ExtractVideoImage` 并写库），但那是“同一份工作做两遍”，Emby 自己的 ffprobe / ffmpeg 仍然会执行，不满足“只允许本插件提取”。因此按本批要求，不做硬实现。

## 二、评估依据

1. 反编译的 Emby 4.10 程序集：`MediaBrowser.Controller`、`MediaBrowser.Model`、`MediaBrowser.Common`、`Emby.Server.Implementations`。
2. 本仓库历史提交 `00c8651` 中的上游实现：`StrmAssistant/Mod/ExclusiveExtract.cs`（以及 `Mod/PatchManager.cs`）。
3. 当前分支现状：`Plugin.cs:585` `IsModSupported => false`；`StrmAssistant/Mod` 目录不存在；`StrmAssistant.csproj` 没有 `Lib.Harmony` 依赖。

## 三、候选途径逐项结论

| # | 候选途径 | 能否满足 Wiki“独占” | 核心卡点 |
|---|---|---|---|
| 1 | 注册 / 替换 `IMediaProbeManager` | 否 | 插件 API 只有解析，没有注册；实例在插件加载前已创建并被其他服务持有 |
| 2 | 通过 `[Export]` 扩展点抢先或替换 | 否 | 扩展点只做追加，不能移除或覆盖内置 provider |
| 3 | `ItemAdded` / `ItemUpdated` / `RefreshCompleted` 后覆盖 | 否（只能覆盖结果） | 事件在 Emby 探测之后；Emby 仍会跑 ffprobe/ffmpeg，开销翻倍 |
| 4 | 预写 `MediaStreams` 让 Emby 跳过探测 | 否 | 探测与否由 `Emby.Providers` 内部决定，无公开开关；扫描顺序上 `ItemAdded` 晚于刷新 |
| 5 | 把 ffprobe/ffmpeg 换成包装程序 | 理论可行，实际不可接受 | 只读配置、全局副作用、需模拟 ffprobe JSON/ffmpeg 输出，无法可靠静默回落 |
| 6 | Harmony 补丁（上游做法） | 能，但前提是 mod | 补丁对象全是内部非公开类型；本分支明确不支持 mod |
| 7 | 其他公开扩展点（post-scan、scheduled task、IMediaSourceProvider 等） | 否 | 只能做事后任务/动态媒体源/通知，无法拦截库刷新中的探测与截图 |

### 1. 注册或替换 `IMediaProbeManager` —— 不能

依据：

- 插件可用的宿主接口只提供“解析/取导出”，没有注册入口：
  `MediaBrowser.Common/IApplicationHost.cs:43,49,51,59` 只有 `GetExports<T>`、`Resolve<T>`、`TryResolve<T>`、`CreateInstance(Type)`。
- 真正的注册方法 `RegisterSingleInstance` 和容器字段 `Container` 在 `ApplicationHost` 上都是 `protected`：
  `Emby.Server.Implementations` 反编译 22066（`protected readonly Container Container`）、22519-22521（`protected void RegisterSingleInstance<T>`）。
- `MediaProbeManager` 在启动早期由 `RegisterResources()` 构造并注册：
  `Emby.Server.Implementations` 反编译 23070-23071；随后以实例参数传给 `MediaSourceManager`（23072）和 `SyncManager`（23082）。
- 插件加载发生在更晚的 `FindParts()`：`Init()` 先 `RegisterResources()`（22856），然后 `FindParts()`（22865）；插件在 `FindParts` 内才被加载（23403-23405）。
- `IMediaProbeManager` 只有一个方法（`MediaBrowser.Controller` 反编译 5407-5410），内置消费方由容器在 `ProviderManager.AddParts(...)`（23411）时构造，之后也无法通过公开 API 替换；SimpleInjector 容器此时已在使用中。
- 插件构造函数确实能拿到 `IMediaProbeManager`（`Plugin.cs:179`），但这是“能调用”，不是“能替换”。

结论：无 mod 时无法替换探测管理器。

### 2. 通过 `[Export]` 扩展点抢先或替换 —— 不能

依据：

- `FindParts()` 把插件导出追加到固定清单：`LibraryManager.AddParts`、`ProviderManager.AddParts`、`MediaSourceManager.AddParts` 等（23410-23421）。
- `IMediaProbeManager` 不在任何 `AddParts` 清单中。
- `ProviderManager.AddParts` 接收的是 `IMetadataProvider` / `IImageProvider` / `IMetadataService` / `IMetadataSaver` / `IExternalId`，语义是“追加 provider”；没有移除内置 provider 的 API，也没有“同名覆盖”的公开契约。
- 即使插件导出一个名为 `ffprobe` 的 provider，Emby 内置的 `Emby.Providers.MediaInfo.FFProbeProvider` 仍会被执行。

结论：扩展点只能增加功能，不能阻止内置探测。

### 3. `ItemAdded` / `ItemUpdated` / `RefreshCompleted` 后覆盖 —— 只能覆盖结果，不是独占

依据：

- 事件确实存在：`ILibraryManager.ItemAdded/ItemAdding/ItemUpdated`（`MediaBrowser.Controller` 反编译 7105-7111）、`IProviderManager.RefreshStarted/RefreshCompleted/RefreshProgress`（2641-2645）。插件也已经订阅（`Plugin.cs:278-281`）。
- 插件具备重新探测与写库的能力：
  - `IMediaProbeManager.GetMediaInfo(MediaInfoRequest, CancellationToken)`（Controller 5407-5410），已有使用：`StrmAssistant/Common/DistributedExtractApi.cs:463,607`；
  - `IImageExtractionManager.ExtractVideoImage(...)`（Controller 5328-5344），已有使用：`DistributedExtractApi.cs:642`；
  - 持久化：`IItemRepository.SaveMediaStreams` / `SaveChapters`，以及 `ILibraryManager.UpdateItems`，已有先例：`DistributedExtractApi.cs:916-944`。
- 但这些事件只是通知，没有可取消或可阻止的参数；并且在库扫描路径上，顺序是**先刷新/探测，再触发 ItemAdded**：
  `MediaBrowser.Controller` 反编译 12066 —— `child.RefreshMetadata(...)` 之后才 `LibraryManager.TriggerItemAdded(child, ...)`。
- 因此“刷新完成后覆盖”会让 Emby 和插件各跑一遍 ffprobe/ffmpeg：不是“只允许本插件提取”，而是双倍开销，且两边写库存在竞争；上游的 `IgnoreFileChange`、`CatchAllBlock`、`CatchAllAllow`、`ExtractOnFileChange`、`NoIntroProtect`、`NoPersistIntegration` 等控制项也无法实现。
- “失败时静默回落”在这种方案里没有实际意义：失败时保留的是 Emby 已经写好的结果，而不是“回落”。

结论：可作为独立的“插件重探测/权威覆盖”功能，但不能称为独占。

### 4. 预写 `MediaStreams` 让 Emby 跳过探测 —— 不能保证

依据：

- 扫描流程中 `ItemAdding` 在 `CreateItems` 保存之后、`RefreshMetadataRecursive` 之前触发（`Emby.Server.Implementations` 反编译 42283-42304，`MediaBrowser.Controller` 12066），理论上可以在这里抢跑；
- 但“已有 MediaStreams 时是否还探测”由 `Emby.Providers` 内部的 `FFProbeProvider` / `ProviderManager.CanRefresh` 决定，这些是内部非公开实现，不在插件可引用的公开契约内；
- 在 `ItemAdding` 里同步执行 ffprobe 会把整个库扫描串行化，而且仍不能保证内置 provider 不再探测，风险大于收益。

结论：不可靠，不采用。

### 5. 把 ffprobe / ffmpeg 换成包装程序 —— 理论可行，实际不可接受

依据：

- `IFfmpegConfiguration` 只暴露只读路径：`EncoderPath`、`ProbePath`、`DetectPath`（`MediaBrowser.Controller` 反编译 5301-5307），没有公开的 setter；
- 要接管只能改服务器 encoding 配置或替换 system 目录下的二进制，会同时影响转码、截图、片头指纹等所有 ffmpeg 调用；
- 包装程序必须完整模拟 ffprobe 的 JSON 输出和 ffmpeg 的图片输出，还要通过 IPC 回连插件；平台相关、Emby 版本敏感，失败面远大于收益，也无法可靠做到“静默回落”。

结论：不实现。

### 6. Harmony 补丁（上游做法）—— 能，但前提是 mod

依据：本仓库历史提交 `00c8651` 中的 `StrmAssistant/Mod/ExclusiveExtract.cs` / `Mod/PatchManager.cs` 使用 `Lib.Harmony 2.3.6`，补丁对象全部是非公开成员：

- `Emby.Providers.Manager.ProviderManager.CanRefresh`（静态与实例两个重载）
- `Emby.Providers.Manager.ItemImageProvider.ClearImages`
- `ProviderManager.IsSaverEnabledForItem`
- `BaseItem.AfterMetadataRefresh`
- `Emby.Providers.MediaInfo.FFProbeProvider.IsProbingAllowed`
- `Emby.Server.MediaEncoding.Probing.MediaProbeManager.RunFfProcess`
- `Emby.Server.MediaEncoding.Encoder.EncodingHelpers.GetInputArgument`
- `Emby.Server.MediaEncoding.Probing.ProbeResultNormalizer.GetMediaInfo`
- `Emby.Server.Implementations.Data.SqliteItemRepository.SaveChapters / DeleteChapters`
- `Emby.Api.Library.LibraryStructureService.Post / Any`（增加/移除库、媒体路径）
- `Emby.Api.ItemRefreshService.GetRefreshOptions`

这些类型都不在插件可编译引用的程序集里，只能靠运行时反射 + Harmony 改写 IL。当前分支 `Plugin.cs:585` 明确 `IsModSupported => false`，`StrmAssistant/Mod` 不存在，csproj 也没有 Harmony 依赖——这正是本批“不依赖 mod”前提下的硬卡点。

### 7. 其他公开扩展点 —— 不能

- `ILibraryPostScanTask`：扫描结束后运行，太晚；
- `IScheduledTask`：只能周期/手动触发，无法拦截库刷新；
- `IMediaSourceProvider`：只影响播放时的动态媒体源，不参与库内媒体信息提取；
- `ILibraryMonitor`：只做文件系统监控与事件分发，不控制探测；
- `IWebSocketListener` / `IServerEntryPoint` / 自定义 HTTP 服务：只做通知或旁路，无法拦截内置流程。

## 四、卡点总结

1. Emby 4.10 没有提供 `IMediaProbeManager` 的注册/替换 API，也没有可取消的“刷新前/探测前”事件。
2. 决定是否探测/截图的核心逻辑在 `Emby.Providers`（`FFProbeProvider`、`ProviderManager.CanRefresh`、`ItemImageProvider`）与 `Emby.Server.MediaEncoding`（`MediaProbeManager`、`EncodingHelpers`、`ProbeResultNormalizer`）的内部实现中，插件引用不到。
3. 库扫描路径保证 `ItemAdded` 在刷新之后触发，事件无法抢在探测前。
4. 插件能做到的只有“调用同一套探测/截图能力再写一遍”，属于事后覆盖；既不能阻止 Emby 的提取，也无法实现上游的阻止/放行控制项。

## 五、后续可选方案（本批未实施）

- **A. 允许 mod**：恢复上游的 Harmony 实现（需恢复 `Lib.Harmony` 依赖与 `Mod` 框架），这是唯一能按 Wiki 定义工作的方案。
- **B. 坚持无 mod**：另立一个明确命名的非独占功能，例如“插件重探测 / 权威覆盖”（默认关闭、按库/类型、失败保留 Emby 结果）。它不阻止 Emby 提取，会带来双倍探测开销，不应命名为“独占”。
- **C. 向 Emby 请求扩展点**：例如可注册的 `IMediaProbeManager` 或可取消的刷新前钩子；在此之前无 mod 方案无法成立。

## 六、验证

- 本批未改动任何功能代码、设置项或 resx；仅新增本评估文件。
- 评估覆盖：公开插件 API（`IApplicationHost`、`ILibraryManager`、`IProviderManager`、`IMediaProbeManager`、`IImageExtractionManager`、`IFfmpegConfiguration`）、服务注册顺序、库扫描顺序、上游 mod 实现。
- `dotnet build -c Release`：Build succeeded，0 Error（仅既有 1 条 `LibraryApi.cs:1108` 的 CS0618 警告，与本批无关）。
