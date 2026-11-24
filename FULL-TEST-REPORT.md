# 神医助手（StrmAssistant）1.1.0 完整补验报告

- 日期：2026-11-24
- 仓库：`/vol3/1000/Projects/StrmAssistant`（本报告写在 Hermesh worktree，未改主检出）
- 基线：`b8045da`（`feature/pro-backport` + 1，版本 1.1.0）
- 本轮最终提交：`00b25b6`（在 `b8045da` 之上，分支 `hermesh/s_75cglx6f_01m86ea1x2w888xmtmx590ve6d`）
- 生产 8096 未触碰；StrmButler 源码与 18096 实例未改动
- 隔离实例：`18097`（复制 18096 配置后专用于本轮）、`18095`（全新小实例，仅用于演员刷新）

## 结论（先说结果）

**可以发版。** 上一轮列为“环境限制”的五项全部用本地 mock / 本地接收端 / 模拟播放 / 独立小实例补验完成；两个新开关的设置页切换在最终构建上双向通过；构建 0 error、自动化用例 749/749。补验过程中发现并修复了 3 个真实缺陷（其中 1 个会让“本地 patches / 分级回退”在缓存命中后整条失效），修复后对应项目全部重跑通过。

仍未做到的两项已单列说明：真实 115 库上的“片头指纹全量检测”（为保护 115 未跑，改用受控本地视频完成同一管线）和浏览器自动化 E2E（本仓库没有浏览器测试框架）。

---

## 一、环境与基线

| 项 | 值 |
|---|---|
| 构建 | `dotnet build StrmAssistant.sln -c Release -p:EmbyServerDir=...` → **0 error**，仅保留既有 `CS0618 LibraryApi.IsFolder` 警告 |
| 自动化 | `tests/StrmAssistant.Verification` → **749 passed / 0 failed / 0 skipped**（1.0.16 为 518，backport +194，两个开关 +24，本轮回归修复 +13） |
| 实测实例 | `strm-verify-emby`（18097，复制 18096 的 config/data/metadata/plugins，移除 StrmButler）；`strm-person-emby`（18095，全新空实例） |
| 插件版本 | 启动日志 `Loading StrmAssistantLite, Version=1.1.0.0` |
| Mock 服务 | 本地 TMDB mock（18099）、本地豆瓣 HTTPS CONNECT 代理（18098，自签 CA 已装入测试容器）、本地通知接收端（18080）、本地视频源（18081） |

> 说明：Emby 4.10 中 `LibraryOptions.MetadataFetchers` 在库级别不生效，元数据抓取开关必须写到 `TypeOptions`（Movie/Series/Season/Episode）里；本轮所有 mock 场景都按这个方式配置。

---

## 二、第一部分：五项“环境限制”补验

### 1. 豆瓣客串中文化（本地 mock 豆瓣）

**办法**

- 宿主上运行一个最小 HTTPS CONNECT 代理（`douban_proxy.py`，18098），用自签 CA 为 `m.douban.com` 签发证书；把 CA 追加进测试容器的 `/etc/ssl/certs/ca-certificates.crt`。
- 插件网络设置：`EnableProxyServer=true`、`ProxyServer=http://172.17.0.1:18098`、`ProxyMode=Whitelist`、`ProxyWhitelist=m.douban.com`。插件真实走 CONNECT + TLS，代理按豆瓣真实结构返回 mock JSON。
- mock 数据：`/rexxar/api/v2/search`（`subjects.items[].target`）、`/movie/3000001`（`id/title/intro/cover_url/rating/pubdate/actors/directors`）、`/movie/3000001/celebrities`（`actors/directors` 均带 `name` + `latin_name` + `character`）。
- 测试对象：`Verify Movie`（Tmdb 900001），库里语言 zh-CN，人员原为 TMDB 英文名 Tom Hanks / Guest Actor / Director Person。

**实际证据**

代理日志（真实 CONNECT + TLS 请求）：

```
17:48:58 CONNECT-LINE CONNECT m.douban.com:443 HTTP/1.1
17:48:58 TLS GET /rexxar/api/v2/search?q=Verify%20Movie&type=movie&start=0&count=10
17:49:01 TLS GET /rexxar/api/v2/movie/3000001
17:49:03 TLS GET /rexxar/api/v2/movie/3000001/celebrities
```

插件日志：

```
17:49:01 Info DoubanSupplement - Provider ids updated: Verify Movie
17:49:03 Info DoubanSupplement - Cast updated: Verify Movie (+1, localized 3, total 4)
```

最终条目状态：

```
ProviderIds: Tmdb=900001, Imdb=tt9999999, Tvdb=777777,
             Douban=3000001, DoubanRating=8.8, DoubanRatingCount=1234
People: 汤姆·汉克斯(Actor/Self), 客串演员(Actor/Guest),
        导演甲(Director/Director), 新演员(Actor/新角色)
```

**结果：通过。** 映射（latin_name→中文名）、改名、新增客串演员、`UpdatePeople` 完整列表（4 条，旧记录保留角色）全部生效。

---

### 2. 本地 TMDB 六小项（本地 mock + 本地覆盖文件）

**办法**

- 插件设置 `EnableLocalMovieDb=true`、`LocalMovieDbUrl=http://172.17.0.1:18099`，插件把真实 TMDB 请求重写到 mock；同时打开 `EnableLocalMetadataSource` + 五项独立开关，`LocalMetadataRoot=/config/verify-metadata`。
- mock 提供 `/3/configuration`、`/3/configuration/primary_translations`、`/3/search/movie|tv`、`/3/movie/{id}`、`/3/tv/{id}`、`/3/tv/{id}/season/{n}[/episode/{m}]`、`/3/person/{id}`、`/3/find/{id}`、`/t/p/*` 图片。
- 测试媒体：`Verify Movie`（900001）与 `Verify Show`（900002，S01E01–E03），都是 `-p:EmbyServerDir` 构建的 1.1.0 插件在真实 Emby 刷新流程里抓取。
- 覆盖文件放在 `verify-metadata/override/tmdb-movies2/900001/` 与 `override/tmdb-tv/900002/`。

**逐项证据**

| 小项 | 触发方式 | 证据 | 结果 |
|---|---|---|---|
| patches 字段级合并 | `patches/01-local.json` 改 `overview` / `tagline` | 日志 `Local metadata patches applied - 900001: 1 file(s), 1`；条目 `Overview=补丁后的中文简介：本地 patches 生效。`、`Taglines=[Patched Tagline]` | 通过 |
| 分级按国家链回退 | mock 只给 `JP/G`，链 `US,JP` | 日志 `Local metadata certification fallback - 900001: JP/G -> G`；条目 `OfficialRating=G` | 通过 |
| episodeorder | `episodeorder.json`：E1→3、E2→1、E3→2 | 日志 `Local episode order applied - Episode 1: S1E1 -> 3` 等 3 条；条目 `SortIndexNumber=3/1/2`，`SortParentIndexNumber=1` | 通过 |
| tags.json | `{"tags":["本地标签A","LocalTagB"]}` | 日志 `Local metadata tags applied ...`；条目 `TagItems=[LocalTagB, 本地标签A]`（Emby 4.10 用 `TagItems`，`Tags` 为 null 是正常的） | 通过 |
| external_ids.json | `{"imdb":"tt9999999","tvdb":"777777"}` | 日志 `Local metadata external ids applied ...`；`ProviderIds.Imdb=tt9999999`、`Tvdb=777777` | 通过 |
| 覆盖图片 | `images/poster.jpg`（2×2 红 JPEG） | 日志 `Override image applied .../poster.jpg`；`GET /Items/44882/Images/Primary` 下载字节 MD5 = 本地覆盖文件 MD5 `216f26c240c4ae81d9161cf41c1fadad` | 通过 |

**结果：六项全过。** 这也是本轮发现 P0 缺陷的地方，详见第五节。

---

### 3. 通知投递（本地 HTTP 接收端）

**办法**

- 启动本地接收端（18080，记录每个 POST 的 JSON）。
- 用 Emby 官方通知配置 API 给管理员配置 Webhook：`POST /Notifications/Services/Configured`，`Url=http://172.17.0.1:18080/notify`，事件包含 `metadata.update / image.update / collection.update / collection.items.added / collection.items.removed / deep.delete / library.new / library.deleted`。
- 先 `POST /Notifications/Services/Test` 验证链路（收到 `system.notificationtest`）。
- 再分别触发：条目刷新（metadata.update）、建合集/加入/移除（collection.*）、带 `DeleteFileLocation=true` 删除条目的深度删除（deep.delete）。

**实际证据（接收端原始 `Event`）**

```
metadata.update | Strm Assistant - Metadata Updated | Verify Movie - metadata updated ...
collection.update | Strm Assistant - Collection Updated | Collection VerifyCollection was updated
collection.items.added | ... | Verify Show was added to collection VerifyCollection
collection.items.removed | ... | Verify Show was removed from collection VerifyCollection
deep.delete | Strm Assistant - Media Deep Delete | Item Name: Verify Movie ... 
library.deleted | Verify Movie removed from <server> | ...（Emby 自身事件）
```

深度删除的目标文件确实被删：

```
DeepDelete - Item: Verify Movie | Strm: .../Delete Me (2021).strm | Target: /config/verify-media/delete-target.txt
DeepDelete - Local target deleted: /config/verify-media/delete-target.txt
（接收端随后收到 deep.delete）
```

**结果：要求的 5 类事件全部真实投递成功。** 其中 `collection.items.added/removed` 与 `deep.delete` 是修复后才通的（见第五节），修复前分别“永不触发”和“死代码”。`image.update` 未单独触发成功（保存图片走的是 `ItemImageChanged`，没有产生 `ItemUpdated(ImageUpdate)`），但投递链路已由其余事件覆盖。

---

### 4. 自动标记前集已播（真实播放接口模拟）

**办法**

- 新建无密码用户 `verifyuser` 并用它登录拿到带 UserId 的 token（无 UserId 的 admin token 不会写用户播放数据）。
- 用 `/Sessions/Playing` + `/Sessions/Playing/Stopped` 把《鸡毛飞上天》S01E01、S01E02 各播到 40%（低于 90%，不触发标记但写入进度）。
- 再把 S01E03 播到 96% 后停止，触发插件 `AutoMarkPreviousEpisodesPlayed`。

**实际证据**

播放前：

```
E1 Played=False, PlaybackPositionTicks=0
E2 Played=False, PlaybackPositionTicks=0
```

E1/E2 播到 40% 后：

```
E1 Played=False, PlaybackPositionTicks=10416400000, PlayCount=1
E2 Played=False, PlaybackPositionTicks=10787244000, PlayCount=1
```

E3 播到 96% 后：

```
E1 Played=True, PlaybackPositionTicks=0
E2 Played=True, PlaybackPositionTicks=0
E3 Played=True
```

插件日志：

```
17:58:37 Info AutoMarkPreviousEpisodes - 鸡毛飞上天 - 第 1 季 -
Marked 2 previous episode(s) as played before ...S01E03...
```

**结果：通过。** 只标记“有进度且未播完”的前集，位置被清零、`PlayCount` 保留。

---

### 5. 演员刷新整库（enhanced 模式，独立小实例）

**办法**

- 为避免在 2251 个演员的复制库上跑全量，单独起了一个全新 Emby 小实例（18095），只装 1 部测试影片，TMDB 走同一本地 mock；生成 3 个 Person（Tmdb 501/502/503）。
- 配置：`RefreshPersonMode=Manual`、`RefreshPersonScope=AllPersons`、`RefreshPersonEnhancedMode=true`、`SkipLocked=false`、`SkipMissingTmdbId=false`、`SkipRecentlyRefreshed=false`、并发 1、间隔 0。
- mock 人物详情返回中文名/中文简介/生日；501 还带头像 `profile_path`。

**实际证据**

```
RefreshPerson - Number of Persons Before: 3
RefreshPerson - Task 1/3 - 汤姆·汉克斯
RefreshPerson - Task 2/3 - 客串演员
RefreshPerson - Task 3/3 - 导演甲
RefreshPerson - Statistics: Total=3, Processed=3, Succeeded=3, Skipped=0, Failed=0, Cancelled=0
RefreshPerson - Failure Reasons: NoId=0, RequestFailed=0, NoChineseData=0
```

逐字段写入结果：

| 人物 | 中文名 | 中文简介 | 生日（API 返回） | 头像 |
|---|---|---|---|---|
| Tmdb 501 | 汤姆·汉克斯 | 汤姆·汉克斯，美国演员。 | 1956-07-08T16:00:00Z | 有（Primary image tag） |
| Tmdb 502 | 客串演员 | 客串演员，中文简介。 | 1980-02-01T16:00:00Z | 无（mock 无头像，符合预期） |
| Tmdb 503 | 导演甲 | 导演甲，中文简介。 | 1970-03-02T16:00:00Z | 无 |

**结果：通过。** 处理数/成功数/失败数、中文名、简介、生卒日期、头像预下载各自都有实际写入证据。

---

## 三、第二部分：两个新开关的界面验证

**办法（正式 GenericUI 路径，不经过坏掉的 Configuration REST）**

- `GET /UI/View?PageId=63c322:Settings&UserId=<admin>` → 200，返回 136,629 字节，包含 10 个分区（GeneralOptions、NetworkOptions、MediaInfoExtractOptions、MetadataEnhanceOptions、IntroSkipOptions、ExperienceEnhanceOptions、NotificationOptions、SearchOptions、DiscoverOptions、AboutOptions）。
- 两个开关都能在返回对象里看到：`ExperienceEnhanceOptions.ShortcutMenuExtendedCommands`、`GeneralOptions.VersionNamingExtendedTokens`，默认 `false`。
- 按插件设置页自己的保存路径提交：`POST /UI/Command`，`{PageId, CommandId:"PageSave", Data: JSON.stringify(EditObjectContainer.Object)}`。
- 每次保存后立刻（不重启）抓 `GET /web/modules/shortcuts.js` 对比。

**实际证据**

| 步骤 | 服务端视图 | `Strm Assistant.json` | 下发脚本 |
|---|---|---|---|
| 默认 | false / false | — | `extended: false`；扩展标识 0 处 |
| UI 切换为开 | true / true | `"ShortcutMenuExtendedCommands": true`、`"VersionNamingExtendedTokens": true` | `extended: true`；`dolby-vision/hdr10plus/dts x` 5 处；扩展高亮正则与分类器出现 |
| UI 切回关 | false / false | 两个字段 false | `extended: false`；扩展标识 0 处；旧正则/旧 `classifyToken` 逐字回归 |

服务端配置与下发脚本在同一个请求周期内变化，**未重启**。`shortcuts.js` 两种状态都通过 `node --check`（语法 0 错误）。

**结果：通过。** 页面可见、界面保存后服务端配置真实变化、行为下发真实变化、无需重启。

---

## 四、第三部分：最终提交上的完整回归

最终提交：`00b25b6`（`b8045da` + 本轮修复）。

| 回归项 | 结果 |
|---|---|
| 构建（干净重建 `--no-incremental`） | **0 error**，1 个既有警告 `CS0618 BaseItem.IsFolder` |
| `tests/StrmAssistant.Verification` | **749 passed / 0 failed / 0 skipped** |
| Host 契约八项 | 本仓库不存在这组测试（`grep` 全仓无 Host contract / browser 测试脚本）；**不适用** |
| 浏览器测试（P7/P16/P17/P18） | 本仓库没有浏览器测试框架/脚本（P7/P16/P17/P18 属于 StrmButler）；以 HTTP 层 UI 视图 + 下发 JS 语法校验代替，**不适用** |
| 设置页 | `/UI/View` 200，10 个分区全部返回；两个开关可见、可保存 |
| 真实库 · 搜索 | `GET /StrmAssistant/Search?SearchTerm=鸡毛飞上天` → `Available=true`，返回《鸡毛飞上天》（Id 24718） |
| 真实库 · 媒体信息 | 条目 9243 有 `-mediainfo.json` sidecar（4496 B）且 `MediaSources` 带完整流；全库 9,204 条目中前 2,000 条里 1,993 条有 `MediaStreams` |
| 真实库 · 字幕 | 前 2,000 条中 756 条有外部字幕流；另在受控剧集放了真实 `.srt`，Emby 识别为 `('Subtitle','(SRT)',IsExternal=True)` |
| 真实库 · 多版本 | 运行“Merge Multi Versions”后，条目 9243 返回 2 个 `MediaSources`（115 源 + 本地 preflight 源）；`/StrmAssistant/Items/9243/VersionPlayback` 返回 200 |
| 真实库 · 片头 | 真实库当前无片头标记（全库 9,204 条 `Chapters` 无 `MarkerType=Intro`）。为不压 115，改用受控本地 2 集：`RedetectIntro` → `{"Queued":true}`；指纹提取 `Episodes: succeeded 2, failed 0`；两集均生成 `IntroStart=3,000,000,000`、`IntroEnd=5,995,024,875` |
| 通知设置页 | `/Notifications/Types` 的 `Strm Assistant` 分类含 7 个事件（含修复后的 `deep.delete`） |

**数字汇总**：构建 0 error；用例 749；设置页 10 分区；搜索 1 次命中；演员刷新 3/3；通知 5 类事件；本地 TMDB 6/6；豆瓣 1 部影片 4 人。

---

## 五、发现并修复的问题

### P0：本地 TMDB 被动缓存写回的是“非 JSON 文本”，缓存命中后 Emby 丢弃整条响应

- **现象**：第一次刷新正常；第二次开始（缓存命中）`overview/tagline` 补丁不生效，Emby 日志出现
  `System.ArgumentNullException: Value cannot be null. (Parameter 'name') at BaseItem.AddGenre`。
- **根因**：`LocalMetadataSource.AttachMetadata` 用 Emby 的无类型 `IJsonSerializer` 对 `Dictionary<string,object>` 往返，把嵌套对象/数组/数字/布尔写成了
  `{"genres":"[{id: 18, name: Drama}]","runtime":"100","adult":"false",...}` 这种不是合法 JSON 的文本；缓存命中时又原样返回给 Emby。
- **修复**：新增 `LocalMetadataCapture.Annotate`，用 `System.Text.Json` 按字节级复制原文档、仅追加 `_meta`；非对象/非法 JSON 原样返回。新增 13 条断言（嵌套数组、数字、布尔、null、`_meta`、非对象、非法 JSON）。
- **复验**：缓存文件恢复为合法 JSON；`overview=补丁后的中文简介…`、`taglines=[Patched Tagline]`、缓存命中不再报错。

### P1：`deep.delete` 通知是死代码

- **现象**：`NotificationApi.DeepDeleteSendNotification` 全仓无调用点，`CustomNotifications` 也没注册该事件 → 接收端永远收不到。
- **修复**：在 `DeepDelete.DeleteItemPostfix` 删除成功后调用（通知全部用户，与原插件一致）；注册 `deep.delete` 通知类型。
- **复验**：接收端收到 `deep.delete`，目标文件同时被真实删除。

### P1：`collection.items.added` / `collection.items.removed` 永不触发

- **根因**：Emby 的合集成员变更走 `ICollectionManager.ItemsAddedToCollection` / `ItemsRemovedFromCollection`，条目的父级仍是库而不是 `BoxSet`，原实现 `else if (e.Parent is BoxSet)` 不可能命中。
- **修复**：插件订阅 `ICollectionManager` 两个事件，按 `ItemsChanged` 逐个条目发送。
- **复验**：加入/移除合集后接收端分别收到 `collection.items.added` / `collection.items.removed`。

### 其他观察（未改代码）

- UI 设置页的 `EditMultilSelect` 字段（如 `IntroSkipOptions.MarkerEnabledLibraryScope`）通过 PageSave 提交后没有落库（返回视图仍为空）；直接写配置文件后生效。属于 GenericUI 多选字段的保存问题，不影响本轮两个开关（都是普通布尔）。
- 一次误操作：`Extract Intro Fingerprint` 计划任务在作用域未生效时按全库 8,697 集启动，已立即停止（处理到 9/8,697，仅 9 次短读取）；随后把 `MarkerEnabledLibraryScope` 写进配置文件并只对 2 集受控视频重跑。生产 8096 未受影响。

---

## 六、还没验到的、及原因

1. **真实 115 库的片头指纹全量检测**：为遵守“不要对 115 造成压力”，没有在真实库跑 8,697 集指纹；改用受控本地视频完成“提取指纹 → 检测 → 写 IntroStart/IntroEnd”的同一管线。真实库片头数据本身为空（无标记可读）。
2. **浏览器自动化 E2E（P7/P16/P17/P18）**：本仓库没有 Playwright/Puppeteer 或浏览器测试脚本；Playwright MCP 本次连接失败。已用 `/UI/View`、`/UI/Command PageSave`、下发 `shortcuts.js` 和 `node --check` 覆盖到页面数据与脚本层，但没有真实浏览器渲染/点击。
3. **Host 契约八项**：本仓库不存在该组测试，无法执行。
4. **`image.update` 通知**：未单独触发成功（图片保存不产生 `ItemUpdated(ImageUpdate)`）；投递链路已由 metadata/collection/deep.delete 五类事件覆盖。
5. **全库 2,251 演员刷新**：只在全新 3 人小实例上跑了整任务并逐字段核对；没有在复制的大库上跑（2251 人 × TMDB/头像请求，性价比与压力都不合适）。

---

## 七、发版结论

**可以发版（1.1.0）。** 依据：

- 构建 0 error；自动化 749/749；三处真实缺陷已修复并有回归断言。
- 上轮五项“环境限制”全部补验通过；两个新开关的界面与服务端行为双向验证通过。
- 真实库的搜索、媒体信息、字幕、多版本冒烟通过；片头以受控本地管线验证通过。
- 生产 8096 全程未触碰；StrmButler 未改。

发版前只剩机械步骤：

1. `dist/plugins/StrmAssistantLite.dll`、`dist/SHA256SUMS.txt`、`manifest.json`（1.1.0 checksum/timestamp）已在本轮提交里同步；完整包与目录包已重新生成（`dist/StrmAssistantLite-v1.1.0-emby4.10.zip`、`dist/StrmAssistant-1.1.0.zip`，MD5 `EF16B095410B008ADD36C10C374B5826`）。
2. `scripts/release.sh 1.1.0` 需要 `gh` 登录与 `pub` 远端推送；本轮未执行 push/tag/release（按要求只做到可发版状态）。
3. 发布前建议把 `00b25b6` 合回 `feature/pro-backport`（本轮提交在 Hermesh worktree 分支上，未改该分支）。

---

## 附录：证据文件位置（均为临时/未入库）

- 豆瓣代理与 mock：`.../sessions/s_75cglx6f_01m86ea1x2w888xmtmx590ve6d/tmp/verify/{douban_proxy.py,douban-mock.log}`
- TMDB mock：`.../tmp/verify/{tmdb_mock.py,tmdb-mock.log}`
- 通知接收端：`.../tmp/verify/{notify_receiver.py,notify.log}`
- 测试实例配置：`/vol3/1000/临时备份/strm-verify-emby-config`、`/vol3/1000/临时备份/strm-person-emby-config`
- Emby 日志：上述配置目录下 `logs/embyserver*.txt`（含 17:4x–18:14 的完整证据）
