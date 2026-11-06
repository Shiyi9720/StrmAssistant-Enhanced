# DV P7 真盘试点 · 执行记录

- 日期：2026-11-06
- 批次：第二十三批（第一步）
- 结论：**本机没有可用的真实原盘 ISO，试点无法执行；按任务要求停在试点这一步，未用合成样例冒充真盘证据，也未开始第二步的代码实现。**

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

## 六、本批状态

- 未改任何功能代码；
- 未开始 `CustomFfprobeApi.cs` / `DvP7Api.cs` / `DiscMediaInfoApi.cs` 的实现；
- 未在真实零售盘上验证，因此第二十二批评估里的限制仍然成立。
