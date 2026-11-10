# batch-14：可重复运行的验证工程（1.0.7）

## 目的

batch-12（第三方源阶梯退避）与 batch-13（按版本存断点）此前只有构建验证 + 代码审阅，没有自动化回归。本轮补一套能在本机反复跑的验证工程，并顺手把最危险的换算数学抽成可测的纯函数。

## 新增

- `StrmAssistant/Common/ResumePositionMath.cs`：把 `VersionPlaybackApi.ConvertPosition` 原名原签名搬出来（负值归零、AwayFromZero 舍入、clamp 到目标时长、任一时长为 0 时原样返回）。`VersionPlaybackApi` 只改调用点，行为不变。
- `tests/StrmAssistant.Verification/`：net8.0 console 工程，**不进 `StrmAssistant.sln`**，不影响插件 Release 构建；用 `<Compile Include>` 链接 `SourceBackoff.cs`、`DvP7Api.cs`、`ResumePositionMath.cs` 等真实源文件（不复制副本）。
  - `src/Shims.cs`：仅为编译所需的 `ILogger`、`Plugin.Instance`、`PluginOptions/MediaInfoExtractOptions`、`CustomFfprobeApi` 最小 shim。
  - `fixtures/p7-single-track.json`：《流浪地球2》2160p BluRay DV.HDR REMUX 的真实 ffprobe 输出（单轨 HEVC，`dv_profile=7`、`el_present_flag=1`、`bl_present_flag=1`）。
  - `fixtures/dv-profile8.json`：《火遮眼》2160p WEB-DL DV.HDR 的真实输出（`dv_profile=8`、`el_present_flag=0`）。

## 覆盖

| 组 | 内容 | 条数 |
|---|---|---|
| A | SourceBackoff 阶梯 10m→30m→2h→6h（第 5 次仍 6h）、恢复清零、空 key 惰性、key 互不干扰 | 21 |
| B | DvP7 Evaluate：真实 P7 单轨样本、真实 P8 样本、合成 stream_group 的 MEL/FEL/Unknown 三档、无 DOVI 普通 HEVC、null 文档 | 27 |
| C | ResumePositionMath：比例换算、clamp、时长为 0、非正数、AwayFromZero | 15 |

结果：**63 passed / 0 failed**（本机以 Shiyi 身份实跑，退出码 0，连续两次一致）。

## 本轮顺带确认的事实（此前未记录）

- 库里主流 DV P7 是**单轨 MKV REMUX**（如《流浪地球2》），Emby 4.10 自己就把 `ExtendedVideoSubType` 标成 `13076`（DoviProfile76），前端脚本会显示 Profile 7。插件自己的 `DvP7Api` 只在**碟片（ISO/BDMV）提取路径**被调用，所以单轨 MKV 不会经过插件的 MEL/FEL 判定——单轨本身也拿不到 EL 码率，属于设计使然，不是缺陷。
- 库里 6 个 BDMV 目录（前目的地、碟中谍 1-4、阿凡达）实测全是 1080p H.264，没有 UHD 双轨 P7 碟，**MEL/FEL 真机样本仍然缺失**，该分支只有合成 JSON 覆盖。

## 仍未覆盖

- `DvP7Api.DetectAsync` 全链路（真起静态 ffprobe、真实 stream_groups 输出、异常分支）。
- 真实 UHD 碟的 packet-sample 兜底（`-read_intervals %+#300`）。
- 豆瓣 / IntroDB 真实 403/429 下的退避与恢复（IntroDB 当前 `EnableIntroDb=false`，豆瓣近期未触发 403）。
- 多版本续播的客户端真机行为（Web / 手机 / TV）。
