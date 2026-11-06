# DV P7 真盘试点 · 执行记录

- 日期：2026-11-06
- 批次：第二十三批（第一步 + 第二步试点）
- 结论：**真盘试点仍被 NAS 凭据挡住（升级后重试结果相同）；按 Hermes 建议，已用 FATE 公开样例完成检测逻辑与 BL 标记的实现，并通过正向/反向验证。尚未在真实零售盘上验证，EL 展示标签与 MEL/FEL 区分未做。**

## 一、目标

在 Mac 上找一张真实 DV P7 双层原盘 ISO 和一张非 DV 的 UHD 原盘 ISO，
用插件同款静态 ffprobe 9.0.2 对 `bluray:` 跑 `-show_stream_groups`，记录：

- DOVI 流组是否存在；
- BL / EL 的 PID（0x1011 / 0x1015）；
- DOVI 配置记录内容；
- 与 FATE 样例的差异。

## 二、实际搜索范围

按任务要求，只查看本机已挂载的卷和常见共享路径，没有用任何账号密码挂载 NAS。

| 位置 | 命令 / 方式 | 结果 |
| --- | --- | --- |
| `/Volumes` | `ls -la /Volumes` | 只有 `Macintosh HD` 和 `Recovery`，没有外接盘或网络卷 |
| 所有挂载点 | `mount` / `df -h` | 只有本机 APFS 卷与两个应用挂载点（MiHome、Rex），无 SMB/NFS |
| 外接磁盘 | `diskutil list external` | 只有磁盘映像 `Chromium`（385 MB），无物理外接盘 |
| SMB 共享 | `smbutil statshares -a` | 空，没有任何已挂载共享 |
| `/Network`、`/net`、`/mnt`、`/media` | `ls` | 均不存在 |
| 常见 NAS 挂载名 | `/Volumes/{Media,media,Video,video,Movies,movies,NAS,nas,Share,share}` | 均不存在 |
| 用户目录 | `~/Movies`、`~/Downloads`、`~/Desktop`、`~/Documents`、`/Users/Shared` | 无 ISO / M2TS / BDMV |
| 全盘 Spotlight | `mdfind "kMDItemFSName == '*.iso'c"` | 只有两个 Windows 安装 ISO：`~/Library/Parallels/Downloads/26300.9711...zh-cn.iso`、`~/Parallels/Windows 11.pvm/Unattended.iso` |
| 全盘 Spotlight | `mdfind` 查 `*.m2ts` / `BDMV` | 0 条 |
| 大文件 | `mdfind "kMDItemFSSize > 20000000000"` | 只有 Parallels 的 `harddisk.hdd`，不是媒体文件 |

另外确认：本机没有运行 Emby / Docker，也没有已配置的媒体库路径可借用
（`~/.emby` 下只有管理 token 脚本）。

## 三、缺什么

1. 一张**真实 DV Profile 7 双层**（BL+EL+RPU）的 UHD BD ISO 或 BDMV 文件夹；
2. 一张**非 DV 的 UHD BD** ISO 或 BDMV 文件夹，用于验证不误判。

FATE 的 `dovi-p7.ts`、`dovi-p7-uhdbd.ts`（第二十二批派生）仍然可用，
但它们是**公开测试样例 / 由样例派生的布局文件**，不能替代“真实零售盘”这一证据等级。
按任务要求，本批不拿它们冒充真盘结果。

## 四、试点工具已就绪

静态 ffprobe 已在位，版本与插件 `CustomFfprobeApi` 使用的一致：

```
/tmp/ffprobe-static/ffprobe
ffprobe version 9.0.2-a3f9c2b
configuration: ... --enable-libbluray --enable-libdvdread --enable-libdvdnav ...
```

拿到盘后直接执行（把路径替换为真实路径）：

```bash
# ISO（libbluray 直接打开镜像）
/tmp/ffprobe-static/ffprobe -v error -print_format json \
  -show_streams -show_format -show_stream_groups \
  "bluray:/path/to/DV_P7_DISC.iso"

# BDMV 文件夹
/tmp/ffprobe-static/ffprobe -v error -print_format json \
  -show_streams -show_format -show_stream_groups \
  "bluray:/path/to/BDMV"

# 对照：不带 -show_stream_groups，确认是否真的没有其它 DV 信号
/tmp/ffprobe-static/ffprobe -v error -print_format json \
  -show_streams -show_format \
  "bluray:/path/to/DV_P7_DISC.iso"
```

记录要点：`stream_groups[].type`、`nb_streams`、组内 stream 的 `id`（预期 0x1011/0x1015）、
以及 EL 流 `side_data_list` 里有没有 `DOVI configuration record`。

## 五、恢复试点的条件

需要用户提供以下任意一种（由用户/Muse 提供，不由本会话挂载 NAS）：

- 一张 DV P7 双层原盘 ISO 的**绝对路径**（以及片名，便于确认是不是 P7）；
- 一张非 DV UHD 原盘 ISO 的绝对路径；
- 或者用户自行把 NAS 共享挂载到本机 `/Volumes/...` 后告知路径。

拿到路径后：先跑上面的命令、把原始 JSON 追加进本文件，再判断是否进入第二步实现。

## 六、第二次尝试：NAS（2026-11-06 22:29）

### 6.0 用户提供的目标盘

以下路径由用户提供，本会话因认证失败**没有在 NAS 上实际验证**：

| 用途 | 文件名 | 大小 / 用户说明 |
| --- | --- | --- |
| DV P7 双层 | `寄生虫 (2019) - 2160p.DV.BluRay.iso`（`/vol1/1000/CloudDrive/115open/拾忆/电影/日韩/寄生虫 (2019) [tmdbid=496243]/`） | 92.6G；用户已实测 0x1011 3840×2160 + 0x1015 1920×1080，判断为 P7 双层 FEL |
| 另一张 P7 | `变形金刚4：绝迹重生 (2014) - 2160p.DV.BluRay.iso`（`/拾忆/电影/` 下） | 94.5G；用户称同样 2 条流 |
| 非 DV 对照 | `你的名字。(2016) - .iso`（`/拾忆/电影/` 下） | 61.2G；用户称 1 条 HEVC 2160p |

### 6.1 连通性

```
$ ping -c 2 192.168.31.51
2 packets transmitted, 2 packets received, 0.0% packet loss

$ nc -z 192.168.31.51 445   → succeeded
$ nc -z 192.168.31.51 139   → succeeded
```

Bonjour 广播确认主机名：`SHIYIFNOS`（`dns-sd -B _smb._tcp local`）。
ARP 表里 192.168.31.51 对应 `38:5:25:35:8b:7d`，是局域网内设备。

### 6.2 共享枚举（全部被服务器拒绝）

```
$ smbutil view //192.168.31.51
smbutil: server rejected the authentication: Authentication error

$ smbutil view -N //192.168.31.51          # 不提示密码
smbutil: server connection failed: Operation timed out

$ smbutil view -G //192.168.31.51          # 允许 guest
smbutil: server rejected the authentication: Authentication error

$ smbutil view -g //192.168.31.51          # 仅 guest
smbutil: server rejected the authentication: Authentication error

$ smbutil view -N //shiyi@SHIYIFNOS        # 当前用户名
smbutil: server connection failed: Operation timed out
```

结论：该 NAS 的 SMB 需要账号密码，不允许匿名或 guest 访问。

### 6.3 钥匙串检查（只读，不打印密码）

```
$ security find-internet-password -s 192.168.31.51        → 未找到
$ security find-internet-password -s SHIYIFNOS            → 未找到
$ security find-internet-password -s shiyifnos            → 未找到
$ security find-internet-password -s SHIYIFNOS.local      → 未找到
$ security find-internet-password -s shiyifnos.local      → 未找到
$ security find-internet-password -r "smb "               → 未找到
$ ls ~/Library/Preferences/nsmb.conf                       → 不存在
```

登录钥匙串里没有任何该 NAS 的 SMB 凭据；也没有 `nsmb.conf` 可用的保存密码。

### 6.4 挂载尝试（不提示密码）

```
$ mkdir -p /tmp/nasmount
$ mount_smbfs -N -o nopassprompt,nobrowse //192.168.31.51/CloudDrive /tmp/nasmount
mount_smbfs: server rejected the connection: Authentication error   (exit 77)
```

没有弹密码框、没有反复尝试、没有用空密码或猜测的密码。

### 6.5 停在哪一步

按任务红线「如果挂载需要密码且钥匙串里没有，立刻停住」，试点停在这里：

- 没有列出共享名（连 `smbutil view` 都过不了认证）；
- 没有挂载任何卷；
- 没有对三张盘跑 ffprobe，因此**本批没有任何真盘 ffprobe 输出**。

### 6.6 继续所需

由用户在 Mac 上手动完成一次（我们不做）：

1. Finder →「前往」→「连接服务器」→ `smb://192.168.31.51`；
2. 输入账号密码，勾选「在我的钥匙串中记住此密码」；
3. 挂载 `CloudDrive` 共享（或包含 `CloudDrive/115open` 的那一个共享）；
4. 告诉本会话挂载点，或直接告诉三张 ISO 的绝对路径。

之后本会话即可用 `/tmp/ffprobe-static/ffprobe` 直接跑试点，不需要再接触凭据。

### 6.7 NAS 升级后重试（22:40）

用户告知 NAS 刚完成升级，可能是上次认证失败的原因。升级完成后重新检查：

```
$ ping -c 2 192.168.31.51                 → 2/2 通，约 5.5ms
$ nc -z 192.168.31.51 445                 → succeeded
$ nc -z 192.168.31.51 139                 → succeeded
$ dns-sd -B _smb._tcp local               → SHIYIFNOS

$ smbutil view //192.168.31.51
smbutil: server rejected the authentication: Authentication error

$ smbutil view -G //192.168.31.51         # 允许 guest
smbutil: server rejected the authentication: Authentication error

$ mount_smbfs -N -o nopassprompt,nobrowse //192.168.31.51/CloudDrive /tmp/nasmount
mount_smbfs: server rejected the connection: Authentication error   (exit 77)

$ mount_smbfs -N -o nopassprompt,nobrowse //guest@192.168.31.51/CloudDrive /tmp/nasmount
mount_smbfs: server rejected the connection: Authentication error   (exit 77)
```

钥匙串复查同样没有变化：按 IP、主机名、主机名小写、`主机名.local`、`-r "smb "` 均未找到；
`~/Library/Preferences/nsmb.conf` 仍不存在。

结论：**升级后 SMB 认证依旧要求账号密码，本机钥匙串里仍没有该凭据**。
按红线立刻停住，没有弹密码框、没有猜测密码、没有继续重试。

## 七、检测实现与 FATE 样例验证（2026-11-06 23:20）

真盘仍无法访问，按 Hermes 的建议先用 FATE 公开样例把检测逻辑写完；真盘试点等 NAS 挂载后补做。

### 7.1 实现

| 文件 | 改动 |
| --- | --- |
| `Common/CustomFfprobeApi.cs` | 抽出 `RunProbeJsonAsync(inputPath, outputArguments)`，返回静态 ffprobe 的原始 JSON；`ProbeAsync` 改为复用它。新增的 `-show_stream_groups` 探测走同一入口。 |
| `Common/DvP7Api.cs`（新建） | ffprobe JSON 模型 + 检测规则：先找 `type` 含 `Dolby Vision` 且 `nb_streams == 2` 的流组，按 `0x1011` = BL、`0x1015` = EL 识别；没有流组时回退到 `dv_profile == 7 && el_present_flag == 1 && bl_present_flag == 1` 的 DOVI 配置记录。EL 的 side data 只作为附加信息，不是判定必要条件。 |
| `Common/DiscMediaInfoApi.cs` | 在 `ApplyMediaInfoAsync` 保存流之前调用检测；只在 HEVC 原盘上运行，给 BL 设 `ExtendedVideoType = DolbyVision`、`ExtendedVideoSubType = DoviProfile76`，EL 保持原样。检测失败/静态 ffprobe 不可用时静默跳过，保留 Emby 原结果。 |

本批未动 resx、未碰 `Mod/`、未改禁改文件，也不需要 Harmony。

### 7.2 正向样例

命令（`/tmp/ffprobe-static/ffprobe`，9.0.2-a3f9c2b）：

```bash
ffprobe -v error -print_format json -show_streams -show_stream_groups <sample>
```

**单轨 P7 MKV**：`fate-suite.ffmpeg.org/mkv/dovi-p7-hvce.mkv`（3840×2160）

```json
"stream_groups": [],
"streams": [{
  "index": 0, "codec_name": "hevc", "codec_type": "video",
  "width": 3840, "height": 2160,
  "side_data_list": [{
    "side_data_type": "DOVI configuration record",
    "dv_profile": 7, "dv_level": 6,
    "rpu_present_flag": 1, "el_present_flag": 1, "bl_present_flag": 1,
    "dv_bl_signal_compatibility_id": 6
  }]
}]
```

**带 DOVI descriptor 的规范 TS**：`fate-suite.ffmpeg.org/mpegts/dovi-p7.ts`

```json
"stream_groups": [{
  "index": 0, "id": "0x1015", "nb_streams": 2,
  "type": "Dolby Vision (Split base and enhancement layer)",
  "streams": [
    { "index": 0, "id": "0x1011", "codec_name": "hevc", "codec_type": "video" },
    { "index": 1, "id": "0x1015", "codec_name": "hevc", "codec_type": "video" }
  ]
}]
```

EL 流上的 DOVI 记录：`dv_profile=7, dv_level=4, rpu_present_flag=1, el_present_flag=1, bl_present_flag=0, dv_bl_signal_compatibility_id=6`。

**真实 UHD BD 布局**（`dovi-p7-uhdbd.ts`，第二十二批由 FATE 样例只改 PMT 派生）：

```json
"streams": [
  { "index": 0, "id": "0x1011", "codec_name": "hevc", "codec_type": "video", "side_data_list": [] },
  { "index": 1, "id": "0x1015", "codec_name": "hevc", "codec_type": "video", "side_data_list": [] }
]
```

不带 `-show_stream_groups` 时没有任何 DV 信号；加上之后才出现上面的 `Dolby Vision (Split base and enhancement layer)` 流组。
这条正是真实零售 UHD BD 的信令方式（EL 无 descriptor），也是本实现必须依赖流组的原因。

用编译后的 `DvP7Api.Evaluate` 对上述 ffprobe JSON 跑检测：

| 样例 | 检测结果 | Evidence | BL / EL |
| --- | --- | --- | --- |
| `dovi-p7-hvce.mkv` | **True** | `dovi_config_record` | index 0（单轨，无 PID） |
| `dovi-p7.ts` | **True** | `stream_group` | 0x1011 / 0x1015 |
| `dovi-p7-uhdbd.ts` | **True** | `stream_group` | 0x1011 / 0x1015 |

### 7.3 反向样例

| 样例 | ffprobe 输出 | 检测结果 |
| --- | --- | --- |
| `fate-suite.ffmpeg.org/mpegts/h264small.ts`（16 KB，H.264） | `"stream_groups": []`，无 side data | **False** |
| `fate-suite.ffmpeg.org/mkv/hdr10tags-both.mkv`（17 KB，HEVC 560×320，HDR10） | `"stream_groups": []`，side data 只有 Content light level / Mastering display / Display Matrix | **False** |

两个反向样例都没有 DOVI 配置记录，也没有 DV 流组，检测均返回 `IsDvP7 = False`，不误判。

### 7.4 结论与限制

- 检测规则在 FATE 单轨 MKV、规范 TS、UHD BD 布局三种形态上都返回 True，在 H.264 和非 DV HEVC 上返回 False。
- **未在真实零售盘上验证**：UHD BD 布局仍是“由 FATE 样例改 PMT 派生”的文件，不是零售盘抓取；
  寄生虫/变形金刚4/你的名字。三张 ISO 等 NAS 挂载后补做。
- 不区分 MEL/FEL；本批只标记 BL，不做 EL 展示标签。

## 八、本批状态

- 已改 3 个生产文件：`CustomFfprobeApi.cs`、`DiscMediaInfoApi.cs`，新建 `DvP7Api.cs`；
- `dotnet build -c Release`：0 错误；
- 未在真实零售盘上验证，第二十二批评估里的真盘限制仍然成立；
- EL 展示标签、MEL/FEL 区分未做。
