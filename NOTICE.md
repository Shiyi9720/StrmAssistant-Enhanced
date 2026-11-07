# NOTICE

本仓库包含或引用了以下第三方成果，各自的版权与许可归其作者所有。

## 上游项目

- **Strm Assistant** — <https://github.com/sjtuross/StrmAssistant>
  - 许可：GPL-3.0（本仓库沿用，全文见 `LICENSE`）
  - 本仓库在其社区版基础上继续开发；原项目的版权声明与许可证予以保留

## 运行时依赖（随插件包分发于 `StrmAssistantLite.deps/`）

| 组件 | 用途 | 许可 |
|---|---|---|
| 0Harmony / MonoMod 系列 | 运行时方法补丁 | MIT |
| System.Text.Json / System.Memory / System.Buffers 等 | JSON 与基础库 | MIT |
| ToolGood.Words.Pinyin | 汉字转拼音（搜索用） | Apache-2.0 |
| ChineseConverter | 简繁转换 | MIT |

各组件版本以 `StrmAssistantLite.deps/` 内实际文件为准。

## 数据与接口

- TMDB、豆瓣、OpenSubtitles、theintrodb.org 均为第三方服务，本项目仅按其公开接口访问，不缓存超出功能所需的数据，也不对其可用性作任何保证。

## 不含的内容

- 不包含 Emby Server 的任何专有程序集、代码、图标或其它版权资源；构建时引用的 Emby 程序集仅在本地从用户自己安装的服务器中提取，不随本仓库分发。
