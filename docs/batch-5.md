# Batch 5 — TMDB 剧集组刮削（2026-11-09）

hermesh 派发（agent claude，session `s_75cglx6f_01m6ysgavp5kcjhdh95hhn0qpy`），
worktree 全量编译 0 error；移植进主仓后构建同样 0 error。

## 改动（10 文件 +1088 −100）
- 新增 `Common/EpisodeGroupDocument.cs`：本地/远程共用的剧集组文档模型
  （`groups[].order`=媒体库季号，`episodes[].order`=组内零基位）。
- 新增 `Common/RemoteEpisodeGroup.cs`（865 行）：TMDB 远程剧集组数据源——
  按系列拉取 /3/tv/{id}/episode_groups 选组（type 分级：播出/DVD/数字/TV 优先，
  绝对序号次之，故事线/制作最后）→ 拉组详情 → 规范化为本地约定 →
  内存 7 天 + 磁盘 DataFolderPath/TmdbEpisodeGroupCache/{seriesId}.json
  （tmp 写入原子 move，Version+SourceKey 校验，切镜像自动失效）+
  MovieDbCache 原始响应缓存；每系列 SemaphoreSlim 串行化；失败 10 分钟冷却；
  库范围负缓存；对外暴露 TryMapRequest / TryGetTmdbNumbers（与本地源同构）。
- `LocalEpisodeGroup.cs`：两个入口改为「本地 → 远程」两级查找，换算逻辑抽成共享实现。
- `MovieDbApi.cs`：`MovieDbHttpClient.SendAsync` 加预取钩子（仅 GET + 开关开启时），
  请求 TMDB provider 前先让远程组就绪。
- `Options/MetadataEnhanceOptions.cs`：开关 `MovieDbEpisodeGroup`（默认 false）+
  `EpisodeGroupLibraryScope` 库范围 + 隐藏 LibraryList。
- `Plugin.cs`：Initialize、OnBeforeShowUI 填库列表、OnOptionsSaving 清洗、启动日志。
- 资源：新增 2 个键（范围名/说明），三语齐（en/zh/zh-hant）+ Designer。

## 数据流与降级
`TMDB GET → SendAsync 预取 RemoteEpisodeGroup → Prepare() 改写 season/episode 号 →
响应 PostProcessAsync 还原为媒体库季/集号 → OverrideImageService 反向映射`。
降级全部记日志不静默：无 API key / 非 2xx / 超时 / 空组 / 组详情失败 → Warning + 冷却 + 走原生；
系列不在所选库 → 不请求；单条 URL 查不到组 → Debug 沿用原生。
默认关闭时只多一次布尔判断，零 HTTP、零磁盘。

## 移植方式
新文件与主仓未改动文件（LocalEpisodeGroup/MovieDbApi/Resources*）整份从 worktree 拷贝；
`MetadataEnhanceOptions.cs`、`Plugin.cs` 手工合并（主仓含 batch 4 与 Mac WIP）。
构建 0 error（1 条既有警告）。**尚未部署**——与 batch 6 一起部署，减少打断指纹任务。

## 后续待办
- 生产启用前需在 UI 打开开关并选库，观察 Info 日志 `group id/name/type` 与命中率；
- 缺 UI 选组入口（当前是最小可用：按 type 自动选第一个组），迭代项。
