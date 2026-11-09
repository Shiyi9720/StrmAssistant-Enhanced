# Batch 11 — 修复「抽帧成功但封面没有落库」（2026-11-10）

hermesh 派发（claude，worktree `s_75cglx6f_01m70kwasbmfybeqa6tdytm51n`）。代码只改
`StrmAssistant/Common/LibraryApi.cs`，另加本文档。构建 **0 error / 1 warning**（warning 为既有
`BaseItem.IsFolder` CS0618 噪音，不是本次新增）。

## 前置说明：本 worktree 的基线

派发时 HEAD 为 `02d2c2c`，该提交只包含 `docs/batch-9.md`。batch 9 的抽帧代码和 batch 10 的
自动入队代码都在 sibling 提交 `e0b462c`（`hermesh/s_75cglx6f_01m70jfqdrvttew8c899nqptyd` turn 1，
父提交即 `02d2c2c`）。开工前用 `git merge --ff-only e0b462c` 把本 worktree 分支快进到该提交，
**没有产生新提交**。因此下文的 `git diff --stat` 只统计 batch 11 自身，不包含 batch 9/10。

## 问题现象

对新入库的 Magnet 库 `.strm` Movie（`InternalId=247775`）：

1. 日志出现 `ImageCapture - Primary image saved: <name> - seek 695.04s - 110693 bytes`，无异常。
2. metadata 目录确实生成了 `poster.jpg`（1920x1080、110693 字节）。
3. `GET /Items/{id}` 的 `ImageTags` 为空；`GET /Items/{id}/Images/Primary` 返回 500
   （`ImageService: Error processing request` NRE）。
4. 直接查 `library.db`：`MediaItems.Images` 为 `NULL`。
5. 对照老条目 247319：外部脚本在媒体目录写过 `<name>-poster.jpg`，`Images` 列有记录，取图 200。

## 根因（已定位，不是 refresh 覆盖）

反编译 Emby 4.10.2 参考 DLL（`lib/emby/Emby.Providers.dll`）可看到：

```csharp
// Emby.Providers.Manager.ImageSaver.SaveImage(...)
...
item.SetImage(image, index);          // 只改内存里的 BaseItem.ImageInfos
if (updateImageCache) { ... }
...
// 方法结束：没有 UpdateToRepository / SaveItem / UpdateItem
```

也就是说 **`IProviderManager.SaveImage` 本身不落库**。正常图片获取链路能落库，是因为
`ItemImageProvider` 在调用 SaveImage 后执行了 `result.UpdateType |= ItemUpdateType.ImageUpdate`，
随后 `MetadataService.RefreshMetadata` 收尾时调用
`baseItem.UpdateToRepository(reason, metadataRefreshOptions)`（`MetadataService.SaveItem` 最后一行），
再由 `LibraryManager.UpdateItem` → `IItemRepository.SaveItems` → `SqliteItemRepository.SaveItem`
把 `BaseItem.ImageInfos` 序列化进 `MediaItems.Images`。

batch 9 的调用顺序是：

1. `OrchestrateMediaInfoProcessCoreAsync` 先 `await RefreshSingleItem(...)`，刷新流程已经结束并保存过条目；
2. 返回后 batch 9 才调用 `_providerManager.SaveImage(...)`；
3. SaveImage 写完文件、`SetImage` 改了内存对象，但**之后没有任何 Emby 代码再保存这个条目**。

所以文件存在、日志说 saved、DB 的 `Images` 却仍是 NULL。这不是「保存后又被 refresh 覆盖」：
refresh 在抽帧之前就已 await 完成；抽帧之后也没有常规保存动作。老条目 247319 能用，是因为封面
文件在媒体目录，库扫描把它识别为本地图片并写入了 `Images`，并未经过这条 SaveImage 路径。

### 次要风险（一并处理）

抽帧方法按 batch 11 要求重新 `GetItemById` 取了一个新实例并对它 `SetImage`。如果调用方之后又用
自己持有的旧实例保存（例如 `MediaInfoApi.DetectHdrVividAsync` 在编排完成后
`_libraryManager.UpdateItems(new List<BaseItem> { video }, ...)`），旧实例的 `ImageInfos` 不含刚补的
封面，会把 `Images` 覆盖回空。第 3 条改动里的 `SyncPrimaryImageInfo` 专门消除这个风险。

## 改法

全部位于 `StrmAssistant/Common/LibraryApi.cs`。

### 1. `TryCapturePrimaryImageAsync(Video item, CancellationToken cancellationToken)`

- 抽帧前重新 `_libraryManager.GetItemById(item.InternalId) as Video`；取不到（条目已删除）直接
  Debug 并返回 `false`，不再退回使用旧实例。
- 抽帧前 Debug 记录 `target.ImageInfos`（`ImageCapture - Capture start: ...`）。
- `SaveImage` 包独立 try/catch：失败只 Warn，继续走媒体目录降级；`OperationCanceledException` 照旧上抛。
- `SaveImage` 后读取内存里的 `target.GetImageInfo(ImageType.Primary, 0)`，Info 记录
  `SaveImage=ok/failed` 与 `in-memory primary=<path|none>`。
- 显式落库：`PersistImageInfo(target)`。
- 落库校验：`GetPersistedPrimaryImage(target.InternalId)` 重新从数据库读条目，Info 记录
  `Persistence after SaveImage: persisted/not persisted`。
- 落库失败（或 SaveImage 抛异常导致内存无图）时降级：
  1. `TrySaveFallbackPoster` 用 `IFileSystem.WriteAllBytes` 写
     `<媒体目录>/<文件名去扩展名>-poster.jpg`；写前若文件存在先 `SetAttributes(false,false)`
     清只读；任何 `UnauthorizedAccessException`/IO 失败只 Warn 并放弃本条。
  2. `target.SetImage(new ItemImageInfo { Path, Type=Primary, DateModified, Width, Height }, 0)`。
  3. 再次 `PersistImageInfo(target)` 并重新从数据库校验，Info 记录最终路径。
- 两次校验都失败：记录 24h 失败退避，Warn `Not persisted, giving up`，返回 `false`。
- 成功后 `SyncPrimaryImageInfo(item, persistedInfo)`，把最终图片记录同步回调用方持有的实例。
- 最终 Info 仍是 `ImageCapture - Primary image saved:`，追加最终持久化路径。

### 2. 新增私有方法

```csharp
private void PersistImageInfo(BaseItem item)
private ItemImageInfo GetPersistedPrimaryImage(long internalId)
private static void SyncPrimaryImageInfo(BaseItem callerItem, ItemImageInfo primary)
private bool TrySaveFallbackPoster(Video item, string sourcePath, out ItemImageInfo imageInfo)
private static bool TryGetJpegDimensions(byte[] bytes, out int width, out int height)
private static string DescribeImages(ItemImageInfo[] images)
```

`PersistImageInfo` 走
`_libraryManager.UpdateItems(new List<BaseItem> { item }, null, ItemUpdateType.ImageUpdate, true, false, null, CancellationToken.None)`
—— 与 Emby 普通刷新相同的仓库通道，`saveMetadata: false` 避免顺带写 NFO/XML，也不触发
`ItemUpdated` 事件。没有新增 SQL，没有新增依赖注入。

`TryGetJpegDimensions` 读取 JPEG 的 SOF 标记拿到宽高；Emby 自己的 ImageSaver 不填宽高，降级记录
可以填上。

### 3. 未改动的部分

- 抽帧命令 `BuildImageCaptureArguments`、seek 位置、`ImageCaptureExcludeMediaContainers` /
  扩展名/黑名单判断、24h 失败退避逻辑均未改。
- 其他库行为、章节缩略图、UI、选项、资源键均未改。
- `EnableImageCapture=false` 时 `TryCapturePrimaryImageAsync` 第一行就返回；
  `AutoExtractOnItemAdded=false` 时 batch 10 的入队路径也不执行 —— 默认关闭路径零行为变化。

## 验证

### 构建

```bash
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"
dotnet build StrmAssistant.sln \
  -p:EmbyServerDir=/vol3/1000/Projects/StrmAssistant/lib/emby -c Release -v:m
# => Build succeeded. 0 Error(s), 1 Warning（既有 CS0618）
```

### 生产验证（部署后）

1. 选一个确认没有主图的 `.strm` 条目，单独触发 `MediaInfoExtract`。
2. 正常日志应出现（按顺序）：

   ```
   ImageCapture - Frame ready: <name> - SaveImage=ok - in-memory primary=/config/metadata/library/.../poster.jpg
   ImageCapture - Persistence after SaveImage: <name> - persisted: /config/metadata/library/.../poster.jpg
   ImageCapture - Primary image saved: <name> - <strm> - seek <秒>s - <字节> bytes - /config/metadata/library/.../poster.jpg
   ```

   不应再出现 `not persisted`。

3. 数据库核对（先做 WAL 安全拷贝再查）：

   ```sql
   select Id, Name, Images from MediaItems where Id=<id>;
   ```

   `Images` 应包含一条 `...*Primary*...` 记录。

4. `GET /Items/{id}` 的 `ImageTags.Primary` 非空，`GET /Items/{id}/Images/Primary` 返回 200。
5. 重跑任务：已有主图的条目不会再抽帧，也不会重复写文件。

### 降级路径验证

让 metadata 目录不可写（或临时把该条目的 `GetInternalMetadataPath` 目标目录设为只读）后触发抽帧：

- 日志出现 `Persistence after SaveImage: ... not persisted`；
- 随后出现 `Fallback persistence: ... persisted: <媒体目录>/<名称>-poster.jpg`；
- 媒体目录生成 `-poster.jpg`，数据库 `Images` 指向该文件，取图 200；
- 再模拟媒体目录也不可写：应只出现一次 `Fallback poster not written` / `Not persisted, giving up`，
  后续 24h 内跳过该条目。

## 风险

- **多一次数据库写入**：每个成功补图的条目会在刷新保存之后再 `UpdateItems(ImageUpdate)` 一次。
  只在该条目无主图且开启 `EnableImageCapture` 时发生。
- **降级会多写一份图**：只有 metadata 目录写入成功但图片记录仍无法落库时，才会在媒体目录再写
  `<name>-poster.jpg`；历史 metadata 目录里的图会残留，不影响使用。
- **媒体目录只读**：降级写盘失败只 Warn 并记录 24h 退避，不影响媒体信息任务本身。
- **调用方实例同步**：`SyncPrimaryImageInfo` 只改内存里调用方实例的 Primary 记录，不额外写库；
  目的是防止后续用旧实例保存时覆盖 `Images`。
- **未覆盖 SaveImage 的其他调用点**：`DoubanApi` / `MediaInfoApi` 里也有直接
  `SaveImage` 的调用，但它们位于各自的刷新/反序列化流程中，后续仍会走 Emby 的保存通道；
  本批不改它们，以免扩大行为面。

## 回滚

1. 首选：关闭 `EnableImageCapture`（默认即关闭），新增逻辑全部短路。
2. 代码回滚：仅还原 `StrmAssistant/Common/LibraryApi.cs` 中本批改动（抽帧后的落库/校验/降级及
   六个私有方法），保留 batch 9/10 代码即可；无数据迁移、无配置迁移、无资源键变更。