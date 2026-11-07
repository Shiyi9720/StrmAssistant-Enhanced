# 豆瓣推荐频道预告片

## 功能范围

- 首页豆瓣推荐卡片在开关开启时显示「预告片」按钮。
- 点击后按需请求豆瓣预告片地址，并在 Emby 页面内用 `<video>` 播放。
- 预告片来自豆瓣公开接口，只做展示/播放，**不下载、不入库**。
- 开关：`MetadataEnhanceOptions.EnableDoubanTrailer`，默认 `false`，仅在
  `EnableDoubanRecommendations` 开启时可见。

## 接口选型

使用豆瓣移动端 JSON 接口：

```
GET https://m.douban.com/rexxar/api/v2/movie/{douban_id}/trailers
Referer: https://m.douban.com/
```

实际返回样例（2026-11-07，`movie/1292052`，已截断）：

```json
{
  "trailers": [
    {
      "id": "325959",
      "title": "中国大陆预告片 (中文字幕)",
      "type_name": "预告片",
      "runtime": "01:30",
      "cover_url": "https://img3.doubanio.com/img/trailer/medium/2935310952.jpg",
      "video_url": "https://vt1.doubanio.com/202611071119/d1a4c8019e03b60c7d7670c4878baa22/view/movie/M/703250959.mp4"
    }
  ]
}
```

选择规则：优先第一条 URL 含 `.mp4` 的预告片，否则取第一条有效地址；同时兼容
`video.url` / `video.video_url` 字段。

### 放弃的接口

- `https://frodo.douban.com/api/v2/movie/{id}/trailers`：返回 `400` 和
  `apikey is required`，公开环境不可用。
- 豆瓣网页 `/subject/{id}`：预告片信息混在 HTML/脚本中，解析脆弱，未采用。

## 缓存与限速

- `LruCache(300)`：命中的预告片缓存 2 小时；无预告片/失败的负缓存 10 分钟。
- 复用 `DoubanApi` 的全局 `RequestGate` 与 `DoubanRequestIntervalSeconds`
  （2–30 秒，默认 3 秒）串行限速。
- 任何异常或限流都返回空结果，不影响推荐列表。

## 播放与降级

- 优先在页面内用 `<video controls playsinline>` 播放豆瓣返回的 MP4 直链。
- 实测 CDN 支持 `Range` 并返回 `206 video/mp4`，不强制 `Referer`。
- `<video>` 跨域播放不要求 CORS 头，因此不设置 `crossOrigin`，只设置
  `referrerpolicy="no-referrer"`，避免触发防盗链策略。
- 不使用服务端代理：直链可播放时无需消耗 Emby 带宽，也避免代理 Range/HLS 的复杂度。
- 若浏览器无法播放（例如仅返回 m3u8 而浏览器不支持 HLS，或 CDN 拒绝直链），
  播放层会显示失败提示，并提供明确标注为「降级」的新标签页直链入口。

## API

- `GET /StrmAssistant/Douban/Recommendations`：新增可选字段
  `TrailerUrl`、`TrailerTitle`、`TrailerCoverUrl`；已有字段不变。
- `GET /StrmAssistant/Douban/Trailer?DoubanId=...&SubjectType=movie`：
  按需返回 `TrailerUrl`、`TrailerTitle`、`CoverUrl`、`Runtime`，无结果时为空。

`DoubanId` 只接受数字，视频地址只接受 `http`/`https`。
