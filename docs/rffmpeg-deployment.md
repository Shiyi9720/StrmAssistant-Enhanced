# rffmpeg 负载均衡方案（对齐 PRO「分布式媒体信息提取 — 负载均衡方案」）

PRO 的做法是外置 sidecar：容器内把 `ffprobe`/`ffmpeg` 换成 rffmpeg 封装器，由它通过 SSH 把命令发到 worker 节点执行。
本项目已在生产按同样形态部署并验证（2026-11-07）。

## 组成

| 角色 | 位置 | 说明 |
|---|---|---|
| master | Emby 容器 `/config/bin/` | `rffmpeg`（12.7MB, linux_x64, Go）、`ssh`（静态 OpenSSH 10.2p1）、`rffmpeg.yaml`、`id_rsa`（600）、`rffmpeg.db`（sqlite）、`logs/` |
| worker | NAS 容器 `rffmpeg-worker`（`sjtuross/rffmpeg-worker-vulkan:latest`） | 暴露 `2222:22`，环境 `SSH_USER=emby SSH_UID=1000 SSH_GID=1000`，挂载 `./.ssh:/host_ssh:ro` |
| 媒体可见性 | worker 挂载 | `<strm-root>`（strm 库）与 `<clouddrive-root>`（115 云盘）只读，**路径与 master 完全一致**（rffmpeg 无路径转换） |

## 关键配置（/config/bin/rffmpeg.yaml）

```yaml
program: { log: "/config/bin/logs", debug: true }
directories: { owner: "root", group: "root" }
remote:
  user: "emby"
  args: ["-i","/config/bin/id_rsa","-p","2222","-o","LogLevel=ERROR",
         "-o","StrictHostKeyChecking=no","-o","UserKnownHostsFile=/dev/null"]
commands:
  ssh: "/config/bin/ssh"
  ffmpeg: "/usr/local/bin/ffmpeg"      # worker 侧路径
  ffprobe: "/usr/local/bin/ffprobe"    # worker 侧路径
  fallback_ffmpeg: "/config/bin/ffmpeg.orig"   # master 本地原始二进制
  fallback_ffprobe: "/config/bin/ffprobe.orig"
  fallback: true
database: { type: sqlite, path: "/config/bin/rffmpeg.db" }
```

- `directories.owner` 必须是实际运行用户（容器内是 root），写 `emby` 会因权限初始化失败。
- `fallback_*` 必填且指向**master 本地原始二进制**；否则远程不可用时 rffmpeg 会去执行不存在的 `/usr/local/bin/ffprobe`。

## 部署步骤（可重放）

```bash
# 1) master 侧二进制（raw.githubusercontent 在 NAS 上会截断，必须先在 Mac 下好再 scp）
scp rffmpeg rffmpeg.yaml ssh nas:<emby-config>/bin/
chmod +x …/rffmpeg …/ssh

# 2) 密钥（容器内没有 ssh-keygen，用宿主生成）
ssh-keygen -t rsa -b 4096 -f …/config/bin/id_rsa -N ""
cp …/config/bin/id_rsa.pub <worker-dir>/.ssh/authorized_keys   # 600

# 3) worker
docker run -d --name rffmpeg-worker -p 2222:22 \
  -e SSH_USER=emby -e SSH_UID=1000 -e SSH_GID=1000 \
  -v <worker-dir>/.ssh:/host_ssh:ro \
  -v <strm-root>:<strm-root>:ro \
  -v <clouddrive-root>:<clouddrive-root>:ro \
  --restart unless-stopped sjtuross/rffmpeg-worker-vulkan:latest

# 4) 注册节点并试跑（在 emby 容器内）
cd /config/bin && ./rffmpeg add <emby-host> -n worker1 -w 1
./rffmpeg status
./rffmpeg run ffprobe -v error -show_entries format=duration -of json /test/sample.mp4

# 5) 让 Emby 用上（只换 ffprobe，转码仍走本地 ffmpeg——比 PRO 双换更保守）
docker exec emby sh -c "cp -a /bin/ffprobe /config/bin/ffprobe.orig; ln -sf /config/bin/rffmpeg /bin/ffprobe"
```

## 验证记录（2026-11-07 生产）

- 容器内 `/bin/ffprobe -version` → `starting rffmpeg args=-version as=/bin/ffprobe` → `finished rffmpeg successfully`
- 真实条目 refresh（`POST /emby/Items/203342/Refresh`）返回 204，流仍 3 条（Video hevc + Audio aac + Audio eac3），Emby 日志无 ffprobe 报错
- rffmpeg 日志累计 `running command host=worker1` 5 次（含 -version 与真实探测）

## 已知限制与坑

1. **`/bin/ffprobe` 符号链接在容器重建后丢失**（镜像层不可变）。要持久化，需在 Emby 的 compose 里加：
   `- <emby-config>/bin/rffmpeg:/bin/ffprobe:ro`（或启动脚本里重建符号链接）。
2. `raw.githubusercontent.com` 直下 12.7MB 二进制在 NAS 上会截断（得到 6.7MB，`file` 报 `too large section header offset`、执行段错误）——**先下到 Mac 再 scp**。
3. worker 必须挂载与 master 一致的媒体路径，rffmpeg 不做路径映射。
4. 目前 worker 与 Emby 同主机（NAS）——链路通了但无性能收益；要真正分载，把 worker 容器放到另一台机器（改 `rffmpeg add <另一台IP>`）。
5. 只换 `ffprobe` 不换 `ffmpeg`：媒体信息/探测走 worker，转码仍在本地，风险最小；若要完全对齐 PRO 可同样 `ln -sf` ffmpeg，但需自行评估转码稳定性。

## 跨机 worker 可行性实测（2026-11-07 补测）

- Mac 经局域网 SMB 读 NAS 上 115 云盘的 ISO：**正常**，偏移 32KB 处 2KB 的 md5 与 NAS 本地一致，拖动中间 64MB 实测约 10 MB/s（早前"Permission denied"未能复现，属当时状态）
- 但把 SMB 挂载点 bind 进 Docker Desktop 容器会**挂死**（`docker run -v /Volumes/CloudDrive:/cd:ro` 两次 4 分钟无响应）——Docker Desktop 的 virtiofs 不支持跨网络文件系统，这是产品限制，不是网络/NAS 问题
- 结论：跨机 worker 只能走**原生进程**（开 macOS 远程登录 + 静态 ffprobe + 路径软链 `<clouddrive-root>`），且 SMB 读速比 NAS 本地低一个数量级，只适合分摊少量探测
- 生产决定：保持 NAS 单 worker（同机同存储，最快路径）；多 worker 分发逻辑不做实测，无可读同一份媒体的第二台机器
