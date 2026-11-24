# 更新日志

## 1.1.0

本版把三个批次的能力合并进旧插件「神医助手」（13 个功能提交 + 5 个修复提交），并新增两个默认关闭的开关，把上一轮回归中「默认即生效、无独立开关」的两处 UI 改动收进开关。

**UI 行为包**

- 8 个独立开关：倒序季集、日志倒序、强制 SxxExx、自动标记前集已播、自然排序、总集数代替未观看、主屏库数量角标、合集归属。后三项按「默认开、保持现状」的既有决策，其余默认关闭；关闭时对应脚本整段不注入
- **新增「快捷菜单扩展命令与多选」`ShortcutMenuExtendedCommands`（默认关闭）**：关闭时右键菜单只保留 1.0.16 原有命令集，不多不少；打开后才出现「清除章节图（含 BIF）」「解锁全部字段」「删除重复演员」与多选批量执行
- **新增「多版本显示 DV/HDR/Atmos 标识」`VersionNamingExtendedTokens`（默认关闭）**：关闭时 `classifyToken` 与高亮正则逐字保持 1.0.16 的实现（DV/HDR 归入 codec、Atmos/DTS 归入 audio），打开后才按 Dolby Vision、HDR10+、HDR10、HDR、Atmos、DTS:X 分别着色

**快捷菜单**

- 新增「清除章节图（含 BIF）」「解锁全部字段」「删除重复演员」三条命令与多选批量执行，配套 `/Items/{id}/ClearChapterImagesAndBif`、`/Items/{id}/UnlockAllFields`、`/Items/{id}/DedupePeople` 三个端点；以上均受 `ShortcutMenuExtendedCommands` 控制，默认关闭

**通知**

- `metadata.update` 通知补上 20 字段差异与季集上下文；集合变更、集合条目增删、深度删除通知沿用既有实现

**搜索**

- 搜索支持 `douban:` / `bgm:` / `bangumi:` / `tmdb:` / `imdb:` / `tvdb:` 前缀 id 直查；索引格式升至 `Version=6`，升级后首次启动自动重建一次

**多版本显示**

- 打开 `VersionNamingExtendedTokens` 后区分 DV / HDR10+ / HDR10 / HDR 与 Atmos / DTS:X 标识；HDR10+ 用字符前视保留结尾的 `+`，DTS:X / DTS-X 先于通用 DTS 匹配
- 「强制显示文件全名」`MultiVersionForceFileName` 与「多版本按路径排序」`MultiVersionSortByPath` 保持原有默认关闭

**标签黑名单**

- 新增 `MediaInfoExcludeTags`：条目及父级标签精确匹配（大小写不敏感、支持逗号/分号/换行分隔），与既有 `mediainfo-exclude` 关键词黑名单并列

**仪表盘**

- 新增「仪表盘」开关：显示内存当前/峰值/使用率与容器上限、可取消的活动任务、活动会话、搜索索引条目/键数与队列长度，并提供重建索引入口

**演员刷新**

- 演员刷新增强：合并季演员行、只写有变化的字段、预先下载头像、补生卒信息；由 `RefreshPersonEnhancedMode` 控制，默认关闭并保留原 legacy 分支

**豆瓣客串**

- 豆瓣客串演员中文化：按 `latin_name` 映射并用完整演员列表写回（Emby `UpdatePeople` 先删后插），新增 `EnableDoubanCastLocalization` 开关

**本地 TMDB 六小项（各自独立开关，默认关闭）**

- `patches/*.json` 字段级合并、图片覆盖、`tags.json`、`external_ids.json`、`episodeorder.json`（兼容旧 `specials.json`）、内容分级按国家链回退；坏补丁跳过，只作用于返回给 Emby 的响应

**探索发现频道**

- 豆瓣合集浏览（`IChannel`）、在线预告片、MoviePilot 订阅（webhook / API 两种模式）；豆瓣海报经 `/StrmAssistant/DiscoverImage` 代理（仅放行豆瓣域名）

**外挂音频**

- 媒体信息提取主流程接入外挂音轨扫描：本地刷新后对比同目录音轨与条目外挂音频流，有变化才扫描（幂等），复用 Emby 4.10 `AudioTrackResolver` 并支持配置扩展名；音乐条目自身外挂音轨不纳入

**音乐媒体信息持久化**

- 新增默认关闭的「音乐媒体信息提取」开关；非强制任务用 `-mediainfo.json` sidecar 是否存在判断缺失，修复音乐条目因天然有音频流而永远不写 sidecar 的问题

**说明**

- 两个新开关均默认关闭；全默认状态下快捷命令集与多版本名称/配色与 1.0.16 一致，其余新功能也全部保持默认关闭（UI 行为包中总集数/角标/合集归属三项按既有决策默认开启）
- 构建 0 error（仅保留既有 `CS0618 LibraryApi.IsFolder` 警告）；自动化验证工程 749 条用例全过（1.0.16 为 518 条，backport 批次累计 +194，两个开关 +24，回归修复 +13）

**回归修复（完整补验发现）**

- 本地 TMDB 缓存写回格式错误：被动缓存把嵌套对象/数组/数字/布尔写成 `{id: 18, name: Drama}` 这种非 JSON 文本，缓存命中时把该文本原样返回给 Emby，`MovieDbProvider` 在 `AddGenre(null)` 抛异常并丢弃整条响应（表现为 patches / 分级回退在缓存命中后完全失效）。改为用 `System.Text.Json` 按字节级复制原文档再追加 `_meta`，新增 13 条断言
- `deep.delete` 通知是死代码：`DeepDeleteSendNotification` 从未被调用、通知类型也未注册。现在深度删除成功后向全部用户发送，并注册到通知类型列表
- 合集条目增删通知（`collection.items.added` / `collection.items.removed`）永不触发：Emby 的合集成员变更走 `ICollectionManager.ItemsAddedToCollection` / `ItemsRemovedFromCollection`，条目的父级仍是库而不是 `BoxSet`，原来的 `e.Parent is BoxSet` 分支不可能命中。改为订阅 `ICollectionManager` 事件，按变更条目逐条发送

## 1.0.16

**安全与可靠性**

- 自动更新下载无校验：`UpdatePluginTask` 原来把网络流直接 `FileMode.Create` 写进线上主 DLL，下载被截断、代理/ CDN 返回错误内容或 GitHub 返回错误文件时都会把插件写成半成品。现在先下载到插件目录下的临时文件（`StrmAssistantLite.dll.<guid>.download`），流式算 SHA256，与 GitHub release asset API 返回的 `digest`（`sha256:...`）比对；只有一致才进入安装步骤。release asset 没有可用 digest 时同样拒绝安装，不会退回到 manifest 里那个「目录包 zip 的 MD5」（两者校验对象不同）
- 校验失败的处理：删除临时文件、现有 DLL 保持原样，ActivityLog 记 `PluginUpdateFailed`（Error，Overview 带期望/实际哈希）、管理员通知、计划任务标为 Failed；临时文件在 `finally` 中清理，异常中断也不会遗留
- 覆盖前不再直接丢弃旧版本：先把当前主程序复制为 `<plugins>/StrmAssistantLite.dll.bak-<旧版本>-<UTC 时间戳>`，保留最近 3 份，超出的按文件名里的时间戳自动清理；备份失败（例如同名备份已存在）直接中止安装，不覆盖
- 原子安装与回滚：校验通过的临时文件用 `File.Replace` 整体替换目标（同一目录、同一文件系统），替换失败时从备份恢复原文件；失败路径同时保留临时下载供排查。全新安装（目标不存在）则直接移动，不需要备份
- 「已是最新」不再每周给管理员发通知：这只是正常结果，改为 Info 日志；版本号无法解析（例如 tag/程序集版本不是合法版本串）由 Info 升为 Warn，不再静默跳过

**说明**

- checksum 来源选择方案 A（GitHub release asset API 的 `digest`）：发版时单个 DLL 资产会由 GitHub 计算 `sha256:<hex>`，实测 v1.0.15 与临时测试 release 的 `StrmAssistantLite.dll` 资产都返回该字段；无需额外上传 `.sha256` 文件，也不会出现「比对值与被下载文件不同步」的风险。该机制防的是下载损坏/代理篡改/错误资产，不防 GitHub 账号本身被攻破
- 自动化验证工程 518 条用例全过（1.0.15 为 472 条）；新增 `Common/PluginUpdateInstaller.cs`，覆盖 digest 解析与比对、文件 SHA256、备份命名/保留/清理、原子替换、移动失败回滚、备份同名中止、临时文件缺失等 53 条用例
- 线上实测（1.0.16 部署后）：
  - 正常更新：临时发布 v1.0.17 → 任务发现新版本，SHA256 `086dbcdc…` 与资产 digest 一致，生成 `.bak-1.0.16-20261121T082616909Z`，替换成功、任务 Completed；重启后插件正常加载（Harmony self-test passed、搜索索引加载），`/Plugins` 版本随新程序集变化
  - 校验失败：用故意写错期望哈希的测试构建触发，日志为 `Checksum mismatch ... expected sha256:0000…, got sha256:086dbcdc…`，线上 DLL 哈希保持 `5a6ed20b…` 不变、未生成新备份、临时文件已清理，任务状态 Failed，ActivityLog 记录 `PluginUpdateFailed` Error
  - 备份保留：预置 3 份旧备份后执行更新，新建 1 份并删除最旧的 1 份，目录中稳定保持 3 份
  - 测试用的临时 v1.0.17 release 已删除，`/releases/latest` 回到 v1.0.15，线上重新部署回干净的 1.0.16
- 构建 0 error（仅保留既有 `CS0618 LibraryApi.IsFolder` 警告）；未改动 UI 文案、资源文件、依赖与 `search-index.json`

## 1.0.15

**修复**

- 视频缩略图全部失败（严重）：Emby 4.10.2 的 `ThumbnailGenerator.RefreshThumbnailImages` 已是 10 个参数（在 `extractImages` 与 `saveChapters` 之间新增 `replaceExistingImages`），插件仍按 9 个参数反射调用，每次 Invoke 都抛 `TargetParameterCountException`，日志里每条目都是 `Item failed`。现在按 `GetParameters().Length` 自适应 8 / 9 / 10 三种签名（8 参数无 `mediaSource`，9 参数为 4.9.36+，10 参数为 4.10.2）；`replaceExistingImages` 传 `false`，依据是 Emby 自带 `ChapterImagesTask` 在 4.10.2 IL 中传 `ldc.i4.0`，且该参数用于覆盖已存在的章节图，本任务只处理没有章节图的条目；未知参数数量不再盲目 Invoke，记限流 Warn 并跳过
- 计划任务把「全部失败」当「完成」：Emby 只在 `Execute` 抛异常时标记失败，原实现逐条 catch 后正常返回，缩略图任务 6/6 失败仍显示完成。新增 `ScheduledTaskFailurePolicy`，任务统计 succeeded / failed / skipped，只要有失败且没有任何成功就抛异常，让 Emby 标记该次运行失败。跳过不再抵消失败（100 条失败 + 1 条跳过仍算失败）；全部跳过且无失败仍是正常结束（恢复模式 / 黑名单可以合理地处理 0 条）；部分成功 + 部分失败保持「完成」，避免单条坏条目把整轮结果标成失败。逐条 `OperationCanceledException` 区分「任务被取消」（不报错）与「单条超时 / 独立取消」（计入 failed）；`DownloadSubtitles` 用 `SubtitleDownloadOutcome` 区分「无结果 / 库禁用」与「字幕提供方不可用」，豆瓣非 404 的请求失败与限流重试放弃改为抛出并计入 failed，不再静默当成 notFound。已接入缩略图、媒体信息、片头指纹、剧集/影片刷新、影片元数据刷新、外挂字幕、字幕下载、媒体信息持久化、ISO 提取、拼音排序、豆瓣缓存、人员刷新、剧集标题补全、片头指纹导入/导出与插件更新任务
- 5 部 BDMV/strm 影片永远没有媒体信息：`BDMV/STREAM` 里只有 `.strm`（内容形如 `http://172.17.0.1:9527/d/xxx.m2ts?/00000.m2ts`），`FindMainVideo` 只认 `.m2ts/.mts/.vob`，每 6 小时记一次 `No playable video found`。现在 `DiscMainVideoResolver` 在 STREAM 没有真实片源时读取 `.strm` 第一行 URL，`ExtractAsync` 对该 URL 走 ffprobe / Emby HTTP 探测；本地 BDMV 根目录仍用于容器类型与大小。实测容器内 ffprobe 可读出这 5 部的 h264 + 2×DTS + 5×PGS；这些目录没有 `PLAYLIST`，章节仍为空。第 6 部《前目的地 (2014)》的片源 URL 本身已损坏（只返回 485 KB、无任何流），不是解析问题，需另行更换片源
- 豆瓣请求失败日志不可见：`DoubanSupplement - Request failed: Forbidden` 原先只记 Debug，默认级别下无法判断豆瓣补全是否在工作。现在改为 Warn 并用 `LogLimiter` 按 5 分钟一条限流
- 中文搜索真实失败只记 Debug：索引匹配、原生搜索、可见性查询的非取消异常改为 Warn + 5 分钟限流；查询超时/取消仍记 Debug，避免刷屏

**配置**

- 片头探测客户端范围：线上 `IntroSkipOptions.ClientScope` 由 `Emby,Infuse,SenPlayer` 改为 `Emby,Infuse,SenPlayer,Rex`，覆盖实际在用的 `Rex-Standard/1.2`（匹配逻辑为不区分大小写的包含匹配）。代码默认值不变

**说明**

- 默认配置下功能行为不变；设置文件与数据文件格式不变
- 构建 0 error（仅保留既有 `CS0618 LibraryApi.IsFolder` 警告）；自动化验证工程 472 条用例全过（1.0.14 为 361 条）
- 线上验证计划：部署 1.0.15 后运行一次 `Extract Video Thumbnail` 与 `Extract MediaInfo`，确认缩略图无 `TargetParameterCountException`、5 部 BDMV 影片出现 MediaStreams 且不再每 6 小时重试；《前目的地 (2014)》待更换片源后单独复验；片头探测在 `Rex` 客户端播放后出现 `ClientScope` 命中日志

## 1.0.14

**修复**

- 入库自动提取去重（1.0.11）：`PendingMediaInfoItemIds` 原先在工作线程出队时就清除标记，而追更模式在 `ItemAdded` 时直接入队、不登记标记。追更先入队、入库自动提取 15 秒后再入队，而工作线程在这中间已经出队时，同一条目会被二次入队并重复处理。现在标记改为「入队时登记、处理结束（成功 / 跳过 / 失败）时清除」，出队不再清除；新增 `QueueManager.TryEnqueueMediaInfo` 统一追更、入库自动提取、收藏追更、指纹交接、章节补提取等全部入队路径；同一 `ItemAdded` 中追更先登记，自动提取在延迟前读取标记，已被追更认领就直接返回；被库范围 / 收藏 / 黑名单过滤掉的条目同轮释放标记，取消或取许可失败回滚的条目保留标记以便重试
- 中文搜索拼音首字母查不到（1.0.12，issue #3「拼音无效」）：多音字过去只按词组词典选出的单一读音建立索引，例如「凡人修仙传」只存 `fanrenxiuxianchuan` / `frxxc`，用户输入同样合法的 `frxxz` 时首字母因为不参与模糊匹配直接返回 0。现在 `SearchTextHelper.GetPinyinVariants` 用 `WordsHelper.GetAllPinyin` 为每个多音字生成所有读音组合，全拼与首字母一并入索引；词组词典读音仍排第一，单条标题最多 16 种拼写。索引格式升至 `Version=5`，升级后自动重建一次
- 快捷菜单整体保护：`ShortcutMenuHelper` 的 `strmAssistantCommandSource` 与 `dataExplorerCommandSource` 两个 `getCommands` 入口增加 try/catch，单个命令源构建命令时异常只回退本源的命令列表（返回空），不再拖垮影片/剧集详情页右键菜单的渲染；上一个版本只修了「删除版本」那一处判空
- 多版本续播旧格式记录过户（B1）：`IsSameRecordVersion` 的宽松放行分支在存量记录没有任何 id 字段、只能靠 `VersionKey`/`VersionSignature` 指纹标识时，会把记录复用到**当前播放的任意版本**——切到同内容另一版本播放时 A 版本的记录被移键到 B 下，A 的独立断点永久丢失。现在仅指纹记录必须与当前播放版本的指纹匹配才复用，不同则新建记录；已有 id 精确比对不变
- 多版本续播片长未知夹取（B2）：`ResumePositionMath.ConvertPosition` 在源片长未知（`sourceRuntime<=0`）时直接返回原始断点，跳过目标片长夹取，170 分钟剪辑的 80 分钟断点套到 100 分钟版本上越界（客户端归零从头播）。现在该分支也返回 `Math.Min(positionTicks, targetRuntime)`；无目标片长时保持直通
- 损坏的库范围值静默扩权：`GetValidLibraryIds` 原来把「所有段都解析失败」（选项文件损坏成 `abc`）与「未配置范围」都映射成空数组，而空数组在 `ResolveScopePaths` 里等于全库放行，损坏配置会静默把范围放大到全部库。新增 `LibraryScopeParser` 区分 Empty / WellFormed / Malformed：Malformed 记 Warn（含原始值）并由 resolver 返回不可匹配的哨兵前缀，所有调用方安全失败；正常配置行为不变
- 刷图目录查询窗口竞态：`OverrideImageMatcher.GetFiles` 的 Exists → GetLastWriteTimeUtc → GetFiles 不是原子操作，目录在两步之间消失/失权时抛 `DirectoryNotFoundException` / `UnauthorizedAccessException`，只能靠外层静默跳图。现在三步合并进一个 try，按空目录处理且不缓存 miss
- 分布式媒体根缓存过期窗口：worker 端缓存虚拟文件夹 5 分钟，新增库后其下文件最长 5 分钟被判 `path_not_allowed`。TTL 降到 30 秒，新增库最长 30 秒可用（不依赖不稳定的库事件）

**性能**

- 本地元数据刷图目录枚举去重（1.0.13）：每个剧集条目的 Added/Updated 都会对同一 `<tmdbid>/images` 目录重复 `Directory.GetFiles` 再逐个正则匹配。新增 `Common/OverrideImageMatcher.cs` 统一命名匹配规则，并按目录缓存文件清单（2 分钟 TTL，目录最后写入时间变化立即重读；缺失目录不缓存，最多 512 条）。命名规则与原实现一致，补 44 条边界用例
- 高频失败日志限流：拼音/繁简转换失败、本地发现 UDP 收包失败按 key 5 分钟记一条带堆栈的日志，避免故障时刷屏

**可维护性**

- 异常日志带上堆栈（1.0.13）：全仓三连写法 `Error(上下文)` / `Error(e.Message)` / `Debug(e.StackTrace)` 收敛为 `ErrorException(上下文, e)`，或单条日志内联堆栈；即使 `DebugMode` 关闭，真正的错误也保留完整堆栈；`QueueManager` 四个队列主循环与冷却延迟的 `catch { break; }` 收窄为 `OperationCanceledException` + 记录 `Exception`；播放驱动的高频后台更新失败用新增的 `Common/LogLimiter.cs` 限流
- 静默 catch 收紧（第二轮）：`Common/DistributedExtractApi.cs` 19 处、`Common/MovieDbCache.cs` 9 处，以及全仓其余无日志 catch 一并处理。正经处理链路的失败记 `ErrorException` / Warn 并带上下文，探测与回退路径至少记一条带堆栈的 Debug；取消与纯回退路径用 `// expected:` 注明原因。全仓 C# catch 从「168 处无声」降为「42 处显式注明的取消/回退」，其余全部有日志、rethrow 或说明；`UiEnhancementScript.cs` 中 JS 字符串内的 try/catch 未改动
- 队列回归测试：把入库去重的准入与 pending 标记规则抽到无 Emby 依赖的 `Common/MediaInfoQueueAdmission.cs`，`QueueManager.TryEnqueueMediaInfo` / `RequeueMediaInfo` 与处理完成/过滤释放全部委托给它；验证工程新增 26 条用例覆盖「入队登记、重复拒绝、defer 保留、完成清除、过滤释放、入队失败回滚、Initialize 清空」
- 豆瓣评分表有界清理：评分条目原先只增不减。新增 `Common/DoubanRatingPruner.cs`，删除 180 天前的条目；仍超过 5 万条时按更新时间剔除最旧数据。阈值刻意放宽，避免删掉还需要重新请求的近期评分；`Load()` 后与 `Save()` 时（最多每 6 小时或超限时）执行
- 删除无读取方的 `QueueManager.IsMediaInfoProcessTaskRunning` 死标志（原有 4 处赋值、0 处读取）

**新增**

- 单字中文搜索（默认关闭）：搜索设置新增「单字搜索」开关。开启后单字查询（如「蛛」）可包含匹配到「蜘蛛侠」，排在精确/前缀匹配之后。索引只在开关开启时构建 CJK unigram 层（仅标题类 key，总 posting 上限 200 万条），单字 posting 超过 4000 条时跳过，极常见字仍只走精确/前缀，避免候选爆炸；索引文件格式不变，切换开关只重建内存 lookup，无需重建索引

**说明**

- 默认配置下功能行为不变；除搜索设置新增「单字搜索」开关（默认关闭）及其三语资源外，未改动既有 UI 文案与资源；设置与数据文件格式不变
- 升级后中文搜索索引会重建一次（索引格式升至 `Version=5`）
- 构建 0 error（仅保留既有 `CS0618 LibraryApi.IsFolder` 警告）；自动化验证工程 361 条用例全过（1.0.13 为 267 条）
- 快捷菜单 JS 保护层仅在异常时生效，正常路径返回结果不变

## 1.0.10

**性能与稳定性**

- 中文搜索索引：构建时直接保存已算好的 key，并分别建立精确、前缀、包含三组候选结构；索引持久化后重启直接加载，不再重算。查询改为走候选集合并带 5 秒超时与取消支持。索引格式升至 `Version=4`，旧索引会自动重建一次
- 定时任务分批物化：全库任务改用 `GetInternalItemIds`，每批 500 条读取并处理，不再把整库条目一次性读进内存
- 选项解析缓存：排除容器、扩展名、黑名单等按选项串缓存，通配符正则预编译，不必每个条目重新解析
- 入库自动提取去重：改用 `ConcurrentDictionary`，去掉原来的 O(N²) 线性扫描
- 并发控制：新增 `Common/AsyncSemaphore.cs`，调整并发上限时直接在原对象上 `SetLimit` 调速，不再热替换 `SemaphoreSlim`；修复调整过程中可能出现的 `ObjectDisposedException`、计数膨胀和排队条目静默丢失
- 章节 / 指纹扫描去掉二次方查找：`SeasonHasIntroCredits`、`PopulateIntroCredits` 先建 id 集合再按 id 查询；片头指纹的每库分钟数每次运行只解析一次，并按季缓存条目所属库
- 库范围解析统一：新增 `Common/LibraryScopeResolver.cs`，把「范围 id 解析 → 虚拟文件夹 → 收藏夹 → 路径前缀 → 黑名单 → 去重」这段重复逻辑从十余个调用点收敛到一处，路径比较统一为 `OrdinalIgnoreCase`；各调用点自己的库过滤与队列去重键保持不变
- `.strm` 挂载：`ExclusiveExtract` 的探测钩子不再用 `Task.Run(...).Result` 同时占用两个线程，改为在调用线程解析；同一路径最近 5 分钟内的结果直接复用（最多 256 条）；`GetStrmMountPath` 改为接受调用方的取消令牌
- 豆瓣评分 / 播放记录落盘：不再在持有状态锁时序列化整表，改为锁内标脏、锁外快照与写盘，防抖定时器合并连续变更，宿主关闭时冲刷剩余脏数据；写入先写 `.tmp` 再 `File.Replace` 原子替换（文件系统不支持重命名时回退为覆盖），读者不会看到写了一半的文件。JSON 结构、文件名与加载路径不变
- 代理请求：按代理地址与凭据缓存 `HttpClient`，复用连接与 TLS 握手；单个请求的超时改为链接调用方令牌，不再依赖 `HttpClient.Timeout`
- 定时任务空转：媒体信息持久化、影片刷新、剧集刷新、影片元数据刷新、外挂字幕扫描、清理章节标记等任务的 `Task.Delay(10).Wait()` 改为 `await`，不再每处理一条就阻塞线程 10 毫秒

**变更**

- 插件中文显示名由「Strm 助手」改回「神医助手」，繁体为「神醫助手」。涉及资源键 `Plugin_Name`（插件列表名称）与 `PluginOptions_EditorTitle_Strm_Assistant`（插件页面标题 / 侧栏菜单名），以及 `manifest.json` 的 `name` 与 README 文案
- 英文名仍为 `Strm Assistant`（中性资源与 `README.en.md` 不变），设置文件名 `Strm Assistant.json` 不变

**修复**

- 快捷菜单「删除版本」：条目没有 `MediaSources` 时直接读取 `.length` 抛 `TypeError`，导致影片/剧集详情页的右键菜单整体渲染失败；现先判空再读取
- 用户缓存：`AllUsers` 原为普通 `Dictionary`，用户增删事件与队列线程、通知发送、设置页的枚举并发进行，写操作可能撕裂枚举；改为 `ConcurrentDictionary`，刷新时移除已删除或已禁用的用户
- 随机数：队列管理器、刷新任务与豆瓣客户端在多线程上共用一个 `Random` 实例，并发 `Next` 可能破坏内部状态；新增 `Common/ConcurrentRandom.cs`，每个线程各持一个实例
- 播放会话泄漏：原先只在 `PlaybackStopped` 时清理，客户端崩溃或断连会让 `_playSessionData` 与两个节流表一直留到插件重启；新增 30 分钟巡检，清理 2 小时无活动的会话与 1 小时未更新的节流记录

**文档**

- 新增 `docs/optimization-review-2026-11-16.md`：2026-11-16 代码优化静态审查，记录 10 项问题、影响与建议，本轮修复其中 5 项

**说明**

- 功能行为不变，均为显示名、性能与稳定性改动；设置文件与数据文件格式不变
- 升级后搜索索引会重建一次（索引格式升至 `Version=4`）
- 已在运行中的 Emby 实跑验证：启动无异常、六种中文搜索命中数与旧版一致、`RefreshEpisode` 96 条全部通过、未出现 OOM
- 构建 0 error，仅保留既有的 `CS0618 LibraryApi.IsFolder` 过时警告
- 自动化验证工程 191 条用例全过，并在运行中的 Emby 上完成部署验证

## 1.0.9

**修复**

- 中文界面回退英文：插件主程序集由 Emby 按字节加载，卫星资源（`zh` / `zh-hant`）里的清单名本身带文化后缀，旧逻辑把后缀截掉后再去构造 `ResourceManager`，永远查不到条目而回退英文。现在直接以清单流构造 `ResourceSet` 并按语言缓存（感谢 @拾忆 定位）
- 启动日志刷屏：`InternalGetResourceSet` 与程序集解析各留了一条 `Info` 级调试输出，每次启动要写近 90 行，已降为 `Debug`

**说明**

- 仅资源加载与日志级别改动，功能行为不变
- 已核对 `zh-CN` → 您确定要继续吗？、`zh-Hant` → 確定要繼續執行？

## 1.0.8

**修复**

- 多版本续播取不到换算断点：同一部影片在库里被拆成两条条目、或一个条目挂多个版本时，次要版本过去一律回退到 Emby 原生断点。现在目标版本没有自己的记录时，会取同一影片其它版本的最近未看完进度按片长比例换算并落盘（跨条目与同条目多源都生效）
- 杜比视界 P7 探测把失败当结论：`ffprobe` 退出码非 0 且没有有效输出（例如输入不可读时只打印 `{}`）时，现在返回「探测失败」，与「确实不是 P7」区分开；退出码非 0 但输出含有效流信息时照常采用，避免误杀

**说明**

- 按版本保存播放断点仍默认关闭，升级后行为不变
- 自动化验证工程扩到 183 条用例（新增多源换算、ffprobe 退出码两组）

## 1.0.7

**新增**

- 多版本续播：新增「按版本保存播放断点」（默认关闭）。开启后断点按（条目, 版本）分别记录，切到没有断点的版本时按片长比例换算一次并各自落盘；「已看完」状态仍跨版本共享；旧的单一断点记录自动迁移，不丢进度
- 自动化验证工程 `tests/StrmAssistant.Verification`：覆盖断点换算数学、第三方源阶梯退避、杜比视界 P7 判定（含真实 ffprobe 样本），63 条用例

**改动**

- 断点换算逻辑抽成 `ResumePositionMath.ConvertPosition`，行为不变，便于回归

**说明**

- 本轮是纯合入 + 验证，所有开关默认关闭，升级后行为与 1.0.6 一致
- 多版本续播的真机验收（Web / 手机 / TV 切版本）尚未进行，暂不建议开启

## 1.0.6

**改进**

- 豆瓣 / IntroDB / OpenSubtitles 等第三方源连续失败时改为阶梯退避：10 分钟 → 30 分钟 → 2 小时 → 6 小时，成功后清零；不再固定 10 分钟就重新撞限流
- 杜比视界 P7 的分层检测优先使用插件数据目录 `bin/ffprobe`、`/config/bin/ffprobe` 或设置页里的自定义路径，避免依赖 GitHub 下载

**文档**

- README 的「已知限制」拆成「设计边界（有意为之）」与「已知限制」两部分；不接管 Emby 转码属于设计边界，不再当作缺陷列出
- 安装说明同步 ffprobe 的体积说明（约 15 MB 压缩包 / 36 MB 二进制）与退避策略

## 1.0.5

**新增**

- 缺主图视频自动补封面：媒体信息提取处理完仍没有 Primary 的视频时，插件自己沿 strm/shortcut 直链用 ffmpeg 抽一帧（默认 30% 位置，宽度 600，均可在设置页调整），存为条目封面。适合私人资源这类刮削不出海报的库
- 新条目自动处理：开启「入库即提取」后，新条目挂上库里就自动排队做媒体信息与封面（默认关闭）；同时 `Extract MediaInfo` 任务默认每 6 小时兜底跑一次

**修复**

- 抽帧封面不生效：Emby 4.10 的 `SaveImage` 只把图片写进元数据目录、不落库，条目依旧「没有封面」并反复重抽。现在抽帧后显式提交图片更新并回读校验，失败时回退为写媒体目录 `<文件名>-poster.jpg` 再登记
- 设置页「关于」里版本行显示不全（`1.0.5.0+emby.4.10.2.0` 被截断），现只显示纯版本号

**说明**

- 封面抽帧与「入库即提取」默认关闭，升级后行为不变，需要时在「媒体信息提取」里逐项打开
- 已合并 1.0.4 的全部修复（首页卡死、插件加载失败等）

## 1.0.4 — 待发布

**修复（重要，1.0.3 请勿使用）**

- 首页一直转圈：注入的前端脚本以 Emby 的 `shortcuts.js` 模块形式下发，模块内抛异常会让依赖它的 `emby-itemscontainer` 拿到 `undefined`（报 `Cannot read properties of undefined (reading 'default')`），整个首页渲染卡死。现整段注入代码加 try/catch 与 DOM 就绪保护，异常只输出一条 warn
- 插件加载失败（设置页 500、插件列表消失）：片头探测的「用户范围」被写成用户 GUID（如 `612d58eb…`），而代码用 `long.Parse` 解析，抛 `FormatException` 导致插件构造失败。现改为字符串比较，且任何解析异常都不再中断插件启动
- 顺带清空受影响的 `UserScope` 配置并关闭被意外打开的片头探测

**说明**

- `v1.0.3` 存在上述两个问题，已在 Release 页标注，请升级到 1.0.4


## 1.0.3 — 待发布

**修复**

- 「检查更新」按钮的提示与实际行为不一致：原来任务执行完就显示「已是最新版本」，但实际可能刚下载了新版本（需重启才生效）。现在先查 GitHub 最新版本再决定动作：
  - 无新版本 → 「已是最新版本 (x.y.z)」，不触发下载
  - 有新版本 → 「发现新版本，正在下载…」→ 完成后提示「已下载新版本，请重启 Emby 生效」


## 1.0.2 — 待发布

**新增**

- 插件设置页左下角新增「检查更新」按钮：点击后直接触发内置的 `Update Plugin` 任务，轮询任务状态并显示结果（已是最新版本 / 已更新请重启 / 失败原因），不必再去「计划任务」里手动找

- 关于区版本行只显示版本号（如 `v1.0.2`），两处入口文案缩短为「GitHub」「功能说明」，避免窄屏被截断
- 版本显示简化为 `v1.0.1 · Emby 4.10.2`，不再输出内部构建标记与提交哈希

**说明**

- 仅在「设置 → 高级 → Strm 助手」页面显示，不影响其他页面
- 按钮调用的是同一个更新任务，逻辑与自动检查一致（比较仓库 Release，下载主 DLL）



版本号即插件在「设置 → 插件」中显示的版本；升级可用插件内置的计划任务 `Update Plugin`，或手动替换 DLL。

## 1.0.1 — 2026-11-07

首个公开构建（Emby 4.10.2 / `4.10.2.0`）。

**功能**

- 中文本地化（简体 / 繁体），插件列表与设置页均显示中文
- 中文搜索：拼音全拼与首字母、繁简互搜、别名与演员检索、分级排序、索引重建任务
- 本地 TMDB 元数据源：`cache/` + `override/` 四模式、`episodegroup.json`、override 图片自动刷入、`tags.json` 自定义标签、统一内容分级、自定义特别篇顺序
- 豆瓣辅助：简介 / 海报补全、演员导演、季集、推荐频道、预告片、评分角标、缓存预构建
- 多版本：智能排序、跨库版本进度与收藏分离、库条目数角标、剧集总集数
- 媒体信息：ISO / BDMV 提取、DVD IFO 章节、DV P7 双层检测（MEL / FEL 徽标）、HDR Vivid、Atmos / DTS:X、截图比例矫正、自定义 ffmpeg
- 片头片尾：并行声纹、Unified IntroDb、第二数据源
- 字幕：STRM 场景优化、外挂字幕与外挂音轨扫描
- 通知与体验增强：元数据/图像更新通知、快捷菜单清理、倒序季集等
- 可选 rffmpeg sidecar 与分布式媒体信息提取

**修复**

- 插件显示名为中文后，`User-Agent` 含非 ASCII 字符导致所有外部 HTTP 请求失败（自动更新、元数据、豆瓣等受影响）
- 自动更新检查误指向上游仓库，可能被上游版本覆盖；且版本号非数字导致比较异常
- 帮助与关于页的跳转链接改指本仓库
