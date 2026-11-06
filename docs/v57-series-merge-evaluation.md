# Pro v3.0.0.57「优化剧集跨媒体库合并的性能」评估

- 评估对象：Pro v3.0.0.57 更新项「优化剧集跨媒体库合并的性能」
- 评估方式：只读代码走查 + 生产 Emby 4.10 程序集行为核对（未反编译 Pro）
- 分支：`hermesh/s_pm7f7civ_01m6r0458bqh7g28nyq4cbdbg3`（含第二十五、二十六批）
- 结论先行：**我们当前分支里没有活跃的「剧集跨媒体库合并」路径，不存在可优化的对应性能问题，不需要为这条更新项跟进。**
  如果产品上希望补上这个能力，那是一次功能开发（中等量级、较高风险），不是一次性能优化。

---

## 1. 我们的跨库合并实现现状

### 1.1 代码分布

| 位置 | 作用 |
| --- | --- |
| `StrmAssistant/ScheduledTask/MergeMultiVersionTask.cs` | 合并任务主体（电影 + 剧集分支） |
| `StrmAssistant/Options/ExperienceEnhanceOptions.cs` | `MergeMultiVersion` 开关、电影/剧集合并范围选项 |
| `StrmAssistant/Plugin.cs` | 触发点：刷新完成事件、库选项更新事件 |
| `StrmAssistant/Common/LibraryApi.cs` | 取库列表、强制开启 Emby 自动剧集分组 |
| `StrmAssistant/Common/MetadataApi.cs` | 合并时使用的刷新参数（ValidationOnly） |

除此之外，全库搜索 `MergeItems` / `RefreshFullItem` 只在 `MergeMultiVersionTask.cs` 出现
（`:90`、`:119`、`:382`），没有第二条合并实现。

### 1.2 触发时机

有两条触发链：

1. **扫库后置任务**：`TriggerMergeMovieTask : ILibraryPostScanTask`（`MergeMultiVersionTask.cs:20-32`）。
   生产 Emby 4.10 的 `ApplicationHost.FindParts()` 用
   `LibraryManager.AddParts(..., GetExports<ILibraryPostScanTask>(), ...)` 收集所有具体实现，
   `Container.Options.ResolveUnregisteredConcreteTypes = true`，因此插件程序集里这个 public 具体类
   会被 DI 直接实例化并注册为扫库后置任务；`LibraryManager.PerformLibraryValidation()` 在每次
   **完整媒体库校验扫描**结束时调用 `RunPostScanTasks()`。任务内部只判断
   `MergeMultiVersion` 开关（默认 `false`，`ExperienceEnhanceOptions.cs:17`）。
   注意：这条链**不区分库类型**，电视库、混合库扫描结束也会调用。
2. **刷新完成后**：`Plugin.OnRefreshCompleted`（`Plugin.cs:377-396`）在
   `_currentMergeMultiVersion && e.Argument.Item.IsTopParent` 且库类型是 `Movies` 或 `null` 时，
   设置 `MergeMultiVersionTask.CurrentScanLibrary` 并让 TaskManager 执行任务。
   这条链只对电影库生效，不会因剧集库刷新而触发合并。

### 1.3 Execute 主流程

`MergeMultiVersionTask.Execute`（`:51-156`）：

```
Execute
├─ PrepareMergeSeries()                  → 剧集库分组（实际恒为空，见 1.4）
├─ PrepareMergeMovies(CurrentScanLibrary)→ 电影库分组
├─ processSeries = 组非空 && 当前库类型是 TvShows/null
├─ processMovies = 组非空 && 当前库类型是 Movies/null
├─ if (processSeries)
│   ├─ FindDuplicateSeries()             → 每个重复剧集 RefreshFullItem
│   └─ FindInconsistentSeries()          → 每个不一致剧集 RefreshFullItem
└─ if (processMovies)
    └─ 逐库 ExecuteMergeMovies()          → 查重 + Union-Find + MergeItems
```

进度权重（5/35/5/50）只是 UI 进度分配，没有时间含义。

### 1.4 剧集分支：结构完整，但从不执行（已确认）

证据链（全部可在当前分支复核）：

1. `ExperienceEnhanceOptions.cs:40-41`：
   ```csharp
   [Browsable(false)]
   public MergeSeriesScopeOption MergeSeriesPreference => MergeSeriesScopeOption.LibraryScope;
   ```
   这是**只读表达式属性**，没有 setter，配置反序列化也无法把它改成 `GlobalScope`，永远是
   `LibraryScope`。
2. `MergeMultiVersionTask.cs:185`：
   ```csharp
   if (!Plugin.Instance.IsModSupported || mergeSeriesPreference != MergeSeriesScopeOption.GlobalScope)
       return Array.Empty<long>();
   ```
   因此 `PrepareMergeSeries()` 恒返回空数组。
3. `:63-65` 的 `processSeries` 要求 `seriesLibraryGroups.Any()`，恒为 `false`。
4. `FindDuplicateSeries` / `FindInconsistentSeries` / 两处逐剧集 `RefreshFullItem` 因此全部不可达。
5. 即便把第 1 条改成可写，`PrepareMergeSeries` 还要求 `IsModSupported`（Harmony 补丁可用）且库的
   `EnableAutomaticSeriesGrouping` 为真；而插件只在**库选项更新事件**里强制开启该选项
   （`Plugin.cs:413-433`、`LibraryApi.cs:1069-1086`），老库不会自动补上。

结论：**我们的剧集跨库合并是死代码**。它不产生任何扫描/任务开销，也没有可优化的性能对象。
这段代码来自上游公开仓库 `sjtuross/StrmAssistant`（`upstream/main` 的
`MergeMultiVersionTask.cs` 与 `ExperienceEnhanceOptions.cs` 与本分支逐字相同），
说明这是上游公开版/精简版的既定状态，而不是我们改坏的。

### 1.5 用户实际看到的「剧集合并」来自 Emby

插件里唯一与剧集分组有关的行为是 `LibraryApi.EnsureLibraryEnabledAutomaticSeriesGrouping()`
（`:1069-1086`）：把电视库的 `EnableAutomaticSeriesGrouping` 置为 `true`。

生产 Emby 4.10 `MediaBrowser.Controller.dll` 的
`Series.CreatePresentationUniqueKey` 只在满足
`EnableAutomaticSeriesGrouping && 有 tvdb/tmdb/imdb id` 时改写展示键：

```
PUKey = 用户数据键（tvdb/imdb/tmdb id）+ 首选元数据语言 + 该剧集所属 CollectionFolder 的 id 列表
```

也就是说，这个选项合并的是**同一个媒体库内、分散在多个文件夹/多个 Series 条目**的同一部剧；
不同媒体库的 id 列表不同，展示键始终不同，**不会跨库合并**。
这是 Emby 内置行为，我们只负责把开关打开，没有代码可优化。

---

## 2. 瓶颈分析

### 2.1 已确认（代码/生产程序集可验证）

1. **剧集分支零开销**：如 1.4，`PrepareMergeSeries` 只做一次选项读取和一次日志。
2. **电影分支是唯一实际执行的合并路径**，且只在 `MergeMultiVersion` 打开时运行。
3. **扫库后置任务在 `CurrentScanLibrary == null` 时按默认 `LibraryScope` 遍历所有电影库**
   （`MergeMultiVersionTask.cs:274-291`：条件不成立走 `else`，`GetMovieLibraries()` 返回的每个库
   单独成组）。也就是说，一次电视库扫描结束也会触发一轮「全部电影库」的合并扫描。
4. **`ExecuteMergeMovies` 的查询形态**（`:299-326`）：
   - 每个电影库一次递归查询，把该库所有带 tmdb/imdb/tvdb id 的电影整条载入内存；
   - 内存中按 `(providerId 名称, 值)` 分组；
   - 对**每个分组的每一个条目**调用 `GetAlternateVersionIds()`。
5. **`GetAlternateVersionIds()` 是一次同步数据库查询**（生产 `MediaBrowser.Controller.dll` 的
   `Video.GetAlternateVersionIds()`：`LibraryManager.GetInternalItemIds(new InternalItemsQuery {
   PresentationUniqueKey = ..., ExcludeItemIds = [self], IncludeItemTypes = [self 类型], Recursive = true })`）。
   一部电影最多出现在 tmdb/imdb/tvdb 三个分组里，因此最坏情况是**每部电影 3 次数据库查询**，
   与是否存在重复无关；这是当前实现里最明确的 N 次查询放大点。
6. **组内比较是 O(k²)**：`groupItems.Any(i => i.InternalId == id)` 对每个候选 id 线性扫描组内列表
   （`:321-322`）。
7. **`MergeItems` 的写入成本按条目数线性增长**：生产 `LibraryManager.MergeItems` 给每个条目分配新的
   随机 `PresentationUniqueKey` 并逐个 `UpdateToRepository(MetadataEdit)`；每个条目都会触发
   `ItemUpdated` 事件，插件自己的 `OnItemUpdated` 会调用 `SearchIndexService.NotifyItemChanged`
   （`Plugin.cs:576-578`，去抖后批量重建索引）。
8. **剧集分支若启用的两次查询形态**（仅作为成本模型参考）：
   - `FindDuplicateSeries` 一次查询载入所有剧集条目（只含 Series，量级小）；
   - `FindInconsistentSeries` 的查询带 `GroupBySeriesPresentationUniqueKey = true`，
     生产 `SqliteItemRepository` 会生成 `GROUP BY coalesce(SeriesPresentationUniqueKey, PresentationUniqueKey)`，
     即**在数据库侧对选中库内的全部 Season/Episode 行做分组**，返回每个剧集键一行。
     结果集不大，但分组表达式不是普通列，无法直接利用现有的
     `(Type, SeriesPresentationUniqueKey, PresentationUniqueKey, DateCreated)` 索引完成分组（推断，见 2.2）。
9. **刷新参数比想象中轻**：`MetadataApi.GetMetadataValidationRefreshOptions()`（`:68-80`）使用
   `MetadataRefreshMode.ValidationOnly`，且没有设置 `Recursive`（生产 `MetadataRefreshOptions.Recursive`
   默认 `false`）。生产 `ProviderManager.RefreshFullItem` → `RefreshItem` 因此只刷新该条目本身、
   不递归子项；`MetadataService` 在 `ValidationOnly` 下会过滤掉所有 `IRemoteMetadataProvider` 并跳过
   `ExecuteRemoteProviders`。所以「逐剧集 RefreshFullItem」不是远程请求风暴，成本主要是本地 provider +
   数据库读写。

### 2.2 推断（未在真实多库环境实测）

1. **耗时量级**：电影分支在中等规模库（数千部电影）每次扫库后可能产生数千到上万次
   `GetAlternateVersionIds` 数据库往返；在机械盘/大库上这可能从几秒到几十秒。具体数值未测。
2. **`GROUP BY coalesce(...)` 的 SQLite 执行计划**：可能退化为对 `Type IN (Season, Episode)` 的
   全表扫描 + 临时 B 树排序。未在真实库上 `EXPLAIN QUERY PLAN` 验证。
3. **Emby 自动剧集分组的扫描期成本**：开启后 `Series` 的展示键要拼库 id，扫描时每条剧集都要计算，
   但这是 Emby 内部实现，我们没有补丁、也没有生产计时数据。
4. **Pro 的优化方向**：见第 3 节，纯推断。

### 2.3 小结

我们**没有**「活跃的剧集跨库合并」性能问题：该功能在本分支不可达。
当前实现里真实存在的性能面是**电影多版本合并**，且只在用户显式打开 `MergeMultiVersion` 后生效。

---

## 3. Pro 优化方向推断（全部为推断）

Pro 的更新项描述只有一句话，我们未反编译、未使用 Pro DLL，以下仅是从事务结构出发的推断：

1. **Pro 很可能把剧集跨库合并真正启用了**。依据：我们（以及上游公开仓库）保留了完整的
   `MergeSeriesScopeOption` 枚举、`GlobalScope` 分支、孤儿资源键
   `ExperienceEnhanceOptions_MergeSeriesPreferences_Series_Merge_Preference`
   （`Resources.Designer.cs:759-763`），但把属性硬编码成 `LibraryScope` + `[Browsable(false)]`。
   这是典型的「功能骨架保留、公开版关闭」形态。
2. **最可能的性能优化点是把「逐剧集 RefreshFullItem」换成批量/最小写入**：
   - 先用一次查询定位重复剧集，只对展示键确实不一致的剧集重算并直接落库，
     而不是对每个重复项走一遍刷新管线；
   - 复用 `PresentationUniqueKey` 计算结果，避免重复查询。
3. **其次是把遍历范围从「所有电视库」缩到「本次扫描/变更的库」**（增量触发）。
   我们现在 `FindInconsistentSeries` 一旦启用就是全库递归查询，这是最容易被放大的地方。
4. **也可能是把数据库侧分组交给 `GroupBySeriesPresentationUniqueKey` 之外的手段**
   （例如按 `SeriesId`/`SeriesPresentationUniqueKey` 等值查询分批处理），避免 `coalesce` 表达式分组。
5. **触发时机**可能从「每次扫库」改为「按需/定时」或「仅在剧集库变化后」。

以上每一条都只是合理方向，不能当作 Pro 的实现事实。

---

## 4. 跟进建议

### 4.1 对「性能优化」这条更新项：不需要跟进

- 对应功能在本分支不可达（1.4 证据链），没有性能热点可优化；
- 用户实际看到的剧集合并由 Emby 自动分组完成（1.5），我们没有可改的代码；
- 为了对齐一条更新项去重写合并任务，属于无收益改动。

### 4.2 如果产品上确实要补「剧集跨媒体库合并」能力

这应按**新功能**立项，而不是性能优化。参考改动清单：

| 步骤 | 文件 | 量级 | 说明 |
| --- | --- | --- | --- |
| 1. 放开开关 | `Options/ExperienceEnhanceOptions.cs:40-41` | 极小 | 改为可写属性、去掉 `[Browsable(false)]`；resx 孤儿键可复用 |
| 2. 保证库选项 | `Plugin.cs` 初始化路径 | 小 | 启动或选项保存时对电视库调用 `EnsureLibraryEnabledAutomaticSeriesGrouping()`，不要只依赖库选项更新事件 |
| 3. 合并逻辑批量化 | `ScheduledTask/MergeMultiVersionTask.cs` | 中 | 用一次查询 + 内存映射替代逐条导航；按 `SeriesId` 等值分批 |
| 4. 写入最小化 | 同上 + `MetadataApi` | 中 | 只对展示键不一致的条目落库，避免全量 Refresh |
| 5. 范围收敛 | 同上 | 小 | 优先使用 `CurrentScanLibrary`，扫库后置任务不要把全部电视库重扫 |
| 6. 验证 | 真实多库环境 | 大 | 必须有「同一部剧分布在 2 个以上库/多个文件夹」的样本 |

**预期收益**：仅在用户存在跨库重复剧集时可见（合并后的展示/播放体验），对无重复库没有收益。
**主要风险**：
- Emby 的展示键语义把库 id 纳入其中（1.5），强行跨库合并会改变用户数据/收藏/播放状态的归属，
  有串库风险（这也是第十批 `VersionFavoriteApi` 明确按库隔离的原因）；
- 全量刷新可能引发大量 `ItemUpdated` 事件与索引重建；
- 上游公开版至今把这功能关着，说明已知问题可能不止性能。

---

## 5. 限制与未验证

- 未在真实多库环境运行或压测，没有生产日志计时；第 2.2 节全部是推断。
- Emby 内部行为来自本机已有的生产 Emby 4.10 程序集反编译（ApplicationHost、LibraryManager、
  Series、Video、ProviderManager、SqliteItemRepository），不是 Pro 程序集。
- 未反编译 Pro、未使用 Pro DLL、未抄 Pro 代码；第 3 节是推断。
- 未验证 SQLite 对 `GROUP BY coalesce(...)` 的实际执行计划。
- 「扫库后置任务会被 DI 实例化」依据的是生产 Emby 4.10 的发现/实例化代码路径，
  未在生产日志中核对 `Running post-scan task TriggerMergeMovieTask`。

## 附录：关键代码位置

| 主题 | 位置 |
| --- | --- |
| 扫库后置任务 | `StrmAssistant/ScheduledTask/MergeMultiVersionTask.cs:20-32` |
| Execute 主流程 | 同文件 `:51-156` |
| 剧集分支开关（死代码） | 同文件 `:180-199`，`Options/ExperienceEnhanceOptions.cs:40-41` |
| 重复剧集查询 | 同文件 `:201-238` |
| 不一致剧集查询 | 同文件 `:240-265` |
| 电影库分组 | 同文件 `:267-295` |
| 电影合并与逐条版本查询 | 同文件 `:297-401`（重点 `:321-322`） |
| 刷新完成后触发（仅电影库） | `StrmAssistant/Plugin.cs:377-396` |
| 强制开启 Emby 自动剧集分组 | `StrmAssistant/Plugin.cs:413-433`、`Common/LibraryApi.cs:1069-1086` |
| ValidationOnly 刷新参数 | `StrmAssistant/Common/MetadataApi.cs:68-80` |
| 用户可见的「剧集合并」= Emby 库内分组 | 生产 `MediaBrowser.Controller.dll` 的 `Series.CreatePresentationUniqueKey` |
