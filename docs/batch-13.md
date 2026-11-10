# batch-13：播放断点按版本维度记录（1.0.7）

## 背景

README 已知限制第 1 条：Emby 原生断点按条目存（`UserData.PlaybackPositionTicks`），同片多版本共用一条；插件原有的 `VersionPlaybackApi` 在切版本瞬间按比例换算，各客户端上报的 `PositionTicks` 含义不完全一致（含片头、转码时长），会出现「偶有偏移」。任务书 `hermesh-batch13-per-version-resume.txt`。

## 代码改动（经 hermesh 派 claude 完成，diff 移植进主仓）

- `Common/VersionPlaybackApi.cs`
  - a. `MediaSourceId` 精确命中即原样返回，不换算；
  - b. 未命中时在同内容组（`ItemKey`/`NameKey`）内取最近一条未看完、且不属于当前任何版本的记录，按 `position × 目标时长 / 源时长` 换算一次并落盘为目标版本独立记录；
  - c. `Played` 以组内最新状态为准，跨版本共享；
  - d. 旧记录（无 `MediaSourceId` / 无 `ItemId`）作为通配读入，迁移时补齐字段并 `Persist(true)`，不丢进度。
  - 关闭开关时走 `GetLegacyPlayback`，方法体与原 `GetVersionPlayback` 逐行一致。
- `Options/VersionPlaybackOptions.cs`（新增）：`PersistPerVersionResume`，**默认 false**；挂在「体验增强」下，设置页分组「多版本续播」。
- 三语言 resx + `Resources.Designer.cs` 同步新增分组名/开关名/说明。
- 存储仍是同一个 `StrmAssistant.VersionPlayback.json`；记录新增 `ItemId`、`RunTimeTicks`、`VersionKey`、`VersionSignature` 字段，旧文件可直接读。

## 验证

- 插件 `dotnet build -c Release`：0 error（1 个既有 CS0618 警告）。
- 见 `docs/batch-14.md` 的 63 条自动化用例（含换算数学、阶梯退避、DvP7 判定）。
- **未做真机验收**：Web / 手机 / TV 客户端切版本 A→B→A 的实际断点位置、Debug 日志、旧 JSON 迁移、多用户并发写盘都还没在真实客户端上跑过。开关默认关闭，属于「先合入、按需开启、真机验完再建议开」。

## 已知风险

- `OnPlaybackProgress` 里 perVersion 键用 `e.MediaSourceId ?? e.MediaSource?.Id`，而读取路径统一用 `MediaSourceInfo.Id`；若某客户端两者不一致，会出现「记录键不命中 → 退回换算」的退化（不会丢进度，但会多一条记录）。真机验收时重点看 `VersionPlayback - resume hit/miss/converted` 三条 Debug 日志。
- 换算只在切换瞬间做一次，之后以目标版本自己的记录为准；如果目标版本记录被外部（原生播放）改写，以 Emby 原生为准。
