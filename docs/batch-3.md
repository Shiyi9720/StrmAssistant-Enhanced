# Batch 3 — P1：失败标记 + 队列去重（2026-11-09）

编码由 hermesh 派发（agent claude，session `s_75cglx6f_01m6y3t4e6d8kagr03cznrmb03`，
2 files +49 -4，worktree 构建 0 error），diff 移植进主仓后构建部署。

## 改动
- `FingerprintApi.cs`：
  - 新增 `LogIntroDetectionFailure(Episode)`：`_itemRepository.LogIntroDetectionFailureFailure(
    InternalId, DateModified.ToUnixTimeSeconds())`，与 Emby `OnFailedToFindIntro` 同语义；
    写入失败单独 catch 不掩盖原始异常；成功打 Info 日志。
  - 两处队列去重：`FetchIntroPreExtractTaskItems` / `FetchIntroFingerprintTaskItems`
    返回前 `GroupBy(i => i.Path, OrdinalIgnoreCase).Select(g => g.First())`。
  - `FetchIntroPreExtractTaskItems` 查询补 `HasIntroDetectionFailure = false`
    （hermesh 回报指出的缺口：无音轨的死源否则每轮重试；该记录只在
    CreateTitleFingerprint 真失败时写，媒体信息编排的瞬态失败不写，故安全）。
- `ExtractIntroFingerprintTask.cs`：`CreateTitleFingerprint` 调用处包内层
  try/catch：`OperationCanceledException` 原样抛（取消不记失败）；其他异常先记
  failure 再原样抛，外层 "Episode failed" 日志不变。

## 移植期修正（重要教训）
- claude 按 `GroupBy(i => i.InternalId)` 去重 —— **部署实测无效**（11855→11855）：
  重复行是同 Path 不同 Id（MediaItems 双行，InternalId 不同）。改为按
  `Path (OrdinalIgnoreCase)` 分组后生效。
- 注意 `FetchFingerprintQueueItems` 既有的一处 InternalId GroupBy 未动（口径待后续统一）。

## 部署与实测
- 构建 0 error（1 条既有警告）；部署备份 `StrmAssistantLite.dll.bak-before-p1`。
- 实测：`Stage counts: query=11855 … runtime=8240`；`Number of episodes: 8240`、
  `Number of seasons: 419`（此前 12,155/564）。去重把动漫库的同路径双行砍掉。
- 失败标记日志 `Intro Detection Failure Recorded` 待任务扫到熊出没 S01E48 出现。

## 语义代价（与 Emby 同语义，已知）
- post-extract 集一旦失败即被三处查询排除（主任务/季分析/catchup），直到
  DateModified 变化或 SaveChapters 清记录；瞬态网络故障会误伤（与 Emby 一致）。
- 缓解：任务取消不记失败；只包 CreateTitleFingerprint 一处。
