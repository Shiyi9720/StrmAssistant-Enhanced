# Batch 4 — stub 修复 + 自家半成品修复（2026-11-09）

两单均由 hermesh 派发（agent claude）：
- session `s_75cglx6f_01m6y6nxp07z2vs6ymn5z30nft`：stub 修复 + 官方 Pro 功能全量审计
- session `s_75cglx6f_01m6y7eydvxnhm6exc2vtwfzat`：豆瓣缓存挂载 + 截图开关

## 项目定性更正
本项目是用户基于 sjtuross 社区版自建的 Emby 4.10.2 增强分支（非官方 Lite/Pro）。
此前「Lite 阉割 Pro」的旧结论作废；审计按官方 wiki 对照 Options/* 与读取点。

## 改动
1. `Options/IntroSkipOptions.cs`：`MarkerEnabledLibraryScope` 从空壳
   （get=>string.Empty;set{} + Browsable(false)）改为真实持久化属性。下拉数据源
   （Plugin.cs:1379）、清洗（847-856）、读取点（FingerprintApi 三处）、三语资源键
   本来就齐，缺的只有属性本身。
2. `Options/MetadataEnhanceOptions.cs`：挂载 `DoubanCacheOptions` 子选项（此前反射
   查不到 → GetEffective() 恒默认 → 预构建任务永远隐藏）。资源键三语已存在。
3. `Options/MediaInfoExtractOptions.cs`：`EnableImageCapture` 从 `=> false` 死开关改为
   真实 bool（默认 false，行为不变）；`ImageCapturePosition` 的 VisibleCondition 随之生效。
4. `Plugin.cs`：启动日志补 EnableImageCapture；DoubanCacheOptions.cs 注释更新。

## 部署实测
- 构建 0 error；部署备份 `StrmAssistantLite.dll.bak-before-batch4`。
- 重启后日志：`MarkerEnabledLibraryScope(raw): 163842,197506`（此前恒空）→ 真实库范围生效。
- 指纹任务重触发：队列 7,223 集（重复行与已有标记集进一步剔除），正常推进。
- 重启瞬间的 TaskManager ObjectDisposedException 为旧任务被打断的关机噪音，非新问题。

## 审计结论（官方 Pro 对照）
- 已实现：拼音搜索/本地TMDB/豆瓣补全/多版本/ISO·BDMV/DV P7/分布式提取/片头片尾/字幕/通知/快捷菜单 等（详见 hermesh 会话报告）
- 缺失清单（未做，按价值排序）：
  1. TMDB 剧集组刮削（对动漫有用）
  2. 缺失集 TMDB 补全 / 集标题美化 / 禁止自动建合集库
  3. 深度删除 Deep Delete（级联删源文件，危险，待用户确认）
  4. 拆分多版本按钮、解除原生 8 版本上限等小件
- 无「UI 可见但后端全无」的假开关（除上述已修的两个）。
