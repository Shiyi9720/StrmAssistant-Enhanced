# Strm Assistant（神医版）测试包 — 安装与测试说明

版本标记：`3.0.0.57`｜**仅支持 Emby 4.10.2（程序集 4.10.2.0）**
打包时间：2026-11-07

---

> 本构建为社区版（GPL-3.0）增强分支的发布产物，非官方发布；新增功能按公开文档独立实现，未包含任何 PRO 版本代码或二进制。

## 1. 适用环境（不满足会加载失败）

| 项目 | 要求 |
|---|---|
| Emby Server | **4.10.2 稳定版**，程序集版本必须是 `4.10.2.0` |
| 平台 | Linux x64 / arm64、macOS（官方容器镜像或裸装均可） |
| .NET | Emby 4.10 自带运行时（.NET 8），无需另装 |

**先确认版本**（把 IP 换成你的服务器）：

```bash
curl -s http://192.168.31.51:8096/emby/System/Info | grep -o '"Version":"[^"]*"'
```

输出应为 `"Version":"4.10.2.0"` 一类。若不是 4.10.2，请勿安装（程序集版本不匹配会被 Emby 拒绝加载，日志报 `LoaderException: Could not load ... Version=public-ip-xxx`）。

---

## 2. 安装步骤

1. **备份**你现有的插件目录（如果有旧版）：
   ```bash
   cd <emby配置目录>/plugins
   cp -a StrmAssistantLite.dll StrmAssistantLite.dll.bak-$(date +%Y%m%d)
   ```
2. 把本包 `plugins/` 目录里的**全部内容**拷进 Emby 的插件目录，保持结构：
   ```
   plugins/
   ├── StrmAssistantLite.dll
   ├── StrmAssistantLite.deps/      ← 依赖程序集，缺一个就起不来
   ├── zh/StrmAssistantLite.resources.dll
   └── zh-hant/StrmAssistantLite.resources.dll
   ```
   常见插件目录：Docker 官方镜像 `/config/plugins`；裸装 `/var/lib/emby/plugins`；Windows `%APPDATA%\Emby-Server\programdata\plugins`。
3. **重启 Emby**（拷 DLL 不重启不生效）：`docker restart emby` 或面板里重启。
4. 打开 Web 端 → **设置 → 插件**，应能看到 **`Strm 助手`**，版本 `3.0.0.57`。
5. 打开 **设置 → 高级 → Strm 助手**，能进设置页即为安装成功。

**配置文件**：仍为 `Strm Assistant.json`（和社区版/官方版同名），从旧版升级过来的设置会自动沿用，不会丢配置。

---

## 3. 已实现功能（怎么快速验收）

| 模块 | 关键开关 | 验收方式 |
|---|---|---|
| 多版本合并与排序 | 多版本合并上限（2–64）、智能排序 | 同目录放多个版本，看详情页版本列表与顺序 |
| 播放进度按版本分离 | 体验增强 → 按版本记录进度 | 两个版本分别播不同进度，互不覆盖 |
| 库角标 / 总集数 | 库条目数角标、剧集总集数 | 首页库封面角标、剧集详情显示总集数 |
| 片头片尾跳过 | 片头指纹、并行声纹提取 | 任务中心跑「片头探测」，播放时有跳过按钮 |
| 字幕 | OpenSubtitles 优化、字幕流黑名单、外挂字幕 | 播放时字幕菜单；黑名单里勾的轨道不再显示 |
| 元数据来源 | 本地 TMDB 源、别名/繁简/拼音搜索 | 搜索框输入 `zhizhuxia` 应命中《蜘蛛侠》系 |
| 本地元数据刮削源 | `启用本地元数据源`、`本地元数据根目录`、模式（缓存优先/在线并缓存/仅覆盖/关闭） | 开启后刮削一次，根目录下会出现 `cache/tmdb-movies2/<tmdbid>/movie.json` 与 `images/` |
| 本地剧集组 | `启用本地剧集组` | 剧集目录放 `episodegroup.json`，入库后按自定义季集映射 |
| 统一内容分级 | 统一分级（关闭/仅记录/强制统一） | 开启「仅记录」后看日志记录的原值→目标值 |
| 特别篇顺序 | `自定义特别篇播出顺序` | 特别篇按 `specials.json` 的 `air_order` 排序 |
| 豆瓣增强 | 豆瓣推荐频道、评分角标、预告片、缓存预构建 | 首页出现「豆瓣推荐」频道；条目显示豆瓣分 |
| 分布式提取 | 分布式媒体信息（worker 端点） | 设置 worker 后任务日志显示「本地/worker/回落」统计 |

大多数开关**默认关闭**，按需开启。高危/全局类功能（前端注入、provider 包装、Harmony 补丁）建议一次只开一个，观察一段时间再开下一个。

---

## 4. 已知限制（不是 bug）

1. **同目录多版本上限**：Emby 扫描端原生硬编码为 8 部，插件已把这个常量改写到 64（可在设置里关闭，恢复原生行为）；关闭后仍按原生 8 部合并。
2. **播放进度原生按 TMDB id 共享**：跨版本分离靠插件自己的「用户+版本」记录并接管续播。
3. **不接管 Emby 转码**：插件只做媒体信息提取与播放链路增强，转码仍由 Emby 本地完成。
4. **DV P7 探测**需要静态 ffprobe 9.0（Emby 自带 5.1 不支持 `-show_stream_groups`）：插件首次用到时会自动下载（约 30 MB），离线环境请自行放置到插件数据目录或配置路径。
5. **豆瓣/IntroDB 属于第三方数据源**，接口变动或限流会导致功能暂时不可用（已内置 403/418/429 暂停 10 分钟）。

---

## 5. 出问题怎么报

请附上这四样，基本能一次定位：

1. Emby 版本：`curl -s http://<IP>:8096/emby/System/Info | grep -o '"Version":"[^"]*"'`
2. 启动日志（重点看 `Strm` / `Error creating` / `Init Failed`）：
   ```bash
   docker logs emby 2>&1 | grep -iE "strm|error creating|init failed" | tail -30
   ```
3. 出问题的页面 URL 与操作步骤
4. 设置页截图（开关状态）

**回滚**：把备份的 `StrmAssistantLite.dll` 覆盖回去，重启 Emby。设置文件 `Strm Assistant.json` 无需处理。

---

## 6. 包里有什么

```
plugins/StrmAssistantLite.dll               主程序
plugins/StrmAssistantLite.deps/*.dll        依赖（0Harmony、System.Text.Json、拼音库等 21 个）
plugins/zh/…resources.dll                   简体中文资源
plugins/zh-hant/…resources.dll              繁体中文资源
README-安装说明.md                           本文件
SHA256SUMS.txt                              校验值
```
