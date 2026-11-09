# Batch 6 — 三个官方 UI 增强功能补齐（2026-11-09）

hermesh 派发（agent claude，session `s_75cglx6f_01m6ytqrwjjaghkvnynqkq7cfx`），
worktree 0 error（10 文件 +1133）；移植主仓构建 0 error。默认全部关闭，零行为变化。

## 1. 集标题美化 BeautifyMissingMetadata
- 显示层 Harmony：DTO 层把「名称==文件名」的剧集显示为 `第 N 集`（仅 zh-CN），
  附加部分显示「下部分/第 N 部分」；`NamingOptions.GetMainExpression` postfix 防
  纯 S01E01 文件名被当标题。
- 补全任务 `BeautifyMissingMetadataTask`：按库范围遍历有 TMDB id 的剧，
  MetadataApi 拉 TMDB 全季集列表（6h 缓存+限速），只填空缺/文件名/纯编号/默认名，
  锁定字段不碰；支持本地剧集组映射；批量落库 + 统计日志。
  无默认触发器，需在计划任务里手动加。

## 2. 缺失集 TMDB 补全 EnhanceMissingEpisodes
- 反编译确认 Emby `/Shows/Missing` 走 ProviderManager 第一个 ISeriesMetadataProvider
  的 GetAllEpisodes 生成虚拟 DTO → 实现为替换/注入插件版 `MovieDbSeriesProvider`
  （Postfix GetEnabledMetadataProviders），返回 TMDB 全集列表（含未播出集带真实日期）。
- 支持本地剧集组的本地编号输出（TryGetLocalEpisodes）；镜像/Alt 源自动跟随；
  缓存 6h。副作用：开启后 Upcoming 视图同源（预期）。

## 3. 禁止自动建合集库 NoBoxsetsAutoCreation
- Prefix `CollectionManager.EnsureLibraryFolder()` → false（自动创建/重建合集库唯一入口）；
- Postfix `UserViewManager.GetUserViews` 隐藏 boxsets 类型库视图（手动建的不受影响，
  库管理页不受影响）。开→Patch / 关→Unpatch 动态装卸。

## 移植
新文件 6 个 + MetadataApi/ExperienceEnhanceOptions 整份拷贝（主仓原本未改）；
`LocalEpisodeGroup.cs` 与 batch-5 版本合并（TryGetLocalEpisodes + Entry 类）；
`Plugin.cs` 7 处 hunk 合并（字段/初始化/可选 patch 装载/清洗/日志/保存切换/库列表）。

## 部署实测
- 构建 0 error；备份 `.bak-before-batch5-6`；重启后插件正常加载（MovieDb providers
  patched 序列正常），无新增异常（重启瞬间的 TaskManager ObjectDisposedException 为
  旧任务被打断的关机噪音）。指纹任务重触发 6,974 集继续跑。
- 三个新功能默认关，日志无痕（按设计）；启用后才有对应 patch/任务。
- 备注：启动时的选项 "is set to" 日志块本次 boot 未出现（该块被上游条件包住，
  本次启动未走到；与本次改动无关，待后续排查）。
