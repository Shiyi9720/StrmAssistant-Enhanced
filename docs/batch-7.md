# Batch 7 — Deep Delete / 解除多版本上限 / 拆分多版本 / 去重口径统一（2026-11-09）

hermesh 派发（claude，session `s_75cglx6f_01m6z4htqw8csxhkw9c7d169xa`，14 文件 +806 −6）
+ 修复 session `s_75cglx6f_01m6z6edc6gz0zacvfhy245ctp`（+11 行）。构建 0 error。

## 1. 深度删除 Deep Delete（`Mod/DeepDelete.cs`，默认关）
挂点：反编译确认 Emby 删除链全部收敛到
`LibraryManager.DeleteItem(BaseItem, DeleteOptions, BaseItem parent, bool notifyParentItem)`
（`Emby.Server.Implementations` impl:747），REST 入口 `Emby.Api.Library.LibraryService.Any(DeleteItems)`
只构造 `DeleteOptions{DeleteFileLocation=true}` 后调用它。Emby 自身只删 `.strm` 文件本体，
Series/Season 的子集 Episode 不逐个过 DeleteItem → 插件 prefix 显式级联
（`GetItemList(Recursive, HasPath)` 枚举集）。
行为：`DeleteFileLocation=true` 且选项开启时，读 strm 首个非空行（去 BOM）→ 解析目标 →
批次内去重 → 逐条 Info 日志 → 本地/UNC 路径走 `IFileSystem.DeleteFile`；http(s) 走 HTTP DELETE
（剥查询串）。异常全吞、只记 Error，绝不影响 Emby 删除主流程。选项关闭零 hook。
风险：开启后删除不可撤销（strm 目标文件真删），默认关；误删面=同路径多个条目指向同一源文件时删一次（已去重）。

## 2. 解除 Emby 原生多版本 8 个上限（`Mod/UnlockMultiVersionLimit.cs`，跟随多版本开关）
transpiler 把 `GetVideosGroupedByVersion` 里的 `Ldc_I4_8` 常量替换为
`GetMaxMultiVersionCount()` 调用（插件自身上限 64）。

## 3. 拆分多版本按钮（`Web/Api/SplitMultiVersion.cs` + `LibraryService`/`ShortcutMenuHelper`/`strmassistant.js`）
对多版本电影把次要版本拆回独立条目：不移动、不删除任何文件，只重建条目与版本关系、复制元数据。
快捷菜单仅对多版本电影显示，完成后刷新；默认跟随多版本开关。

## 4. 去重口径统一
`FingerprintApi.FetchFingerprintQueueItems` 的 `GroupBy(InternalId)` 改为
`GroupBy(i => i.Path, OrdinalIgnoreCase)`，与两条新查询一致。

## 5. 部署后回归问题及修复（重要教训）
batch 7 首版部署后 Emby 启动出现**全新**错误：
`Error loading types from assembly — ReflectionTypeLoadException —
Could not load '0Harmony, Version=2.16.1.0'`，位置 `ApplicationHost.GetTypes`。
根因：`GetVideosGroupedByVersionTranspiler` 写成 `yield return` 迭代器，编译器生成的状态机类型
把 `IEnumerable<CodeInstruction>`（HarmonyLib 类型）放进了**接口列表**；`GetTypes()` 会立即解析
基类/接口，而 Emby 以字节加载插件、0Harmony 的 AssemblyResolve 要等插件构造函数后才装 →
早期扫描必然失败。此前所有类型都只在**字段/特性/方法体/方法签名**里碰 Harmony（惰性解析），所以不炸。
修复：transpiler 改普通方法 + `List<CodeInstruction>`（无 yield），行为等价，接口列表里不再有 HarmonyLib。
复现证据（本地模拟 ApplicationHost 扫描）：batch6 dll `802 types OK`；batch7 首版 `1 loader exception`；
修复后 `770 types OK`。部署后生产日志 `Error loading types`=0、`LoaderException`=0、MovieDb patch 13 条齐全。
**教训：插件里任何引用 HarmonyLib 的迭代器/泛型接口实现都会在 Emby 启动扫描阶段炸，transpiler 一律用普通方法 + List。**
