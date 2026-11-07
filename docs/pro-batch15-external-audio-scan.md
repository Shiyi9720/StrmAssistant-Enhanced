# 批次 15：外挂音轨扫描（对照 PRO 对齐「字幕 #23」）

本批只补「数据」，不接管 Emby 的播放/转码决策；开关默认关闭，关掉时行为与之前完全一致。
同批曾实现的「电影 IntroDb」已按「对齐 Pro、不做超出 Pro 的功能」撤回，见文末。

## 提交

| 提交 | 内容 |
|---|---|
| `3e04096` | feat: scan external audio tracks |

工作树干净；未推远端、未部署。

## 1. 外挂音轨扫描（字幕 #23）

- 选项：`Options/MediaInfoExtractOptions.cs`
  - `EnableExternalAudioScan`，默认 `false`
  - `ExternalAudioExtensions`，默认 `flac,ac3,dts,dtshd,mka,aac,mp3,truehd,thd`
- 实现：`Common/SubtitleApi.cs`
  - 复用现有字幕目录枚举与 `AddExternalTracks` 的同名匹配逻辑，**没有新增目录遍历**，也不新增扫描任务。
  - Emby 4.10 已把外挂音轨迁移到 `AudioTrackResolver`：其静态扩展名列表内的交给它；`dtshd/truehd/thd` 等不在列表里的，用同一批 `FileSystemMetadata` 按相同文件名规则补全。
  - 生成的流：`MediaStreamType.Audio`、`IsExternal = true`、`Protocol = File`，带 `Codec/Title/Language/Index`，再沿用现有探测流程补真实编码信息。
- 注意：4.10 里 `SubtitleResolver.isAudio` 的语义已变成「音频条目/歌词」，不能当参数直接传 `true`，代码内有注释说明。
- 兼容性：默认关闭时字幕路径完全不变，已有音轨流不会被移除；开启后该功能只补全插件自己的媒体流数据。

## 2. 本地化

- 本批新增 4 个键（两个选项各一对 `DisplayNameL/DescriptionL`），`Resources.resx` / `Resources.zh.resx` / `Resources.zh-hant.resx` 三份同步，`Resources.Designer.cs` 无缺失。
- 未处理 5 个既有缺失键（`DoubanCacheOptions_*`，属于禁改的豆瓣缓存范围）。

## 3. 验证

- `dotnet build -c Release`：**0 error / 0 warning**，产出 `StrmAssistantLite.dll`（撤回电影部分后重跑复核）。
- 未做运行时验收：真实媒体库里的外挂音轨合流需要在部署后的 Emby 上实测。

## 4. 未做项

- 音轨没有独立扫描任务或独立目录遍历，全部复用既有扫描路径。

## 5. 撤回：电影 IntroDb

- 背景：`59b5e63 feat: record movie IntroDb credits` 曾实现「电影片尾 credits 落插件 JSON」（选项 `EnableMovieIntroDb`，入口 `Plugin.OnRefreshCompleted` 与 `LibraryApi.RefreshMovieMetadata` 的完整条目分支，存储 `StrmAssistant.IntroDb.json` 的 `MovieOutros`）。
- 撤回原因：对齐原则是「对齐 Pro、不做超出 Pro 的功能」；Pro 的 IntroDb 是剧集功能，电影这条没有对应实现，留着属于超前于 Pro 的自造功能。
- 处理：`f399518` revert 该提交，代码完整保存在归档分支 `archive/batch15-movie-introdb`（指向 `59b5e63`），需要时可取回，不进 port 主线。
- 同时修正对齐文档的口径：`docs/final-pro-alignment-20261107.md` 曾把「电影 IntroDb」列为建议批次，与「Pro 无电影片尾」冲突，后续不要再按那条捡起来。
