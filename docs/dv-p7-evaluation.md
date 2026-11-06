# DV P7 双层原盘检测 · 可行性评估

- 日期：2026-11-06
- 分支：`hermesh/s_pm7f7civ_01m6r89n8t2w42bsj85mkmyr7d`
- 结论：**做得到，但只能走插件自带的静态 ffprobe 9.0.2；只靠 Emby 4.10.2 自带的探测链做不到。**
- 本批只提交评估文档，不新增功能代码。

## 一、结论摘要

Wiki 对这条 Pro 功能的描述是：

> [PRO]支持检测双层杜比P7原盘，正确显示增强层媒体信息

它可以拆成两件事：

1. **检测**：判断 ISO / BDMV 原盘的主视频是不是 DV Profile 7 双层（BL + EL + RPU）。
2. **展示**：把增强层（EL）的信息显示出来，而不是把它当成第二条普通视频流，或者直接忽略。

结论：

- **检测做得到。** 真实 UHD BD 原盘的 EL 没有 DOVI descriptor，Emby 和旧版 ffprobe 看不到任何 DV 标记；FFmpeg 9.0 会按 `HDMV` 注册描述符 + `PID 0x1011/0x1015` 建一个
  `Dolby Vision (Split base and enhancement layer)` 流组，用 `-show_stream_groups` 可以看到 BL 与 EL 的配对关系。插件已经在下载并使用同一份 9.0.2 静态 ffprobe。
- **只靠 Emby 的 `MediaStream` 做不到。** `MediaStream` 没有层字段；Emby 的 `ProbeSideDataDovi` 虽然解析了
  `el_present_flag` / `bl_present_flag` / `rpu_present_flag`，但 `ProbeStreamParser.ParseVideoProperties` 只取
  `dv_profile` 和 `dv_bl_signal_compatibility_id` 计算 `ExtendedVideoSubType`，其余三个标志直接丢弃；
  真实 UHD BD 原盘连 side data 都不会有，因为 Emby 自带的 ffprobe 是 5.1，没有 BDMV 双 PID 检测。
- **规模不大。** 约 300–450 行生产代码：1 个新文件（检测器）+ 2 个现有文件（`CustomFfprobeApi.cs`、
  `DiscMediaInfoApi.cs`）。不需要 Harmony 补丁；如果检测器保持为静态工具、不注入新构造参数，
  `Plugin.cs` 也不需要改，否则只需在 `Plugin.cs:270` 加一行构造参数。只有需要本地化 EL 标签时才动 3 个 resx + Designer。

## 二、核查范围与方法

本次只做只读核查，没有改动任何功能代码：

1. **Emby 4.10.2 生产程序集**：`MediaBrowser.Model.dll`、`Emby.Media.Model.dll`、
   `Emby.Server.MediaEncoding.dll`、`Emby.Providers.dll`，用 Mono.Cecil 读类型字段、枚举值和 IL。
2. **Emby 4.10.2 官方安装包**：`emby-server-deb_4.10.2.0_amd64.deb` 与 `arm64.deb`，
   直接读取包内的 `ffprobe` 与 `libavutil/libavformat/libavcodec`，确认 Emby 自带 ffprobe 的真实版本。
3. **FFmpeg 公开源码与测试**：`n5.1` 与 `n9.0.2` 的 `libavformat/mpegts.c`、`fftools/ffprobe.c`、
   `libavutil/dovi_meta.h`，以及 FFmpeg FATE 的测试定义和参考输出。
4. **真实公开样例**：FFmpeg FATE 套件里的 `mkv/dovi-p7-hvce.mkv`、`mpegts/dovi-p7.ts`，
   用插件同款 9.0.2 静态 ffprobe 实际跑过并把输出记录在附录。
5. **上游公开资料**：`sjtuross/StrmAssistant` 公开仓库、公开 Wiki、`StrmAssistant.Releases` 发布仓库。
   **没有反编译 Pro，没有使用 Pro 的 DLL，也没有抄 Pro 代码。**

## 三、Emby 4.10.2 链路逐项核对

### 3.1 `MediaStream` 有哪些字段

`MediaBrowser.Model.Entities.MediaStream` 中与视频/杜比视界相关的成员：

| 成员 | 类型 | 说明 |
| --- | --- | --- |
| `Profile` | `string` | 编码 profile，例如 `Main 10`；不是 DV profile |
| `VideoRange` | `string` | `SDR` / `HDR` |
| `ColorTransfer` / `ColorPrimaries` / `ColorSpace` | `string` | 色彩信息 |
| `ExtendedVideoType` | `ExtendedVideoTypes` | `None/Hdr10/Hdr10Plus/HyperLogGamma/DolbyVision(130)` |
| `ExtendedVideoSubType` | `ExtendedVideoSubTypes` | 例如 `DoviProfile76(13076)` |
| `ExtendedVideoSubTypeDescription` | `string` | 由枚举的 `Description` 特性生成，只读 |
| `Title` / `DisplayTitle` | `string` | 可用于给 EL 流做展示标签 |

**没有任何** `el_present` / `bl_present` / `rpu_present` / `layer` / `enhancement` 字段。
Emby 的数据模型里不存在“这一条视频流是 BL 还是 EL”的位置。

### 3.2 `ProbeResult` / `ProbeSideDataDovi`

`Emby.Media.Model.ProbeModel.ProbeResult` 的字段只有：

```
streams, format, frames, packets, chapters, error
```

**没有 `stream_groups`。** 这与 Emby 的 ffprobe 调用参数一致：它从不请求 `-show_stream_groups`。

`ProbeSideDataDovi` 则完整解析了 ffprobe 的 `DOVI configuration record`：

```
dv_version_major, dv_version_minor, dv_profile, dv_level,
rpu_present_flag, el_present_flag, bl_present_flag,
dv_bl_signal_compatibility_id
```

也就是说：**层信息在 probe model 这一层是存在的**，问题出在往下映射时被丢掉了。

### 3.3 `ParseVideoProperties` 实际用了哪些字段

`Emby.Media.Common.DataConversion.ProbeStreamParser.ParseVideoProperties` 的 IL 行为：

1. 从 `ProbeStream.side_data_list` 中取第一条 `ProbeSideDataDovi`；
2. 如果有：
   ```csharp
   var subtype = 13000 + dovi.dv_profile * 10 + dovi.dv_bl_signal_compatibility_id;
   targetStream.ExtendedVideoSubtype = (ExtendedVideoSubTypes)subtype;
   targetStream.ExtendedVideoType = ExtendedVideoTypes.DolbyVision; // 130
   return;
   ```
3. 它**只读** `dv_profile` 和 `dv_bl_signal_compatibility_id`；
   `el_present_flag`、`bl_present_flag`、`rpu_present_flag` 一次都没有读。

对 P7 蓝光（profile=7，compatibility id=6）算出来是
`13000 + 70 + 6 = 13076`，对应 `ExtendedVideoSubTypes.DoviProfile76`，
枚举描述是 `Profile 7.6 (Bluray)`，Details 是 `dvhe.07, FHD 1:1, UHD 1:1.25`。

这说明 Emby **能表示“Profile 7”这个 profile**，但这个数值不携带“有没有 EL”的信息：
`DoviProfile76` 只说明 profile 7 + compatibility id 6，无法据此判断 EL 是否存在。

### 3.4 Emby 的 ffprobe 命令与版本

`Emby.Server.MediaEncoding.dll` 里的探测命令字面量：

```
-hide_banner -i "{0}" -show_format -show_streams -show_frames -print_format json {1}
```

没有 `-show_stream_groups`。

Emby 4.10.2 官方 deb 内的实际版本（amd64 与 arm64 相同）：

```
FFmpeg version 5.1-emby_2023_06_25_p6
libavformat 59.27.100
libavcodec  59.37.100
libavutil   57.28.100
```

这个 5.1 版本：

- **有** `0xb0 DOVI video stream descriptor` 解析（所以带 descriptor 的规范 TS 能看到 side data）；
- **没有** `AVStreamGroup` API，也没有 `-show_stream_groups`；
- **没有** FFmpeg 9.0 才加入的 BDMV 双 PID 流组检测。

### 3.5 小结：Emby 这一层能给什么

| 场景 | Emby `MediaStream` 能拿到 | 能否判断双层 P7 |
| --- | --- | --- |
| 单轨 P7 MKV/MP4（带 dvcC/hvcE） | `ExtendedVideoSubType = DoviProfile76` | 否，只有 profile，没有 EL 标志 |
| 带 DOVI descriptor 的规范 TS | EL 流得到 `DoviProfile76` | 否，EL 标志被丢弃，且标在 EL 流上 |
| 真实 UHD BD 原盘（EL 无 descriptor） | 两条普通 HEVC 视频流，无任何 DV 标记 | 否，完全没有信号 |

## 四、ffprobe 对 DV P7 的实际输出

以下输出都是用插件同款 `ffprobe 9.0.2`（`StrmAssistant.Releases/static-ffprobe`）在公开 FATE 样例上实测得到的，
不是推测值。

### 4.1 单轨 P7 MKV/MP4（remux，不是原盘）

样例：`fate-suite.ffmpeg.org/mkv/dovi-p7-hvce.mkv`（3840×2160，单条 HEVC 视频流）。

```json
"side_data_list": [
  {
    "side_data_type": "DOVI configuration record",
    "dv_version_major": 1,
    "dv_version_minor": 0,
    "dv_profile": 7,
    "dv_level": 6,
    "rpu_present_flag": 1,
    "el_present_flag": 1,
    "bl_present_flag": 1,
    "dv_bl_signal_compatibility_id": 6,
    "dv_md_compression": "none"
  },
  {
    "side_data_type": "HEVC enhancement-layer decoder configuration"
  }
]
```

信号很明确：`dv_profile=7`、`el_present_flag=1`、`bl_present_flag=1`、compat id 6。
但这是**单轨 remux**，不是本功能的“原盘”目标。

### 4.2 带 DOVI descriptor 的 TS

样例：`fate-suite.ffmpeg.org/mpegts/dovi-p7.ts`（10.5 KB）。

`ffprobe -show_streams` 的真实结构：

```
stream 0  id=0x1011  hevc  1920x1080  side_data: []
stream 1  id=0x1015  hevc  1920x1080  side_data: [DOVI configuration record]
```

EL 流上的 DOVI 记录：

```
dv_profile=7, dv_level=4,
rpu_present_flag=1, el_present_flag=1, bl_present_flag=0,
dv_bl_signal_compatibility_id=6, dv_md_compression=extended
```

再加 `-show_stream_groups`，还会多出一个流组：

```
type:        Dolby Vision (Split base and enhancement layer)
nb_streams:  2
streams:     [0, 1]   // 0x1011 BL + 0x1015 EL
```

这是 Dolby 规范的 TS 信令方式：EL 的 PMT 里带 `DOVI_video_stream_descriptor`，
`stream_type` 是 0x06，descriptor 里给出 `dependency_pid`。

### 4.3 真实 UHD BD 原盘布局（EL 没有 descriptor）

UHD 蓝光盘不按 Dolby 的 TS 规范写：EL 的 PID 上**没有** DOVI descriptor，
`stream_type` 是 0x24（HEVC），用 HDMV 注册描述符，EL 固定使用 PID `0x1015`。
FFmpeg 9.0 的 `libavformat/mpegts.c` 专门处理了这种情况：

- `detect_bdmv_dovi_group()`：要求 `prog_reg_desc == "HDMV"`，并且找到
  `PID 0x1011`（BL）和 `PID 0x1015`（EL）两条 HEVC 流；
- 找到后创建 `AV_STREAM_GROUP_PARAMS_DOLBY_VISION` 流组，组里就是 BL + EL；
- 这条路径**不会**给 EL 流附任何 `DOVI configuration record` side data。

为了验证这条代码路径，我们把上面的 `mpegts/dovi-p7.ts` 只改了 PMT 信令：
EL 的 `stream_type` 从 0x06 改成 0x24，去掉 EL 的 DOVI descriptor，保留 HDMV 注册描述符。
这是一个**由真实 FATE 样例派生**的 UHD BD 布局测试文件，不是零售盘抓取。
`ffprobe 9.0.2` 的结果：

```
ffprobe -show_streams：
  stream 0  id=0x1011  hevc  side_data: []
  stream 1  id=0x1015  hevc  side_data: []

ffprobe -show_stream_groups -show_streams：
  stream_groups:
    type:        Dolby Vision (Split base and enhancement layer)
    nb_streams:  2
    streams:     [0, 1]
```

关键点：**不带 `-show_stream_groups` 时，这个文件在 ffprobe 输出里完全没有 DV 信号**，
只能看到两条普通 HEVC 流。

### 4.4 版本门槛

- `detect_bdmv_dovi_group` 与 DOVI 流组支持来自 FFmpeg 提交
  `29bc8ec8d15493abf3bcbdea68b3046d150334e5`（`avformat/mpegts: create Dolby Vision stream group`，
  作者日期 2026-05-17），已包含在 `n9.0.2` 标签里。
- FFmpeg 5.1（Emby 4.10.2 自带）既没有 `AVStreamGroup`，也没有这段代码。
- 插件下载的静态 ffprobe 是 `9.0.2-a3f9c2b`，包含上述能力。

### 4.5 ffprobe 这一层的结论

| 文件形态 | 附加视频流 | side data | stream group | 可用信号 |
| --- | --- | --- | --- | --- |
| 单轨 P7 MKV/MP4 | 无 | 有，`el=1/bl=1` | 无 | DOVI 记录 |
| 规范 TS（EL 带 descriptor） | EL 0x1015 | EL 流上有 | 有 | DOVI 记录 + 流组 |
| 真实 UHD BD 布局 | EL 0x1015 | 无 | 有（仅 `-show_stream_groups`） | **只有流组** |

因此：**检测真实 UHD BD 原盘，唯一可靠信号是 FFmpeg 9.0 的 DV 流组。**
Emby 的探测参数和自带 ffprobe 都拿不到它；必须由插件自己再跑一次 9.0.2 静态 ffprobe。

## 五、上游公开资料怎么说

公开 Wiki `提取-ISO-媒体信息` 明确写了这条 Pro 功能：

> 3. [PRO]支持检测双层杜比P7原盘，正确显示增强层媒体信息

同一页的设置步骤要求用户从
`https://github.com/sjtuross/StrmAssistant.Releases/tree/main/static-ffprobe`
下载对应平台的静态 ffprobe，放到 Emby 配置目录的 `config/bin`，并在“媒体信息提取”设置里选中。
这说明 Pro 的 ISO/BDMV 提取链路本来就依赖这份自定义 ffprobe，而不是 Emby 自带的那一份。

公开仓库 `sjtuross/StrmAssistant` 是 Lite 版本，里面没有 ISO/BDMV 媒体信息提取和 DV P7 的 Pro 实现；
我们只读了公开仓库、公开 Wiki 和发布仓库，**没有反编译 Pro**。
所以无法确认 Pro 内部具体解析的是 `stream_groups` 还是别的字段；但从 FFmpeg 9.0 的能力来看，
流组是唯一能覆盖真实 UHD BD 无 descriptor 场景的信号，这也是本评估推荐的实现依据。

## 六、实现路径（本批不写代码）

### 6.1 取信号

对原盘的主 M2TS 运行一次插件自己的静态 ffprobe：

```
ffprobe -v error -print_format json \
  -show_streams -show_format -show_stream_groups \
  "<主 m2ts 路径 或 bluray:URL>"
```

判定规则：

1. 在 `stream_groups` 里找 `type` 含 `Dolby Vision` 且 `nb_streams == 2` 的组；
2. 组内两条流都是 `hevc`，按 `id` 识别：
   - `0x1011` = BL（base layer）
   - `0x1015` = EL（enhancement layer）
3. 可选二次确认：如果 EL 流的 `side_data_list` 里有
   `DOVI configuration record`，检查 `dv_profile == 7 && el_present_flag == 1`；
   真实 UHD BD 通常没有这条 side data，所以它只能作为加分项，不能作为必要条件；
4. 如果既没有流组、也没有 `dv_profile=7 + el=1` 的 DOVI 记录，就按“非 P7 双层”处理，
   不猜测、不根据分辨率瞎判。

### 6.2 改哪些文件、多大规模

| 文件 | 改动 | 估计行数 |
| --- | --- | --- |
| `StrmAssistant/Common/CustomFfprobeApi.cs` | 增加一个返回原始 JSON 的探测重载（支持额外参数），保留现有走 Emby `ProbeResult` 的路径 | 80–120 |
| `StrmAssistant/Common/DvP7Api.cs`（新建） | 流组 JSON 模型、P7 双层判定、BL/EL 识别、结果 DTO | 120–180 |
| `StrmAssistant/Common/DiscMediaInfoApi.cs` | 在本地 ISO/BDMV 的 `source` 和远程 `bluray:` URL 上调用检测；在 `SaveMediaStreams` 前给 BL/EL 打标 | 60–100 |
| `Properties/Resources.resx`、`Resources.zh.resx`、`Resources.zh-hant.resx`、`Resources.Designer.cs` | 仅在需要本地化 EL 展示标签时新增文案 | 10–20 |

合计约 **300–450 行生产代码**，1 个新文件，2 个现有文件；不需要 Harmony 补丁，不需要改 `Plugin.cs`，
也不碰 `MovieDbApi.cs`、`PluginOptions.cs`、`MetadataEnhanceOptions.cs`、`DoubanApi.cs` 和搜索相关文件。

### 6.3 展示方案

Emby 没有“增强层”字段，只能用现有字段表达：

- **BL 流**：设置 `ExtendedVideoType = DolbyVision`、
  `ExtendedVideoSubType = DoviProfile76`，让条目在 Emby 里显示为 DV Profile 7；
- **EL 流**：保留为第二条视频流（它本来就在 Emby 的 `MediaStreams` 里），
  用 `Title` / `DisplayTitle` 标成“Enhancement Layer”之类的文案；
  不要删除它，否则“增强层媒体信息”就没了；
- 主视频宽高继续按分辨率最大的流取（现有 `ApplyMediaInfoAsync` 已经是这样），
  避免把 1080p 的 EL 当成主视频。

EL 的具体展示效果（Emby 详情页是否显示第二条视频流、`Title` 是否可见）必须在真实服务器上验证，
这是本评估里唯一不能靠本地静态分析确定的展示细节。

### 6.4 失败与降级

- 静态 ffprobe 不可用（未下载、下载失败、用户没配路径、离线）→ 不做标注，保留 Emby 原结果，
  日志说明一次，不把条目标成 DV；
- ffprobe 返回里没有流组，也没有 P7 的 DOVI 记录 → 按非 P7 双层处理；
- 文件不是原盘（普通 MKV/MP4）→ 不属于本功能范围；如果以后要覆盖单轨 remux，
  可以用 `dv_profile=7 && el_present_flag=1` 作为独立判定；
- 检测是只读探测，失败不能影响现有的媒体信息提取和播放。

### 6.5 验证计划

用四类样本在真实 Emby 4.10.2（Linux x64）上验证：

1. 真实 P7 双层 ISO（FEL）——预期检测为真，BL/EL 都被识别；
2. 真实 P7 的 BDMV 文件夹——预期同上；
3. P7 单轨 MKV——预期不误判为“原盘双层”（按范围可跳过）；
4. 非 DV 的 UHD BD ISO——预期检测为假，不产生 DV 标记。

同时检查：本地 ISO、远程 http/smb ISO、自定义 ffprobe 关闭/缺失、媒体信息页面展示、
以及设置 DV 标记后播放/转码决策是否受影响。

## 七、已知限制与风险

1. **没有在真实零售 UHD BD ISO 上实测。** 本地的证据是 FFmpeg 源码 + FATE 真实样例 +
   按 UHD BD 布局改 PMT 的派生样例；`bluray:` 协议 + `stream_groups` 的组合需要在生产环境用一张真实 ISO 做首次验证。
2. **FFmpeg 的 BDMV 判定是 PID 结构推断，不是 RPU 内容校验。**
   `detect_bdmv_dovi_group` 只要求 HDMV + `0x1011`/`0x1015` 两条 HEVC，理论上非 DV 盘如果也用了
   0x1015 的 HEVC 流可能误判。若要更高置信度，可以再扫 EL 流的 RPU NAL（NAL type 62），
   但这会增加实现量，不是第一版必需。
3. **不区分 MEL / FEL。** 本评估的判定目标是“P7 双层（BL+EL）结构”，
   仅凭 DOVI 配置记录里的 profile / 层标志不足以可靠区分 MEL 与 FEL；
   如果产品要求只识别 FEL（完整增强层），需要解析 EL 的残差数据，超出本评估范围。
4. **展示语义由插件决定。** Emby 没有层字段，`Title`/`ExtendedVideoSubType` 是我们的用法，
   不是 Emby 的正式契约；UI 是否展示、客户端是否买账需要实测。
5. **新增对静态 ffprobe 的依赖。** 远程 ISO 提取本来就需要它；本功能会把依赖扩展到本地 ISO/BDMV 的检测。
   离线服务器需要手动放置 9.0.2 二进制。
6. **可能影响播放/转码决策。** 把 BL 标成 DolbyVision 后，Emby 可能按 DV 内容选择转码/直连策略；
   这与 Emby 自己对 P7 MKV 的处理一致，但仍需在真实环境确认不会让原本可播放的原盘变得不可播放。

## 八、建议

- **推荐做，分两步：**
  - 第一步（低风险，约 250–300 行）：只做检测 + 给 BL 打 DV Profile 7 标记，EL 保持原样并记录日志；
  - 第二步（约 100–150 行 + 可选 resx）：给 EL 加展示标签，并在真实服务器上确认 UI 效果。
- **先做一次真实 ISO 试点**：拿一张确定是 P7 双层的 ISO 和一张非 DV 的 UHD ISO，验证
  `bluray:` + `-show_stream_groups` 的实际输出，再决定是否进入功能实现。
- 如果不接受“依赖插件自己的 9.0.2 静态 ffprobe”，那么按 Emby 4.10.2 自带链路，这条功能**做不到**；
  这不是代码量问题，而是 Emby 自带 ffprobe 5.1 根本没有 BDMV DV 流组这个概念。

## 附录 A：关键证据与复现命令

### A.1 Emby 程序集

- `MediaBrowser.Model.dll`：`MediaStream` 字段/属性、`ExtendedVideoTypes`、`ExtendedVideoSubTypes`（`DoviProfile76 = 13076`）。
- `Emby.Media.Model.dll`：`ProbeResult`（无 `stream_groups`）、`ProbeSideDataDovi` 字段、
  `ProbeStreamParser.ParseVideoProperties` 的 `13000 + dv_profile*10 + compat_id` 映射。
- `Emby.Server.MediaEncoding.dll`：探测命令字面量（无 `-show_stream_groups`）。

### A.2 Emby 自带 ffprobe

```
$ strings /opt/emby-server/lib/libavutil.so.57.28.100 | grep "FFmpeg version"
FFmpeg version 5.1-emby_2023_06_25_p6
```

amd64 与 arm64 的 4.10.2 deb 都是这个版本；`ffprobe` 二进制里没有 `show_stream_groups`。

### A.3 公开样例与实测命令

```bash
# 单轨 P7 MKV
ffprobe -v error -show_streams -show_format -print_format json \
  https://fate-suite.ffmpeg.org/mkv/dovi-p7-hvce.mkv

# 规范 TS（EL 带 DOVI descriptor）
ffprobe -v error -show_stream_groups -show_streams -print_format json \
  https://fate-suite.ffmpeg.org/mpegts/dovi-p7.ts
```

实测输出摘要见正文 4.1–4.3。派生样例的做法：以 `mpegts/dovi-p7.ts` 为输入，
只重写 PMT（EL `stream_type: 0x06 → 0x24`，去掉 EL 的 DOVI descriptor，保留 HDMV 注册描述符），
未改动任何视频数据；`ffprobe 9.0.2` 仍能识别出 DV 流组，且不带 `-show_stream_groups` 时无任何 DV 信号。

### A.4 FFmpeg 源码

- `n9.0.2` `libavformat/mpegts.c`：
  - `DOVI_VIDEO_STREAM_DESCRIPTOR` 解析（约 2444 行起）；
  - Profile 7 双轨 EL 的 `dependency_pid` 流组（约 2504 行起）；
  - `detect_bdmv_dovi_group()`（约 2632 行起），注释明确说明 UHD BD 不符合 Dolby TS 规范、
    EL 固定 PID `0x1015`。
- `n5.1` 同名文件只有 `0xb0` descriptor 解析，没有 `AVStreamGroup`，也没有 `detect_bdmv_dovi_group`。
- 相关提交：`29bc8ec8d15493abf3bcbdea68b3046d150334e5`（2026-05-17）。

### A.5 上游公开资料

- Wiki：`提取-ISO-媒体信息.md` 第 3 条与“设置”第 1–3 步。
- 发布仓库：`https://github.com/sjtuross/StrmAssistant.Releases/tree/main/static-ffprobe`，
  插件 `CustomFfprobeApi` 使用的就是这里的 `ffprobe_9.0.2.zip`。
- 未反编译 Pro，未使用 Pro DLL。
