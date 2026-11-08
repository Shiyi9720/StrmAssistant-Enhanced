# Batch 2 — P0-c：让 Emby 片头管线接受 .strm（2026-11-09）

本批编码由 hermesh 派发（client Hermes-Nas → machine ShiyiFnOS，agent claude，
session `s_75cglx6f_01m6y153s988wmrz337nn3s3wk`），diff 移植进主仓后构建部署。

## 改动（hermesh worktree 产出，已移植到主仓）
- 新增 `StrmAssistant/Mod/IntroDetectionForShortcut.cs`：Harmony prefix patch
  `AudioFingerprintManager.IsIntroDetectionSupported(Episode, LibraryOptions)`。
  仅当 item.IsShortcut 且 Path 命中 `FingerprintApi.LibraryPathsInScope` 时介入；
  介入时只绕过 IsShortcut 一条门，按原顺序重放其余门（chromaprint/IsFileProtocol/
  IsPlaceHolder/RunTimeTicks/IsCompleteMedia/指纹时长），其余情况交给原方法。
- `StrmAssistant/Mod/PatchManager.cs`：注册 `IntroDetectionForShortcut`，
  实例化顺序在 ExclusiveExtract 之前（不依赖 ffprobe patch，失败只记 warning）。

## 反编译确认的链路（claude 产出）
- IsIntroDetectionSupported 仅 2 个调用点：GetAllFingerprintFilesForSeason（季指纹统计）
  与 MarkerDetector.IsIntroDetectionEnabled（Emby 自带 Detect Episode Intros 任务）。
- 原门顺序：chromaprint → IsFileProtocol → IsPlaceHolder → IsShortcut → RunTimeTicks
  → IsCompleteMedia → libraryOptions → 时长 ≥ 指纹分钟数。
- Emby.Providers 其他 5 处 IsShortcut（图片/媒体信息/字幕）不在标记链路；
  Chapters3 读写无 shortcut 过滤。

## 次级阻点（已记录未改）
1. 高：HasAudioStream/MinRunTimeTicks 前置过滤——需媒体信息已持久化（本库已满足）。
2. 中：UpdateSequencesForSeason 要求季内 ≥2 条指纹（单集季永远无标记，如熊出没之外的单集剧）。
3. 中：历史失败记录需先清标记才重新检测（SaveChapters 会清）。
4. 配置门：EnableMarkerDetection 开、Detect Episode Intros 24h 触发。

## 部署与实测验证
- 主仓构建（batch1 + Mac WIP + P0-c）：**0 error**，1 条既有警告。
- 部署 → 重启 emby（备份 `StrmAssistantLite.dll.bak-before-p0c`）→ 重触发指纹任务。
- **验证（真实日志）**：
  - `熊出没之小小世界 S01 has 51 episodes with audio fingerprints available`（此前恒 0）
  - `禁忌女孩 S02 has 8`、`S01 has 13`、`假钞 S01 has 8` 等，564 季逐季进行
  - `Season Progress 1/564…` 正常推进；`Season failed` = 0
  - Chapters3 实测：禁忌女孩 S02E01 intro@70.3s、E02@86.6s、E03@55.2s —— 真实 OP 位置
    （此前是 0→0.2s 垃圾标记；凡人 S01 旧 189 条被重算清理中，IntroStart 现总数 137）
- 全库指纹提取重新开始跑（12,155 集，~24h），标记随季分析逐季写入。

## 流程备注
- 本批起编码一律走 hermesh（会话在独立 worktree，diff 用 `hermesh session diff` 导出，
  手工移植进主仓再构建——worktree 不含 Mac WIP 和未提交改动，直接部署会回退）。
- hermesh state.json 属主被修为 trim.hermes（此前被 Shiyi 侧进程覆盖，agent 读不了）。
- 代码仍未 commit（与 Mac WIP 同仓纠缠）；本文件为 batch 2 docs 收尾提交。
