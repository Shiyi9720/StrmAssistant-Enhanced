# Batch 1 — 片头指纹失效诊断与打点（2026-11-09）

分支 `port/emby-4.10-stable`，HEAD 起点 `3373516`。

## 诊断结论（全部有实测证据）

### 「14 季 762 集只花 2 秒、每季 0 条指纹」的根因链

1. **进队列的集是「收藏扩展」的产物，不是过滤结果。**
   全库收藏只有 2 条剧级收藏（`UserDatas.isFavorite=1`，type=6）：
   凡人修仙传 (2020)、斗破苍穹 (2017)，共 2 用户行。
   `FetchIntroFingerprintTaskItems` 中 `libraryIds.All(i => i == "-1")` 对**空数组空真**，
   当时运行中的 `MarkerEnabledLibraryScope` 为空 → 静默走进收藏分支
   `ParentIds = GetAllFavoriteSeasons()` → 只扫到这 2 部剧的 475 集（+1 集经扩展关联，熊出没 S01E48）。
   按 2 条重复 item 行算正好 951 = 475×2+1；762 = 381×2。
   这一步把「电视剧 4827 集全没跑」「动漫剧集只扫 3 部剧」一并解释。
2. **2 秒扫完**：近两轮只有 3 次 ffmpeg 进程（全部熊出没 S01E48）。
   已有 `.fp` 的集，反射调 Emby `CreateTitleFingerprint` 时目标文件已存在直接返回，不跑 ffmpeg。
3. **每季 0 条指纹**：Emby 核心 `AudioFingerprintManager.IsIntroDetectionSupported()`
   开头 `if (item.IsShortcut) return false;`，`IsShortcut` = Path 以 `.strm` 结尾（`BaseItem.cs:1343`）。
   strm 库下季指纹统计恒 0，与磁盘上 475 个 `.fp` 无关。
4. **熊出没 S01E48 必败**：该 115 源 mp4 只有视频流（ffprobe 仅 `0,video`），无音轨，
   `-vn` 后 chromaprint 无输入 → `Output file #0 does not contain any stream`。
5. **`Local: 0` 口径**：`DistributionStats.Local` 只在分布式管线的本机兜底分支打点；
   `TryCreateTitleFingerprintAsync` 在 `EnableDistributedFingerprint=false` 时直接 return null，
   指纹路径连打点都不进 → 恒 0，属日志口径缺陷。
6. 用户给的四个候选过滤条件逐条库内实测：WithoutChapterMarkers 排 189 集（凡人 S01 有 IntroStart）、
   MinRunTimeTicks 排 ~336、HasIntroDetectionFailure=0 条记录（ItemExtradata 仅 7 行库选项）、
   HasAudioStream(StreamType=1=Audio) 通过 12,240 —— 四条叠加仍 11,153 集，均非真实筛选项。

其他实测：`MediaStreamType` 枚举 Unknown=0, **Audio=1**, Video=2；
`VirtualFolderInfo.Id = GetClientId()`（本机为数字内部 id 字符串），
`GetValidLibraryIds` 把数字 id 转回内部 id 字符串，二者可匹配 —— 现盘配置
`MarkerEnabledLibraryScope=163842,197506` 本可正常命中（待下次运行验证）。

### .fp ↔ 条目映射
`.fp` 目录名 = GUID 字节序变体 `tofolder(b)=b[3::-1]+b[5:3:-1]+b[7:5:-1]+b[8:]`；
475 个 fp 目录 ↔ 475 集（斗破 277 + 凡人 198），一目录对一集。

## 本批代码改动（未 commit，文件携带 Mac 端 WIP，见下）

`StrmAssistant/Common/FingerprintApi.cs`：
1. **修复**：`FetchIntroFingerprintTaskItems` 中 `libraryIds.All(i => i == "-1")` 改为
   `libraryIds.Length != 0 && libraryIds.All(...)` —— 库范围为空时不再静默落入收藏分支。
2. **打点**：
   - `MarkerEnabledLibraryScope(raw)` / `LibraryPathsInScope(N)` 原样打印；
   - 收藏分支打印 `FavoriteSeasonParentIds: N`；
   - `FetchIntroFingerprintTaskItems` / `FetchIntroPreExtractTaskItems` 各阶段计数
     （query → modOrNonShortcut → blacklist → runtime）与 `isModSupported` 状态。

## 构建与验证（只写实跑结果）

- `dotnet build StrmAssistant/StrmAssistant.csproj -c Release`（sudo -u Shiyi，dotnet 8.0.427）：
  **0 error**，1 条既有 `BaseItem.IsFolder` 过时警告（LibraryApi.cs:1131）。
- 运行时验证待做：需把新 dll 部署到 Emby 后跑一次 `IntroFingerprintExtractTask`，
  看新日志行确认 Stage counts 与收藏分支行为。部署动作未做（规矩：不部署生产 Emby）。

## 提交状态

- 本批代码改动与 Mac 端 17 个未提交改动同文件（FingerprintApi.cs 本就在改动列表里），
  按「不动 Mac WIP」的规矩，**代码未 commit**，与 Mac WIP 一起留在工作区。
- 本文件为 batch 1 的 docs 收尾提交。

## 下一步（待批准）

- P0-c：让 Emby 对 `.strm` 放行指纹判定（PatchManager hook `IsIntroDetectionSupported`），
  或绕开季分析直接由插件写 Chapters3 标记 —— 影响面最大，需先在验证环境跑通。
- P1-a：提取失败/无音轨写失败标记避免每轮重试；P1-b：按 InternalId 去重（库里 3707 路径 7414 行）。
