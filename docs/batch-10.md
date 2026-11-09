# Batch 10 — 新入库条目自动抽取媒体信息/封面（2026-11-10）

hermesh 派发（claude，session `s_75cglx6f_01m70jfqdrvttew8c899nqptyd`）。全部实现位于插件内部，
未引入任何外部脚本、cron 或 systemd 单元。构建 **0 error / 1 warning**（warning 为既有
`LibraryApi.cs` 的 `BaseItem.IsFolder` CS0618 噪音，非本次新增）。

## 背景

batch 9 已让 `EnableImageCapture` 真正能从视频抽帧写入 `ImageType.Primary`，但
`ScheduledTask/ExtractMediaInfoTask.GetDefaultTriggers()` 返回空数组，新片入库后没有任何东西触发
该任务，因此不会自动出封面。本批补上两条自动化路径：

1. 默认触发器：选项开启时每 6 小时跑一次 `MediaInfoExtractTask`。
2. 入库即入队：监听 `ILibraryManager.ItemAdded`，延迟 15 秒等扫描落盘后把符合条件的条目放进
   `QueueManager.MediaInfoExtractItemQueue`，并由一个懒启动的媒体信息队列 worker 处理。

### 前置说明：batch 9 代码不在本 worktree 的 HEAD 中

本 worktree 基于 `02d2c2c`，该提交只包含 `docs/batch-9.md`；batch 9 的
`StrmAssistant/Common/LibraryApi.cs`（+272 行，抽帧补封面）只存在于主 checkout 的未提交改动，
与 sibling 分支提交 `d1a19b3` **逐字节一致**。为了让本批能够复用 batch 9 的抽帧逻辑与排除判断，
开工前把该文件按 `d1a19b3` 原文导入本 worktree（未提交）。因此 `git diff --stat` 中的
`LibraryApi.cs +272` 属于 batch 9 前置导入，不是 batch 10 的改动；batch 10 对它的唯一修改是把
`IsImageCaptureExcluded` 的可见性从 `private` 放宽为 `internal`，以便 `Plugin` 复用。

## 改动文件与行为

### `Options/MediaInfoExtractOptions.cs`

新增选项（默认关，零行为变化）：

```csharp
public bool AutoExtractOnItemAdded { get; set; } = false;
```

资源键 `MediaInfoExtractOptions_AutoExtractOnItemAdded` /
`MediaInfoExtractOptions_AutoExtractOnItemAdded_Description`，`zh` / `zh-hant` / `en` 三语 resx 与
`Resources.Designer.cs` 均已补齐。该选项同时控制默认触发器与 ItemAdded 入队两条路径。

### `ScheduledTask/ExtractMediaInfoTask.cs`

```csharp
public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
```

- 选项关闭：`Array.Empty<TaskTriggerInfo>()`，与改动前完全一致。
- 选项开启：返回一个 `TaskTriggerInfo { Type = TriggerInterval, IntervalTicks = 6h.Ticks }`。

注意 Emby 只在**首次注册任务**时读取默认触发器；选项在插件加载后才打开时，需要到计划任务设置里
手工确认/添加触发器，或重启一次让插件重新注册。

### `Common/QueueManager.cs`

新增（并抽出原 `Initialize` 中的 MediaInfo 启动块）：

```csharp
public static void EnsureMediaInfoProcessTaskStarted()
```

- 只启动 `MediaInfo_ProcessItemQueueAsync`，不触碰指纹/集刷新队列，也**不清空队列**（调用方先入队
  再请求启动）。
- 用 `MediaInfoStartLock` 防止并发 `ItemAdded` 事件启动两个 worker。
- `Initialize()` 保持原语义：仍在启动前清空两个媒体信息队列，然后复用该新方法。

### `Plugin.cs`

新增字段 `_currentAutoExtractOnItemAdded`，构造时从选项读取；新增：

```csharp
private async System.Threading.Tasks.Task AutoExtractOnItemAddedAsync(BaseItem addedItem)
```

`OnItemAdded` 末尾 `await` 该方法，并整体包在 try/catch 中，失败只 `Warn`，绝不向 Emby 扫描外抛。
流程：

1. 选项关 / 非 `Movie` / `Episode` / `Video` → 立即返回（选项关闭时连 15 秒延迟都不会发生）。
2. `await Task.Delay(15s)`。
3. 重新读取选项，`_libraryManager.GetItemById(itemId)` 重新取条目；取不到静默返回。
4. 再次校验类型与非文件夹、`LibraryApi.IsLibraryInScope(item)`（复用现有 scope 判断）。
5. 需要抽取：`!LibraryApi.HasMediaInfo(item)` 或
   `EnableImageCapture && !item.HasImage(ImageType.Primary)`。
6. `hasMediaInfo` 为真、仅因封面入队时，才应用 `LibraryApi.IsImageCaptureExcluded`（batch 9 的
   容器/扩展名排除，含 ISO）。缺媒体信息的条目即使容器不能抽帧也照常抽取媒体信息。
7. 不在 `MediaInfoBlacklist`。
8. 同一条目已存在于 `MediaInfoExtractItemQueue` 或 `FavoriteMediaInfoExtractItemQueue` 时跳过。
9. 入队、`EnsureMediaInfoProcessTaskStarted()`，打 Info：
   `AutoExtract - Queued: <name> - <path>`。

`OnOptionsSaved` 中同时跟踪两个开关：

- `CatchupMode` 开 → 按原逻辑 `Initialize()`。
- `CatchupMode` 关、`AutoExtractOnItemAdded` 开 → 只 `EnsureMediaInfoProcessTaskStarted()`。
- 两者都关且状态有变化 → `Dispose()`，回到改动前的停止状态。

选项中新增日志 `AutoExtractOnItemAdded is set to {0}`。

## 行为差异

- 默认（选项关）：`OnItemAdded` 只多一次选项读取后立即返回；`GetDefaultTriggers` 仍为空；队列
  worker 的启动条件与原来一致。与 batch 9 部署态完全一致。
- 选项开启、`CatchupMode` 关：首次收到新条目后懒启动媒体信息 worker，不受 Catchup 的其他队列影响；
  新条目在 15 秒后按上述规则入队，日志出现 `AutoExtract - Queued`。
- 选项开启、`CatchupMode` 开：原有 catchup 入队逻辑保留，新逻辑负责补齐「有媒体信息但缺主图」的
  条目；两处入队由队列去重避免重复。
- 计划任务：选项开启时获得 6 小时间隔触发器；关闭时无触发器。

## 验证方法

### 构建

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build StrmAssistant.sln \
  -p:EmbyServerDir=/vol3/1000/Projects/StrmAssistant/lib/emby -c Release -v:m
# => Build succeeded. 0 Error(s), 1 Warning(s)（既有 LibraryApi.cs CS0618）
```

### 生产验证建议（部署后）

1. 在插件设置里打开「新增条目自动抽取媒体信息/封面」，按需打开 `EnableImageCapture`；重启一次让
   `MediaInfoExtractTask` 以新默认触发器注册。
2. 往 scope 内的库投放一个**无主图**的 `.strm`（Movie/Episode/Video），等待约 15 秒 + 队列最短间隔
   （默认 30 秒）。
3. 日志应依次出现：
   - `AutoExtract - Queued: <名称> - <strm 路径>`
   - `MediaInfoExtract - Clear Item Queue Started`
   - （`EnableImageCapture` 开启时）`ImageCapture - Primary image saved: ...`
4. 详情页确认 Primary 出现；已存在主图的条目不重复抽帧。
5. 计划任务页确认 `Extract MediaInfo` 出现 6 小时间隔触发器（若部署前选项已开启）。
6. 关闭选项后重复投放：不得出现 `AutoExtract -` 日志，worker 不因本功能启动。

## 风险

- **重复事件**：库扫描期间同一 `InternalId` 可能触发多次 `ItemAdded`，15 秒延迟后由队列去重兜底；
  极端情况下仍可能多入队一次，worker 会再次判断 `HasMediaInfo`/主图，不会重复写同一封面。
- **延迟任务数量**：批量入库时每条 `ItemAdded` 各挂一个 15 秒延迟任务，只有入队阶段占用内存，不阻塞
  扫描线程。
- **CatchupTaskScope 过滤**：worker 复用 `FetchExtractQueueItems`，若用户把 `CatchupTaskScope` 里的
  MediaInfo 去掉（`CatchupMode` 关闭时该列表在 UI 中不可见，通常保持默认），自动入队的条目会被该
  过滤器丢弃。默认配置（`MediaInfo`）不受影响。
- **库 Image Capture 开关**：`FetchExtractQueueItems` 的 `IsExtractNeeded` 仍要求库的
  TypeOptions 开启 `Image Capture`/`Embedded Images`；未开启时仅补齐媒体信息，不补封面（与 batch 9
  一致）。
- **默认触发器只注册一次**：Emby 的限制，见上文。
- **Dispose/Ensure 竞争**：从「Catchup 开」切到「全关」再立刻切回「自动抽取开」时，旧 worker 的取消
  回调可能尚未把任务字段置空，极端时序下新 worker 要等下一次入队才启动；不影响最终结果。
- **batch 9 前置导入**：本 worktree 的 `LibraryApi.cs` 是导入的未提交 batch 9 代码，合并/部署时需确保
  与生产主 checkout 的版本一致，避免覆盖。

## 回滚

1. 首选：关闭「新增条目自动抽取媒体信息/封面」——两条路径全部短路，等价于未上线；若 worker 已被
   本功能启动，保存选项时会 `Dispose()` 停止它。
2. 代码回滚：还原本批改动的 8 个文件（`Options/MediaInfoExtractOptions.cs`、
   `ScheduledTask/ExtractMediaInfoTask.cs`、`Common/QueueManager.cs`、`Plugin.cs`、三语 resx 与
   `Resources.Designer.cs`），保留 batch 9 的 `LibraryApi.cs`（仅把 `IsImageCaptureExcluded` 恢复为
   `private`）。无数据迁移、无配置迁移。