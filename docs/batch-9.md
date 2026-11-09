# Batch 9 — 缺主图视频自动补封面（真正的图片捕获）（2026-11-10）

hermesh 派发（claude，worktree `s_75cglx6f_01m70h07qk6qawy8zavdxkm15w`）。代码仅
`StrmAssistant/Common/LibraryApi.cs`（+245 行），另加本文档。构建 **0 error / 1 warning**（warning 为
既有 `BaseItem.IsFolder` 过时提示，非本次改动）。

## 背景

`MediaInfoExtractOptions.EnableImageCapture` 此前只完成了「选条目」：`FetchPreExtractTaskItems` 会把
「没有 Primary 且库的 TypeOptions.ImageFetchers 含 `Image Capture` / `Embedded Images`」的视频并进
媒体信息队列。但执行阶段 `OrchestrateMediaInfoProcessAsync` 用清空了 `ImageFetchers` 的
`dummyLibraryOptions` 调 `RefreshSingleItem`，因此**不会取任何图**。Emby 4.10.2 自带的
`Image Capture` 获取器对 `.strm` / shortcut 条目也不产 Primary（实测清空本地 poster 后 FullRefresh 仍无图）。

本次把缺口补上：媒体信息处理完一个 `Video` 后，若它仍然没有 Primary，插件自己用 ffmpeg 从视频里
抽一帧并 `SaveImage(... ImageType.Primary ...)`。生产目标是 Magnet（无番号）与 AV（immortal 番号）两个库，
条目全部为 `.strm` 直链（CMS 9527 → 115）。

## 改动

### `StrmAssistant/Common/LibraryApi.cs`

1. `OrchestrateMediaInfoProcessAsync` 保持原签名，内部改为：
   - 调私有 `OrchestrateMediaInfoProcessCoreAsync`（原方法体原样搬入）；
   - 返回非 `null` 且 `taskItem is Video` 时调用 `TryCapturePrimaryImageAsync(video, ct)`。
   - 因此媒体信息刷新/还原/分布式/光盘任一成功路径之后都会尝试补图；`null`（跳过、黑名单、
     文件不存在、恢复模式跳过等）不补图。
2. 新增 `public Task<bool> TryCapturePrimaryImageAsync(Video item, CancellationToken ct)`，逐项检查：
   - `EnableImageCapture == true`（默认 false 时直接返回，零行为变化）；`PersistMediaInfoMode == Restore`
     时也不补图（与现有队列选择一致，恢复模式不做网络提取）；
   - 重新取一次条目并确认 `!HasImage(ImageType.Primary)`；
   - `ImageCaptureEnabled(item, libraryOptions)`（沿用原有库/TypeOptions 判定）；
   - 不在 `MediaInfoBlacklist`、`VideoThumbnailBlacklist`；
   - 容器不在 `ImageCaptureExcludeMediaContainers`（复用 `ExcludeMediaContainers`，含 Iso），
     非 shortcut 本地文件的扩展名不在 `ExcludeMediaExtensions`（与原 `IsExtractNeeded` 同口径）。
3. 路径解析：
   - `item.IsShortcut` → `GetStrmMountPath(item.Path)`（内部已 `StrmUrlHelper.Normalize`）；
   - 本地文件直接用 `item.Path`，再过一次幂等的 `StrmUrlHelper.Normalize`；
   - 解析异常/空路径只打 **Info** 并返回 false，不 throw。
4. 取帧位置：`ImageCapturePosition`（clamp 到 10–90）× `RunTimeTicks`；没有 `RunTimeTicks` 或 ≤0 时
   退固定 **120 秒**并打 Debug。
5. 抽帧命令（顺序固定）：
   ```
   ffmpeg -hide_banner -loglevel error -ss <秒> -i <输入> -frames:v 1 [-vf "scale=<宽>:-1"] -q:v 3 -update 1 -y <临时文件>
   ```
   `ScreenshotMaxWidth <= 0` 时不加 `-vf`；`<秒>` 用 InvariantCulture 格式化。经
   `FfmpegApi.RunAsync` 执行，超时基值 120s 再乘 `ImageExtractionTimeoutMultiplier`。
6. 临时文件放在 `ApplicationPaths.TempDirectory`，文件名 `strmassistant-image-capture-<guid>.jpg`
   （GUID 命名天然规避条目名里的非法文件名字符）；**无论成功、失败、取消，finally 都删除**。
7. 保存：`IProviderManager.SaveImage(item, libraryOptions, tempPath, ImageType.Primary, null,
   Array.Empty<long>(), directoryService, false, ct)`。成功后 Info（条目名、seek 秒、字节数）。
8. 失败防重试：静态 `ConcurrentDictionary<long, DateTime>`（key `InternalId`）记录失败时间，
   **24 小时内**同一条目直接跳过；成功则清除记录。抽帧/保存失败只打一次 Warn + Debug，随后继续
   处理后续条目，绝不影响整个任务。包装层另有最后兜底 `catch`：任何未预期异常（含上面各项检查
   阶段）也只 Warn + 记录退避，`OrchestrateMediaInfoProcessAsync` 仍返回原媒体信息结果。
9. 未新增任何资源键；未改章节缩略图逻辑；未改公共方法签名（仅新增方法）。

## 行为差异

- `EnableImageCapture = false`（默认）：`TryCapturePrimaryImageAsync` 第一行即返回，无任何额外
  ffmpeg 调用、日志或写盘，行为与 batch 8 完全一致。
- `EnableImageCapture = true`：仅对**处理完成后仍无 Primary**、库开启 `Image Capture`/`Embedded Images`、
  且未被黑名单/容器/扩展名排除的视频补图。已有主图的条目不会被覆盖。
- 抽帧发生在媒体信息刷新之后，此时 `RuntimeTicks` 已是最终值；比例定位更准确。
- 单条失败被隔离并有 24h 退避，批量任务不会因一个坏链反复重试。

## 验证

### 构建

worktree 的 `lib/emby` 只含 README（DLL 被 `.gitignore` 排除），构建时指向主 checkout 的参考 DLL：

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build StrmAssistant.sln \
  -p:EmbyServerDir=/vol3/1000/Projects/StrmAssistant/lib/emby -c Release -v:m
# => Build succeeded. 0 Error(s)
```

### 生产验证建议（部署后）

1. 确认 `EnableImageCapture` 开启、`ImageCapturePosition` 为期望值（默认 10）。
2. 选一个 Magnet 库中**确定没有主图**的 `.strm` 条目（先备份/记录其 `InternalId`），单独触发
   `MediaInfoExtract` 任务或等待 catchup。
3. 日志应出现：
   - `ImageCapture - RuntimeTicks unavailable, using 120s fallback`（若条目还没 runtime）；
   - `ImageCapture - Primary image saved: <名称> - <strm 路径> - seek <秒>s - <字节> bytes`。
4. 详情页确认 Primary 已出现；`TempDirectory` 下不应残留 `strmassistant-image-capture-*.jpg`。
5. 坏链/不可达条目应只出现一次 `ImageCapture - Frame extraction failed` 或
   `ImageCapture - Failed`，后续运行 24h 内跳过。
6. 关闭 `EnableImageCapture` 后重跑，日志中不应出现任何 `ImageCapture -` 行。

## 风险

- **网络与 CPU 开销**：每个无图条目会建立一次 HTTP 读取并解码一帧。生产两库全量首次补齐时会有
  持续的 ffmpeg 进程；已在现有队列并发度内串行执行，且 24h 失败退避可抑制坏链风暴。
- **封面内容**：按百分比定位，极少数视频该位置可能是黑场/台标/片头。可调整 `ImageCapturePosition`；
  已有主图的条目永远不会被覆盖，重跑也不会改已成功的图。
- **`-ss` 在 `-i` 前**为关键帧快速定位，个别容器可能落在稍早的关键帧；对封面用途可接受。
- **24h 退避是进程内状态**：重启 Emby 后失败记录清空，会在下一次任务重试一次；这是预期行为。
- **取消语义**：抽帧期间收到取消会抛 `OperationCanceledException`（与现有任务一致），临时文件仍会被清理。
- **只处理 `Video`**：Audio 的内嵌图仍走原有 `MediaInfoApi` 反序列化路径，未改动。
- **不依赖 Emby Image Capture 获取器**：避免其 strm/shortcut 不支持的问题；只复用 `SaveImage` 落库。

## 回滚

1. 首选：把 `EnableImageCapture` 关掉 —— 新增代码全部短路，等价于未上线。
2. 代码回滚：还原 `StrmAssistant/Common/LibraryApi.cs` 到 batch 8 版本（移除新增字段、
   `TryCapturePrimaryImageAsync` 及包装层，`OrchestrateMediaInfoProcessCoreAsync` 改回原名），
   重新构建部署即可；无数据迁移、无配置迁移、无资源键变更。