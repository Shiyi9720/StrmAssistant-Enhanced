# Batch 8 — UI 收尾 7 项（2026-11-09）

hermesh 派发（claude，session `s_75cglx6f_01m6z6xj0pc7rnqby3j2wphs9t`），14 文件（+4466 −169 含三语资源），
构建 0 error，已部署验证。

## ① 隐藏无图演职人员
`ExperienceEnhanceOptions.HidePersonNoImage`（默认关）。前端只在 `.peopleItemsContainer` 内判定：
无 `img`、无 `data-src`、无背景图 → 加类隐藏；MutationObserver 重渲染后重算，有图卡片自动恢复。

## ② 搜索推荐列表屏蔽可配
`SearchOptions.HideSearchSuggestions`（默认 true，保持原行为）。关闭后不再注入
`.searchSuggestions{display:none!important}`。

## ③ MergeSeriesPreference 暴露
由 `=> LibraryScope` 只读表达式改为可写选项（默认 LibraryScope）+ `VisibleCondition(MergeMultiVersion)`；
OnOptionsSaving 校验枚举合法性，非法回退 LibraryScope。

## ④ 抑制插件自动更新
`SuppressPluginUpdates`（默认关）+ `SuppressPluginUpdatePlugins`（名单，空=全部）。
前端包装 `ApiClient.getAvailablePluginUpdates` 过滤结果 + DOM 兜底隐藏更新入口。
说明：Emby 4.10.2 web 插件页只调 `getInstalledPlugins`，更新入口唯一数据源是该 API。

## ⑤ TMDB 剧集组手选
`EpisodeGroupPreferredType`（Auto/首播日期/绝对序号/DVD/数字/故事线/制作/TV，对应 TMDB type 1–7）+
`EpisodeGroupPreferredGroup`（组 ID 或名称）。选组级联：显式组 → 类型偏好 → 原自动排序；
缓存 SourceKey 追加选择键 `type:group`，改偏好自动失效。

## ⑥ 单集季跳过片头检测
`FingerprintApi.UpdateIntroMarkerForSeason` 开头统计实际视频分集，< 2 直接返回（连 IntroDb 一并跳过）。

## ⑦ 无 OP 剧误报过滤
新增 `IntroMarkerMinDurationSeconds`(5)/`IntroMarkerMaxDurationSeconds`(600)/`IntroMarkerPaddingSeconds`(0)，
默认值=现有硬编码行为。新 `Common/IntroMarkerHelper.cs` 统一「留白 → 限片长 → 最短丢弃 / 最长裁剪」；
两条写标记路径（Emby 指纹管线 + IntroDb）都接入。

## 部署验证
- 构建 0 error；备份 `.bak-before-batch8`；重启后 `Error loading types`=0、`LoaderException`=0、
  MovieDb patch 13 条齐全；指纹任务重触发正常。
- 备注：`GET /emby/Plugins/{id}/Configuration` 返回 500（Emby `PluginService.Get` NRE）——实测本机
  Emby 4.10.2 的 `/emby/Plugins` 里 **23 个插件 Configuration 全为 null**，该端点是全局性不可用，
  与本次改动无关；插件自身配置页（BasePluginSimpleUI）正常。
