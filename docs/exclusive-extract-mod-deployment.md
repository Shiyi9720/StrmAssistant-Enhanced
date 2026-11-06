# 独占提取 mod 部署清单（HarmonyX 2.16.1）

本文件是第十八批 mod 的部署依据。第十八批用 `Lib.Harmony 2.3.6`（单文件
`0Harmony.dll`，内部嵌入 MonoMod 类型）生产失败，根因是 MonoMod 类型标识冲突；
第二十一批换成 HarmonyX 2.16.1，`0Harmony` 与 `MonoMod.*` 以独立程序集落盘。

## 硬性规则

1. **不要用 ILRepack 把 `0Harmony.dll` / `MonoMod.*.dll` 合并进插件主程序集。**
   合并会重新制造「同一份 MonoMod 类型存在两份标识」的问题。
   csproj 里的 ILRepack 步骤已删除。
2. **插件主程序集是 `Assembly.Load(bytes)` 加载的**，默认探测不会看插件目录。
   `Plugin` 的 `AppDomain.AssemblyResolve` 按简单名从插件目录加载依赖，因此依赖
   DLL 必须逐个放在插件目录（不能只放主 DLL）。
3. 依赖 DLL 必须与主 DLL **同一次构建**产出，不要混用旧版本。

## 必须部署的文件

以下文件全部来自 `StrmAssistant/bin/Release/netstandard2.1/`：

| 文件 | 作用 | 构建时校验 |
| --- | --- | --- |
| `StrmAssistantLite.dll` | 插件主程序集 | — |
| `0Harmony.dll` | HarmonyX 2.16.1（不再内嵌 MonoMod） | `VerifyModDependencies` |
| `MonoMod.Core.dll` | detour 引擎核心 | `VerifyModDependencies` |
| `MonoMod.Utils.dll` | MonoMod 公共工具（类型标识的唯一来源） | `VerifyModDependencies` |
| `MonoMod.RuntimeDetour.dll` | 运行时 detour | `VerifyModDependencies` |
| `MonoMod.Backports.dll` | MonoMod 运行时兼容层 | — |
| `MonoMod.ILHelpers.dll` | IL 辅助 | — |
| `MonoMod.Iced.dll` | JIT/IL 后端 | — |
| `Mono.Cecil.dll` / `Mono.Cecil.Mdb.dll` / `Mono.Cecil.Pdb.dll` / `Mono.Cecil.Rocks.dll` | IL 读写 | — |
| `ChineseConverter.dll` | 简繁转换 | — |
| `ToolGood.Words.Pinyin.dll` | 拼音 | — |
| `System.*.dll`（NuGet 闭包：`System.Memory`、`System.Text.Json`、`System.Runtime.CompilerServices.Unsafe` 等） | HarmonyX/MonoMod 依赖 | — |
| `zh/`、`zh-hant/` 子目录 | 卫星程序集 | — |

最省事且不会漏的做法：把 `bin/Release/netstandard2.1/` 下的**全部** `*.dll`
以及 `zh/`、`zh-hant/` 两个目录一起复制到 Emby 的 `plugins/` 目录。

设置 `-p:EmbyPluginPath=<plugins 目录>` 时，csproj 的 `DeployPlugin` target 会按上述
规则自动复制；不设置时只构建，不影响 macOS 本地开发。

## 构建校验

`dotnet build -c Release` 会执行 `VerifyModDependencies`：缺少
`0Harmony.dll`、`MonoMod.Core.dll`、`MonoMod.Utils.dll` 或
`MonoMod.RuntimeDetour.dll` 时直接构建失败。

## 运行时自检与降级

- 启动时 `PatchManager.HarmonySelfTest()` 对插件自身的 `SelfTestTarget()` 做一次
  真实 detour：postfix 把返回值改成 2，解开后必须回到 1。
- 自检失败 → `IsModOperational = false`，日志明确区分：
  - `detour engine unavailable`：引擎拒绝安装（架构/运行时/类型冲突）；
  - `patch installed but had no effect`：装上了但 detour 无效。
- 16 个补丁点原子化应用：任一目标失败 → 逆序回滚本次已应用的补丁 →
  `IsModOperational = false` → 设置页开关置灰、强制关闭 → 走 Emby 原生提取。
- 日志会列出已加载的 `0Harmony` / `MonoMod.*` / `Mono.Cecil` 程序集及路径；
  同一名字出现多份时打 WARN（类型冲突的第一现场证据）。

## 已知平台情况

- HarmonyX 2.16.1 的 MonoMod 25.x 支持 macOS arm64，本地可真实打 detour
  （2.10.x 的 MonoMod 22.x 在 arm64 上不可用，故未采用）。
- 生产 Linux x64 的真实 ffprobe detour 仍需部署后看日志定案；本地已用
  Emby 4.10.2 生产程序集逐个验证 16 个补丁点可安装、且
  `GetInputArgument(Http)` 的补丁效果真实生效。
