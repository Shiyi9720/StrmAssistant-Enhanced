# batch-16：收尾修复两个已知缺陷（ffprobe 退出码 / 同条目多源换算）

## 范围

本轮只修两个已定位缺陷，不发布、不部署、不重启 Emby；`PersistPerVersionResume` 默认值仍为
`false`，UI 资源键未动。

- 缺陷 A：`CustomFfprobeApi.RunProbeJsonAsync` 丢弃 ffprobe 退出码，探测失败被静默当成「不是 P7」。
- 缺陷 B：同一 Item 挂多个 MediaSource（Emby 合并版本）时，次要版本读不到换算记录。

## 缺陷 A：ffprobe 退出码被丢弃

### 现象

真实 ffprobe（静态 9.x）在输入不存在/不可读时**退出码非 0，但仍会向 stdout 打印 `{}`**。
`RunProbeJsonAsync` 只取 stdout，于是返回 `"{}"`；`DvP7Api.DetectAsync` 反序列化出空文档，
`Evaluate` 得到 `IsDvP7=false`。调用方（`DiscMediaInfoApi`）无法区分：

- 探测失败（磁盘/网络/权限问题）；
- 探测成功且确实不是 Profile 7。

两种情况都不会改 Emby 的结果，但日志与诊断被误导，失败也无法被上层感知。

### 根因

`FfmpegApi.RunAndCaptureAsync` 只返回输出文本。Emby 的 runner 其实通过
`Emby.Model.ProcessRun.IProcessRunner.LastExitCode` 暴露退出码（`FfmpegApi.RunAsync` 早已在用它），
但 `RunAndCaptureAsync` 没有读取。

### 修法

- `Common/FfmpegApi.cs`
  - 新增 `RunAndCaptureWithExitCodeAsync(...) -> RunResult(Output, ExitCode)`，语义与
    `RunAndCaptureAsync` 完全一致（启动失败、runner 为空、超时、取消、异常都返回
    `RunResult.Empty`，即 `Output=null, ExitCode=null`）。
  - `RunAndCaptureAsync` 改为调用新方法并只返回 `Output`，**所有原有调用方
    （`MediaInfoApi`、`DiscMediaInfoApi`、`DistributedExtractApi`、`ValidateAsync` 等）行为不变**。
  - 退出码通过 `ReadExitCode(runner)` 读取，读取异常时退化为 `null`，不阻断探测。
- `Common/CustomFfprobeApi.cs` — `RunProbeJsonAsync` 判定：
  - 退出码非 0，且裁剪出的 JSON 既不含 `"streams"` 也不含 `"stream_groups"`
    （或根本没有 JSON 对象）→ 返回 `null`，并 `Debug` 记录 `exitCode` 与 `outputArguments`；
  - 退出码非 0，但输出里确有 `streams` / `stream_groups` → 照常返回该 JSON（不误杀）；
  - 退出码为 0 → 原样返回裁剪后的 JSON，**包括 `{}`**（保持 D 组既有语义）；
  - 退出码未知（`null`）按 0 处理，兼容不填 `LastExitCode` 的 runner 实现。
- `DvP7Api.DetectAsync` 无需改动：`RunProbeJsonAsync` 返回 `null` 时它直接返回 `null`，
  与「解析成功但非 P7」区分开。

### 测试覆盖

`tests/StrmAssistant.Verification`：

- 新增 `Tests/ProbeExitCodeTests.cs`（组 H，用 shell wrapper 精确控制退出码与输出，不依赖真实 ffprobe）：
  - H1 退出码 1 + `{}` → `null`；
  - H2 退出码 0 + 合法 JSON → 原样返回；
  - H3 退出码 0 + `{}` → `{}`；
  - H4 退出码 1 + 含 `streams`/`stream_groups` → 返回该 JSON。
- `Tests/DvP7DetectTests.cs` 更新既有 D2/D3 断言：缺失输入、空文件现在必须返回 `null`
  （此前只要求「null 或 `{}`」），并断言 `DetectAsync` 对探测失败返回 `null`。

### 未覆盖

- 真实 Emby runner 的 `LastExitCode` 时序（本机测试用 `System.Diagnostics.Process` 复刻）；
  但它与 `RunAsync` 已使用的属性相同，风险低。
- 真实 ffprobe「退出码非 0 但输出含有效 streams」的组合在现实中少见，H4 由 wrapper 构造。

## 缺陷 B：同一 Item 多 MediaSource 读不到换算记录

### 现象

同一部影片在 Emby 里是**一个 Item 挂多个 MediaSource**（合并版本）时，主版本有播放记录，
读取次要版本却没有插件条目，客户端只能回退 Emby 原生断点。跨条目（同片被拆成两条）场景正常。

### 根因

`VersionPlaybackApi.GetPerVersionPlayback` 的换算候选条件：

```csharp
!sources.Any(s => IsSameVersion(r, s) || IsLooseVersionMatch(r, s))
```

本意是「不使用属于当前任何版本的记录」，避免把版本 A 的记录换算给版本 B。但合并多源时，
主源记录确实是当前 Item 的一个 MediaSource 的记录，于是被整体排除；而目标源自己没有记录，
结果既没有精确命中，也没有换算来源。

### 修法

`Common/VersionPlaybackApi.cs`：

- 候选条件改为「**不是目标源自己的记录**」即可：

  ```csharp
  !IsSameVersion(r, source) && !IsLooseVersionMatch(r, source)
  ```

  记录被精确解析阶段放进 `used` 只表示「已匹配给某个源」，不再禁止它作为别的源的换算来源。
  因此：
  - 同一条目其它 MediaSource 的最近一条未看完记录可以作为来源；
  - 跨条目（`ItemKey`/`NameKey` 分组）逻辑不变：原本允许的「不属于当前任何源」的记录仍然允许，
    原本排除的「属于当前其它源」的记录现在也允许；
  - 每个目标源最多取一条来源记录（`FirstOrDefault`），`Played` 记录不参与换算，目标源自己的
    记录永远不会被当作来源。
- 换算后仍按 `PerVersionKey(userId, itemId, mediaSourceId)` 落盘为目标源自己的记录；再次读取
  即为精确命中，不会重复换算或重复写记录。
- 修正注释：`sharedPlayed` 的说明改为「**没有自己记录**的版本才会共享已看完状态」；
  方法头注释补充「来源可以是同组其它条目，也可以是同一合并条目的其它 MediaSource」。

### 测试覆盖

`Tests/ResumeSequenceTests.cs`（真实 `VersionPlaybackApi` + 真实 `ResumePositionMath`）：

- G1 同一条目两源，仅主源有记录（24/60min）→ 次源返回 32min（80min 的 40%），并落盘为次源
  自己的记录；再次读取不变。
- G2 次源已有 10min 记录 → 精确命中，保持 10min，不换算、不被主源覆盖。
- G3 同一条目三源，仅主源有记录 → 次源 32min、第三源 48min，各自落盘为独立记录；第二遍读取
  两源都是精确命中。
- G4 主源看完 → 无记录次源报告 `Played=true, PositionTicks=0`。
- G5 跨条目行为不回归：F1/F2/F3 原样保留并全部通过。
- 原 F4（「合并多源不换算」的现状固化用例）由 G1 取代，不再断言旧限制。

### 未覆盖

- 真机 Emby：合并条目的 `MediaSources[].ItemId` 是否都指向同一 owning item、客户端
  （Web/手机/TV）实际起播位置，仍需按 batch-15 的方式验收。
- 旧格式（无 `MediaSourceId`）的通配记录在同一合并条目里的首读行为：仍按原逻辑先迁移给第一个
  匹配的源，下一次读取才可能被其它源用作换算来源；本轮未改变该行为，也未新增用例。
- 多用户并发写盘、旧 JSON 迁移未在本轮新增覆盖。

## 验证

```
dotnet build StrmAssistant/StrmAssistant.csproj -c Release \
  -p:EmbyServerDir=/vol3/1000/Projects/StrmAssistant/lib/emby
→ Build succeeded. 0 Error(s)（1 个既有 CS0618 警告）

dotnet run --project tests/StrmAssistant.Verification -c Release
→ SUMMARY: 183 passed, 0 failed, 0 skipped
```

（batch-15 基线 144 条；本轮新增 H 组 12 条、G 组 29 条，原 F4 的 2 条旧限制固化断言由 G1
取代，并调整 D2/D3 的断言，合计 183 条。）
