# 片头检测现状与待修点（2026-11-08，Mac 侧诊断交接）

生产：Emby 4.10.2 容器 `emby`（NAS 192.168.31.51），插件 `StrmAssistantLite.dll`（我们自研，AssemblyVersion 1.0.1，本文件写作时线上构建含以下修复）。

## 已修并已上线（别回退）
1. `Task<SeasonFingerprintInfo>` → `Task<object>` 强转 bug：改为 `ReflectionTaskHelper.AwaitAsObject`，同类强转在 SubtitleApi / VideoThumbnailApi 一并修。`Unable to cast` 报错已归零。
2. `.strm` URL 未编码：新增 `StrmUrlHelper.Normalize`，接入 ffmpeg/ffprobe/探测链路与分布式 worker 参数。ffmpeg 输入现在形如 `...?/%E7%86%8A...`。
3. 并发：`GeneralOptions.IntroDetectionMaxConcurrentCount` 由 6 改 1（6 并发时 CMS 源返回 403）。改动前备份在同目录 `Strm Assistant.json.bak-before-conc`。
4. 媒体库层：电视剧(163842)、动漫剧集(197506) 的 `EnableMarkerDetection` 已置 true（通过 `POST /Library/VirtualFolders/LibraryOptions`，body 需 `{Id, LibraryOptions}` 包裹，直接传 LibraryOptions 会 500）。

## 关键结论（实测）
- 任务 `Extract Intro Fingerprint` 能扫到 14 季 / 762 条，但其中只有 **475 个唯一路径**，其余是同一路径的重复 `MediaItems` 行。
- 475 个唯一路径里 **474 个已有 `.fp` 指纹缓存**，路径在 `/config/metadata/library/xx/<ItemId>/title_10_<DateCreated ticks>.fp`。Emby 的 `AudioFingerprintManager.CreateTitleFingerprint` 命中缓存时直接返回，不启动 ffmpeg —— 因此"2 秒跑完"是缓存命中，不是没跑。
- 唯一没有 `.fp` 的是 `熊出没之小小世界 (2024) S01E48`，其源 h264 损坏（`crop values invalid`、`log2_max_frame_num_minus4 out of range`、`Invalid NAL unit 0`），属源问题。
- 22:40 那次真跑了 3 次 ffmpeg：E48×2（失败）+ 凡人修仙传 S01E34（成功，22:41 写入 `.fp`）。

## 待修（怀疑点，需在 NAS 最新源码 8afb9ca 上复核）
1. **作用域丢值**：日志 `IntroFingerprintExtract - LibraryScope: ALL`，但 ALL 只是 `librariesSelected` 为空时的兜底文案。配置里 `IntroSkipOptions.MarkerEnabledLibraryScope = "163842,197506"`，与库 ItemId 完全一致，却读不到 → 需查 `IntroSkipOptions` 的属性是否被 `[Browsable(false)]`/`[EditMultilSelect]` 之外的属性序列化影响，或 `GetPluginOptions()` 是否反序列化丢字段（本地旧副本里该属性曾出现"getter 恒为空"的实现，NAS 版本目前是 `{ get; set; } = string.Empty;` 自动属性，需确认运行时值）。
2. 空 scope 会导致 `FingerprintApi` 里 `libraryIds.All(i => i == "-1")` 对空数组恒真，从而 `ParentIds = GetAllFavoriteSeasons()`，**只扫收藏**。这就是扫描范围异常的根因链。
3. 每季结论 `has 0 episodes with audio fingerprints available` 需解释：既然 474 个路径都有 `.fp`，季级比对却报 0，需查季级聚合（`SeasonFingerprintInfo`）是否也受同样强转/缓存路径影响。
4. 修复后验收标准：对「电视剧」库的一次完整指纹任务，日志里 `LibraryScope` 应打印出真实库名（电视剧、动漫剧集），`episodes` 数应接近该库总集数，并且不少集能在 `/config/metadata/library/` 下生成新的 `.fp`。

## 生产约束
- 部署前必须备份 `StrmAssistantLite.dll`；容器崩溃用备份回滚。
- 不要在 `PluginOptions` 里加集合/`EditableObjectBase` 属性（Emby 4.10 设置页会栈溢出杀进程）。
- 任务类 logger 惰性取：`private ILogger _logger => Plugin.Instance?.Logger;`。
- 用户规则：不要使用/替换别人编译的 Pro DLL（目录里那个 `StrmAssistantPro.dll.others` 5.3MB 不是我们的，禁止加载）。
