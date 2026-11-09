# batch-12：第三方源阶梯退避 + 已知限制拆分（1.0.6）

## 背景

README「已知限制」里四条逐条核对：

| 限制 | 结论 |
|---|---|
| 进度按 TMDB id 共享 / 多版本续播偶有偏移 | 可改，需按 `(ItemId, MediaSourceId)` 维度存断点并真机验收（任务书 `hermesh-batch13-per-version-resume.txt`），本轮不动 |
| 不接管 Emby 转码 | 设计边界，不是缺陷；从「已知限制」移到「设计边界」 |
| DV P7 依赖首次下载 ffprobe 9 | 现场实测 `raw.githubusercontent.com` 在本机**直连不可达**（curl 000，走代理才通），即该检测一直处于降级态。改为优先本地路径，并把 9.0.2 静态二进制预置进插件数据目录 `bin/ffprobe` |
| 第三方源限流 | 原来固定暂停（豆瓣 10 分钟 / IntroDB 2 分钟），到期即重新撞墙；改为阶梯退避 |

## 代码改动

- 新增 `StrmAssistant/Common/SourceBackoff.cs`：`ConcurrentDictionary<string, State>`，阶梯 10 分钟 → 30 分钟 → 2 小时 → 6 小时（cap），`ReportSuccess` 清零；仅阶梯档位变化时打一条 Info，恢复时一条 Debug。
- `DoubanApi`：删 `_blockedUntilUtc`；403/418/429 → `ReportFailure("douban")`，200 且反序列化成功 → `ReportSuccess`。
- `IntroDbApi`：删 `_unavailableUntilUtc` / `_secondaryUnavailableUntilUtc`；`SetUnavailable` → `ReportFailure("introdb:Primary|Secondary")`，成功解析 → `ReportSuccess`。
- 无新增设置项、无 UI 变化、无新任务/定时器；成功路径行为不变。

## 运维要点（本次踩到）

- **并发配置改了必须重启 Emby 才生效**：`GeneralOptions.IntroDetectionMaxConcurrentCount` 在插件初始化时读一次，仅停/启定时任务没用（日志仍打印旧值 `Max Concurrent Count: 3`）。改文件 → `docker restart emby` → 日志应显示新值。
- 重启打断指纹任务，但已完成条目的 `.fp` 会在下次运行时命中：队列从 6069 降到 5720（约 300+ 集被跳过）。

## 部署与验收

- 构建 `dotnet build -c Release` → 0 error（仅既有 CS0618）；md5 `7a65d335e6c5ba72049afb4119b17d50`
- 部署：`/config/plugins/StrmAssistantLite.dll` 备份 `.bak-before-batch12`；重启后 `/Plugins` → `Strm 助手 1.0.6.0`，`ChineseSearch - Index loaded: 13175 items`，无 LoaderException
- 静态 ffprobe：容器内 `/config/plugins/configurations/StrmAssistant/bin/ffprobe -version` → `ffprobe version 9.0.2-a3f9c2b`
- 并发 3 → 1 后实测：单个 ffmpeg、EmbyServer CPU 159% → 16%、整机 load 2.9 → 1.15

### 未测项

- 退避逻辑没有在真实 403/429 下触发过（豆瓣 / IntroDB 当前都正常返回），只有代码审阅 + 构建验证
- DV P7 的实际分层检测未跑（库里没有需要走 P7 判定的条目）
- OpenSubtitles 仍走既有 `ExecuteWithRateLimitRetry`，未纳入 SourceBackoff

## 发布

- 提交 `02cc563`（代码 + 文档）、`72ad41b`（dist）；tag `v1.0.6`
- Release: https://github.com/Shiyi9720/StrmAssistant-Enhanced/releases/tag/v1.0.6
- 附件：`StrmAssistantLite-v1.0.6-emby4.10.zip`（3,614,231）、`StrmAssistant-1.0.6.zip`（3,608,765）、`StrmAssistantLite.dll`（1,871,872）
