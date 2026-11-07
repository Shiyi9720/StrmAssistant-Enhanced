# StrmAssistant 增强版 — 交付说明（2026-11-07）

本插件是在 Emby 社区版 StrmAssistant（Lite）基础上自建的增强分支，目标是覆盖
sjtuross/StrmAssistant PRO 版公开 Wiki 上的功能清单（不复制、不复用任何 PRO 二进制或授权）。

- 目标运行时：Emby 4.10.2 稳定版，程序集 `4.10.2.0`（生产实测）
- 主分支：`port/emby-4.10-stable`
- 生产部署：NAS `<emby-host>`，容器 `emby`，插件目录 `<emby-config>/plugins`
- 部署布局：主 DLL 在 `plugins/` 顶层；全部依赖在 `plugins/StrmAssistantLite.deps/`（由插件 AssemblyResolve 按需加载，避免 Emby 启动扫描期的 LoaderException）

## 一、部署与回滚

1. 构建：`export PATH=$HOME/.dotnet:$PATH && dotnet build -c Release`
2. 部署（本机到 NAS）：
   - 备份现有 `StrmAssistantLite.dll`（命名 `*.bak-<标记>`）
   - 复制 `bin/Release/netstandard2.1/StrmAssistantLite.dll` → `plugins/`
   - 复制同目录其余 `*.dll` → `plugins/StrmAssistantLite.deps/`
   - `docker restart emby`（不重启不生效）
3. 验证：
   - `docker inspect emby --format '{{.State.Health.Status}}'` 应为 healthy
   - 日志出现 `Harmony self-test passed` 表示 mod 引擎就绪
   - 设置页 `GET /emby/UI/View?PageId=63c322:Settings` 返回 200（需管理员令牌）
4. 回滚：把对应 `.bak-*` 覆盖回 `StrmAssistantLite.dll` 后重启容器。

## 二、功能清单（按分组，全部默认安全：改动行为的选项默认关闭或保持旧语义）

### 稳定性与适配
- Emby 4.10 适配：MediaInfoApi / SubtitleApi 的 Init Failed 修复
- 设置页崩溃修复（集合型选项渲染导致的进程退出）
- 完整资源本地化（中文/繁体卫星资源随插件加载，设置页中文化）
- 计划任务日志器惰性化（修复"所有计划任务 NRE 失败"）
- 依赖统一 `.deps` 布局 + HarmonyX 2.16.1 mod 引擎（自检通过）

### 多版本
- 多版本合并上限 2–64（服务端 8 部硬限制之外的插件侧控制）
- 多版本智能排序
- 跨库播放进度分离（按内容身份归并 + 版本签名匹配）
- 多版本文件名美化（默认开，仅影响版本列表）
- 跨库收藏分离

### 体验增强
- 当前集播完自动标记同季前序集已播
- 倒序显示季/集
- 季级合集归属显示
- 强制季集显示样式（默认/SxxExx/集名优先）
- 快捷菜单：删除单个演员、清除章节图、清除 BIF、清除媒体信息
- 拼音首字母排序（写回 SortName，默认关，手动任务触发）
- 空间音频标识（Dolby Atmos / DTS:X，仅按 ffprobe Profile 判定）
- 隐藏合集媒体库（快捷菜单切换 + 首页过滤）

### 元数据
- 本地/自定义 TMDB 源（API + 图片 URL 改写）
- **TMDB 磁盘缓存**（TTL 30 天、上限 256MB LRU、失败回退过期缓存）
- 简繁海报区分、繁中优先、简转繁
- 原语言海报优先
- 电影元数据刷新三档（全量 / 仅缺字段 / 不覆盖）
- 豆瓣辅助刮削 + **深度补全**（演员/导演/季信息/评分人数/上映日期，默认关）
- 豆瓣评分徽标（详情页）
- 豆瓣预构建缓存任务（默认关，限速串行，遇 403/418/429 暂停 10 分钟）
- 中文演员刷新偏好（范围四档、跳过规则、独立并发与限速、统计）
- 豆瓣推荐频道（默认关）

### 搜索
- 拼音（全拼/首字母）、繁简互搜、别名/原名/年份、分级排序、索引服务与重建任务
- 索引入演员名与外部 ID（豆瓣/BGM/IMDb）
- 屏蔽 Emby 自带搜索推荐列表

### 碟片与媒体信息
- ISO 直读（http/smb）+ 自带静态 ffprobe（四级获取，失败回落原生）
- ISO/BDMV 完整媒体信息、DVD IFO 章节解析
- **DV P7 双层检测**（流组 0x1011/0x1015 判定，真实零售盘 2 正 1 负验证）
- 独占媒体信息提取（HarmonyX mod，16 补丁点，生产自检通过；开关默认关）
- 外挂音轨扫描（默认关，可配扩展名）
- 媒体信息/声纹/BIF 三套黑名单、图像提取超时倍率
- 截图比例矫正、DV P5 自定义 ffmpeg、HDR Vivid 识别

### 片头片尾
- IntroDb 双源（官方 + theintrodb.org）、单剧重探、按库片头时长设置页
- 电影片尾记录（默认关，只落插件 JSON，不产生跳过按钮）
- 片头指纹库导入导出

### 分布式
- worker 端点：媒体信息、声纹、截图、指纹、HDR Vivid、DV P5
- **媒体信息优先走 worker**（默认关，失败静默回落并区分 no_worker/timeout/worker_error/path_not_allowed）

## 三、已知限制（插件层无法解决）

1. 8 部多版本合并上限在 Emby 服务端 `Emby.Naming.VideoListResolver`，插件只能控制自身任务侧上限
2. 按库片头时长的可视化设置页无法做（Emby 4.10 渲染集合型选项会栈溢出杀进程），只能改配置文件或按库 JSON
3. 外挂音轨扫描依赖 Emby `AudioTrackResolver` 扩展名白名单，插件只能补 `dtshd/truehd/thd` 等白名单外扩展
4. 电影片尾在 Emby 无跳过机制，只落库不入 UI
5. DV P7 的 MEL/FEL 细分类未做；Emby 原生探测不暴露层信息
6. 完整本地 TMDB 镜像需独立服务，未做；rffmpeg 全量接管 Emby 转码风险过高，按路线 A 只接管插件自发调用

## 四、运维要点

- 插件配置：`<emby-config>/plugins/configurations/Strm Assistant.json`
- 插件数据目录：`<emby-config>/plugins/StrmAssistantLite/`（搜索索引、豆瓣评分缓存、IntroDb、电影片尾等）
- 管理员 API 令牌：容器重启后失效，需重新登录获取（`/emby/Users/{adminId}`）
- 大文件读取若报 `State not recoverable`：`docker restart clouddrive2` 后重试（CD2 FUSE 句柄失效）
- 服务端 ffprobe 9.0.2（碟片 DV P7 检测用）：NAS `~/tools/ffprobe9/ffprobe`
