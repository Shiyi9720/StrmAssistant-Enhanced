**版本 1.1.1**｜适配 Emby 4.10.2（`4.10.2.0`）｜仅修复，无功能变更

**修复**

- 修复「刷新探索发现频道」（Refresh Internet Channels）时刷出 11 条 `Plugin event handler failed` 的问题。根因是频道条目（探索发现 / 豆瓣电影Top250 拉下来的条目）没有文件 `Path`，而 `MediaInfoApi.GetMediaInfoJsonPath` 直接用 `Path.Combine(null, …)` 拼路径，抛出 `ArgumentNullException(path1)`。现在 `Path` 为空或空白时直接返回，不再拼路径、不再记 Error 级日志；`SerializeMediaInfo` / `DeserializeMediaInfo` / `DeleteMediaInfoJson` / `DeserializeIntroMarker` 四个调用点同样跳过，`DeserializeChapterInfo` 在没有 `Path` 时只跳过本地缩略图恢复，章节照常保存
- 有 `Path` 的条目逻辑完全不变

**升级方式**：直接覆盖插件 DLL、`zh/` 与 `zh-hant/` 资源 DLL 后重启 Emby 即可；依赖 DLL（`StrmAssistantLite.deps`）与 1.1.0 字节完全一致，无需替换。插件内置 `Update Plugin` 任务也会自动更新主程序。
