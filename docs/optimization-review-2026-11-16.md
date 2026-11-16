# StrmAssistant 代码优化审查（2026-11-16）

> **审查性质**：本文是一次**只读静态代码分析**的整理结果，**未做任何运行时验证**（没有压测、
> 没有内存/CPU profile、没有在真实 Emby 媒体库上复现）。所有「影响」判断都是基于代码结构的
> 推断，量化描述（如「千万级比较」「数百 MB」）用于说明量级，不是实测值。
>
> **基线**：分支 `port/emby-4.10-stable`，HEAD `6637da0`，版本 v1.0.9；目标运行时 Emby 4.10.2，
> C# / netstandard2.1。
>
> **已排除**：`docs/batch-*.md` 中已经记录并处理过的结论（例如 batch-17 的中文卫星资源修复与
> 启动日志降噪）不再重复列出；本文只收录目前仍存在于代码中的问题。
>
> **行号说明**：文中路径相对于 `StrmAssistant/` 项目目录（例如 `Common/SearchIndexService.cs`
> 即 `StrmAssistant/Common/SearchIndexService.cs`）。行号基于上述 HEAD 抽查，重构后可能偏移，
> 定位时请以符号名（方法名）为准。

## 问题总览

| # | 问题 | 主要位置 | 预估工作量 |
|---|------|----------|-----------|
| 1 | 中文搜索「全表 × 全 key」线性扫描，索引构建/加载存在 N+1 与深分页 | `Common/SearchIndexService.cs` | 大 |
| 2 | 定时任务把全库条目一次性物化到内存 | `Common/LibraryApi.cs`、`Common/FingerprintApi.cs` | 中 |
| 3 | 并发度调整热替换 `SemaphoreSlim`，计数失真、任务可能丢失 | `Common/QueueManager.cs`、`Plugin.cs` | 中 |
| 4 | 每个条目重复解析选项、重建排除集合/黑名单 | `Common/LibraryApi.cs`、`Common/ItemBlacklist.cs`、`Common/MediaInfoApi.cs` | 中 |
| 5 | 每成功一条就全量重写 JSON，部分发生在播放事件线程/持锁 | `Common/DoubanRatingStore.cs`、`Common/VersionPlaybackApi.cs` | 中 |
| 6 | 本地元数据刷图时每集重复枚举同一图片目录 | `Common/OverrideImageService.cs` | 小 |
| 7 | 循环内二次方查找和逐条数据库查询 | `Common/ChapterApi.cs`、`Common/FingerprintApi.cs` | 小 |
| 8 | 库范围/路径过滤逻辑复制 7 份，且用文化敏感比较 | `Common/LibraryApi.cs`、`Common/FingerprintApi.cs`、`Common/VideoThumbnailApi.cs`、`IntroSkip/PlaySessionMonitor.cs`、`Mod/IntroDetectionForShortcut.cs` | 中 |
| 9 | 入库即提取的队列去重是线性扫描 | `Plugin.cs` | 小 |
| 10 | 异常被大量吞掉，堆栈默认不可见 | 全仓（约 86 + 31 处） | 中 |

---

## 1. 中文搜索是「全表 × 全 key」线性扫描，索引构建/加载也有 N+1 和深分页

**位置**

- `Common/SearchIndexService.cs:537-576`（`Match`）
- `Common/SearchIndexService.cs:578-630`（`FindBest`）
- `Common/SearchIndexService.cs:159-282`（`BuildAsync`）
- `Common/SearchIndexService.cs:326-345`（`GetActorNames`）
- `Common/SearchIndexService.cs:766-807`（`TryLoad`）
- `Common/SearchIndexService.cs:949-1021`（增量更新）

**现象**

- 每次搜索都会遍历快照中的**每一个条目**、条目下的**每一个 key**，再对**每一个查询变体**
  逐个执行 `StartsWith` / `IndexOf`。10 万条目 × 几十个 key，单次查询就是千万级字符串比较。
- `Match` 没有取消令牌，也没有超时或结果上限，查询延迟完全随库规模线性增长。
- `TryLoad` 加载索引时，只从原始条目重新跑拼音/繁简转换，**索引文件里没有存已经算好的 key**，
  因此每次插件重启都要重新支付这笔 CPU。
- `BuildAsync` 逐条调用 `GetItemPeople`（N+1），按 `SortName` 深分页拉取，并且超过 24 小时
  就全量重建；新条目的 alias 查询最多 1000 次请求，每次还 `Task.Delay(250)`。
- 构建与增量更新并发时，构建结束时对整表的替换会覆盖构建期间已经处理过的增量更新。

**影响**

- 大库上中文搜索明显卡顿，且会占用 Emby 主进程线程与 CPU，与播放、扫描相互影响。
- 重启后首次搜索延迟高；索引重建期间搜索能力退化。
- 增量更新可能被静默丢弃，搜索结果与实际库不一致。

**建议改法**

- 持久化**倒排表**：精确/前缀桶 `key → item id`，查询时先取候选集，再只对候选执行
  tier 评分（`FindBest` 仅作为候选项的排序器），把复杂度从 O(全表) 降到 O(候选)。
- 索引文件直接存储已构建好的 key（含拼音、繁简变体），`TryLoad` 只做反序列化，不再重算。
- people/alias 批量化：按批调用 `GetItemPeople`，alias 查询加缓存与并发限流，去掉逐条延时。
- 构建期间**暂停并合并**增量更新：构建使用独立快照 + 变更队列，结束时把队列合并进新快照，
  而不是无条件整表替换。
- 为 `Match` 增加 `CancellationToken`、超时和最大候选数保护。

**预估工作量：大**

---

## 2. 定时任务把全库条目一次性物化到内存

**位置**

- `Common/LibraryApi.cs:258-374`、`376-457`、`1795-1816`、`1845-1874`
- `Common/FingerprintApi.cs:403-446`、`448-533`

**现象**

- `GetItemList` 直接返回全库视频/音频/剧集；随后的图片捕获和 extras 处理各自再查一遍全库；
  接着 `Concat` / `GroupBy` / `OrderBy` 又复制一遍全量结果。
- 片头任务把 pre / post 两次全库查询结果拼成一个大 `List`。

**影响**

- 十万级媒体库时峰值内存可达数百 MB，与库扫描、播放抢内存，极端情况下 OOM。
- 大对象堆压力导致 GC 停顿，表现为 Emby 整体卡顿。

**建议改法**

- 沿用仓库中 `PinyinSortNameTask` / `DoubanCachePrebuildTask` 已有的
  `GetInternalItemIds` + 每批 200-500 条 `GetItemList` 的流式模式，处理完即释放。
- 图片捕获与 extras 复用同一批条目，不要各自重新查全库。
- 片头任务把 pre / post 两次全库查询合并为一次，再在内存中分流。

**预估工作量：中**

---

## 3. 并发度调整会热替换 `SemaphoreSlim`，计数失真、任务可能丢失

**位置**

- `Common/QueueManager.cs:161-198`（替换/`Dispose`）
- `Common/QueueManager.cs:315-324` 与 `397`
- `Common/QueueManager.cs:547-556` 与 `630`
- `Common/QueueManager.cs:844-853` 与 `896`
- `Plugin.cs:1127-1146`

**现象**

- 任务获取信号量后，`finally` 里的 `Release()` 读的是**静态字段**；如果期间选项保存触发了
  信号量替换，`Release()` 就释放到了新信号量上。
- 等待旧信号量的任务可能永久挂起，或在旧实例 `Dispose` 后抛 `ObjectDisposedException`；
  外层 `catch { break; }` 会静默结束该批，已经取出的条目直接丢失。
- 替换瞬间新旧两个信号量同时存在，实际并发数会短时超过配置上限。

**影响**

- 并发上限配置失真，可能超限打爆 Emby 或下游服务；也可能让队列 worker 提前退出，
  造成任务静默丢失、队列「看起来在跑但不动」。

**建议改法**

- 不替换实例：改为可动态调整上限的自定义 `AsyncSemaphore`（内部维护当前上限，调小时
  自然收敛，调大时唤醒等待者）。
- 若暂时保留替换方案，任务必须持有**获取时的局部引用**再 `Release`；替换时先取消旧等待者，
  并把已取出未处理的条目重新入队。
- 收窄 catch-all，至少区分 `OperationCanceledException` 与真实异常。

**预估工作量：中**

---

## 4. 每个条目都重新解析选项、重建排除集合/黑名单

**位置**

- `Common/LibraryApi.cs:50-91`、`527-563`（535/541）、`723`、`920-939`、`1130-1145`
- `Common/ItemBlacklist.cs:22-50`、`69-96`
- `Common/MediaInfoApi.cs:187-217`

**现象**

- 扫描/提取每个条目时，至少重建 2-4 个数组或 `List`（容器集合、扩展名集合等），
  来源都是把选项字符串现场 `Split`。
- `ItemBlacklist` 的 `_ownMatches` 缓存因为实例是一次性的而完全失效，每个条目都重新算。
- 通配符匹配每次都现拼 pattern 再调 `Regex.IsMatch`，正则无法被缓存复用。

**影响**

- 大批量提取时产生大量短命分配与 GC 压力；正则重复编译进一步放大 CPU 开销。
- 逻辑上相同的配置在每个条目上算出不同实例，也让缓存失效问题难以察觉。

**建议改法**

- 按选项字符串静态缓存解析结果（容器/扩展名 `HashSet`、wildcard 正则），
  在选项保存时统一失效。
- 正则预编译（`RegexOptions.Compiled`）并复用。
- 黑名单按批次创建一次，整批复用；`_ownMatches` 改为与选项生命周期一致的缓存。

**预估工作量：中**

---

## 5. 每成功一条就全量重写 JSON，有的在播放事件线程/持锁

**位置**

- `Common/DoubanRatingStore.cs:66-133`（`Save` 每条持锁 `SerializeToFile` 整个文件）
- `Common/VersionPlaybackApi.cs:310-401` 与 `815-843`（播放事件回调持 `_sync` 全量序列化）

**现象**

- 豆瓣评分预构建 N 条累计 O(N²) 写入，磁盘 IO 风暴；读取评分角标的 `Get` 在锁上排队。
- 多版本断点每 30 秒全量同步写一次（保留 180 天），且写盘发生在播放事件回调线程上。

**影响**

- 配置目录位于 NAS 时，写盘延迟直接卡住播放事件线程，表现为播放状态更新迟滞。
- 评分角标读取被写锁阻塞，UI 上出现空白或延迟。

**建议改法**

- 统一为「脏标记 + 防抖 + 锁内拷贝、锁外序列化、原子替换」，参照
  `IntroDbApi.PersistIfNeeded` 的既有实现。
- 播放停止时只排队写盘，不等待落盘完成。

**预估工作量：中**

---

## 6. 本地元数据自动刷图时每集重复枚举同一图片目录

**位置**

- `Common/OverrideImageService.cs:134-233`、`239-273`、`275-320`、`322-357`

**现象**

- 每个 Episode 的 Added/Updated 事件都会走 `GetSeasonEpisodeImages`，对同一个
  `<tmdbid>/images` 目录至少 `Directory.GetFiles` 两次，再逐个做正则匹配。
- 整季扫描的 IO 量等于「集数 × 目录文件数」，全部落在 NAS 上。

**影响**

- 季包入库/刷新时产生大量重复 NAS IO，事件线程被拖慢，刷图明显滞后。

**建议改法**

- 按 `(tmdbid, season)` 缓存目录清单，使用短 TTL 或文件变更失效。
- 更直接的做法是拼接 `season-x.jpg` / `episode-x-y.jpg` 精确路径后 `File.Exists`，
  避免整目录枚举。
- 同一季的连续更新事件做合并去抖。

**预估工作量：小**

---

## 7. 循环内二次方查找和逐条数据库查询

**位置**

- `Common/ChapterApi.cs:401-448`（416：每个 episode 重新 `episodesInScope.Select(...).Contains`）
- `Common/FingerprintApi.cs:519-521` 与 `535-551`
  （每条 episode 调一次 `GetCollectionFolders`，每次重新 `Overrides().Any()`）

**现象**

- 章节处理中对每个 episode 都重建 `episodesInScope` 的 id 序列并线性 `Contains`，
  复杂度 O(n²)。
- 指纹时长对每条 episode 都重新查询所属 collection folders 并重新判断 overrides。

**影响**

- 剧集数大时章节/指纹任务耗时呈二次方增长，任务窗口拉长，挤占其他队列。

**建议改法**

- `episodesInScope` 的 id 预先建成 `HashSet<long>`，循环内 O(1) 查找。
- 指纹时长按 library 一次算成字典，episode 循环内直接查表。

**预估工作量：小**

---

## 8. 库范围/路径过滤逻辑复制了 7 份，且用文化敏感比较

**位置**

- `Common/LibraryApi.cs:144-147`、`207-256`、`229-235`、`258-374`、`376-457`
- `Common/FingerprintApi.cs:289-292`、`334-372`、`403-446`、`448-533`
- `Common/VideoThumbnailApi.cs:583-670`
- `IntroSkip/PlaySessionMonitor.cs:424-427`
- `Mod/IntroDetectionForShortcut.cs:128-137`

**现象**

- 同一套「解析 `LibraryScope` → `GetVirtualFolders` → 收藏 `-1` → `PathStartsWith` →
  黑名单 → 去重」流程复制了 7 处。
- 各处去重键不一致（`InternalId` vs `Path`），空值处理不一致。
- 路径前缀使用单参数 `StartsWith`，是 culture-sensitive 比较。

**影响**

- 不同任务对「库范围」的理解可能不一致，出现某个任务多处理/漏处理库的情况。
- culture-sensitive 的路径比较在特定区域设置下可能给出错误结果（例如忽略某些字符差异），
  属于隐性正确性问题。

**建议改法**

- 抽出共享的 scope resolver / filter，统一去重键（建议 `InternalId`）与空值语义。
- 路径比较统一使用 `StringComparison.OrdinalIgnoreCase`。

**预估工作量：中**

---

## 9. 入库即提取的队列去重是线性扫描

**位置**

- `Plugin.cs:606-653`（关键在 644）

**现象**

- 用 `ConcurrentQueue.Any(i => i.InternalId == ...)` 从头枚举做去重，批量入库时是 O(N²)。
- 存在 check-then-enqueue 竞态：两个事件并发检查都通过，同一条目被重复入队。

**影响**

- 大批量入库（整季/整目录拷贝）时 CPU 飙高、入队变慢，并可能重复提取同一文件。

**建议改法**

- 改为 `ConcurrentDictionary<long, byte>` 作为去重集合：入队 `TryAdd`，worker 取出后 `TryRemove`。
- 入队与去重合并为一次原子操作，消除竞态。

**预估工作量：小**

---

## 10. 异常被大量吞掉，堆栈默认不可见

**位置**

- 全仓约 **86 处** `Logger.Debug(e.StackTrace)`、**31 处**只记录 `Error(e.Message)`
- 大量 `catch { break; }` 或空 catch，例如 `Common/QueueManager.cs:254-257`、`317-319`、`549-552`

**现象**

- 默认 `DebugMode=false` 时，生产日志里往往只剩一句话，没有堆栈，无法定位。
- `QueueManager` 的 catch-all 把非取消异常也当作正常退出处理。

**影响**

- 线上问题难以诊断，队列静默停摆时没有可用的错误信息。
- 真实 bug 被当作「任务正常结束」，长期不可见。

**建议改法**

- 统一使用带异常的日志接口（`ErrorException` 或 `Warn` 并附堆栈），避免只记 `Message`。
- catch-all 收窄到 `OperationCanceledException` 等预期异常；其余记 `Error` 后继续下一批，
  而不是 `break` 退出整个循环。

**预估工作量：中**（改动机械但面广，需要逐处判断哪些异常是预期内的）

---

## 附录：顺手项

以下问题规模较小或局部性强，未单列优先级，但建议在相关改动中一并处理。

- **代理请求不复用连接**：`Common/ProxyApi.cs:260-264` 每个代理请求都新建
  `HttpClient` / `HttpClientHandler`，没有连接复用，高并发下会耗尽 socket 并放大 TLS 握手开销。
- **ffprobe/ffmpeg 热路径同步阻塞**：`Mod/ExclusiveExtract.cs:233-243` 在热路径上用
  `Task.Run(...).Result` 同步等待挂载解析，且 `GetStrmMountPath` 使用 `CancellationToken.None`，
  无法随请求取消；应改为异步或至少传入取消令牌。
- **静态 `Random` 并发使用**：`Common/QueueManager.cs:22`、`864` 与
  `ScheduledTask/RefreshMovieTask.cs:21`、`65` 的 `static Random` 非线程安全，
  多线程同时调用可能产生重复序列甚至内部状态损坏（目标框架 netstandard2.1 没有
  `Random.Shared`）。改用加锁的单一实例或每线程独立实例。
- **`AllUsers` 字典并发读写且不清理**：`Common/LibraryApi.cs:104`、`149-163` 的
  `AllUsers` 是普通 `Dictionary`，用户增删时写入、队列线程同时枚举，可能抛
  `InvalidOperationException`；且已删除用户不会被清理。建议改为
  `ConcurrentDictionary` 并处理用户删除事件。
- **会话/时间字典泄漏**：`IntroSkip/PlaySessionMonitor.cs:30-34`、`337-339` 的会话数据与
  最近更新时间字典只在正常 `PlaybackStopped` 时清理，异常断开（崩溃、网络中断）会持续泄漏。
  建议增加基于时间的清理扫描或定期重建。
- **定时任务阻塞线程**：4 个定时任务使用 `Task.Delay(10).Wait()` 阻塞线程
  （例如 `ScheduledTask/RefreshMovieTask.cs:100`）。应改为 `await Task.Delay(...)`，
  避免占用线程池线程。
