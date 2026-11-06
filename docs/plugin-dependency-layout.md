# 插件依赖程序集布局（避免 Emby 扫描期 GetTypes 异常）

## 现象

生产日志（第二十批 + 第二十一批部署后）在启动扫描阶段出现少量：

```
Error loading types from assembly
LoaderException: Could not load file or assembly 'MonoMod.Utils, Version=...'
LoaderException: Could not load file or assembly 'Mono.Cecil, Version=0.11.6.0, ...'
LoaderException: Could not load file or assembly 'MonoMod.Iced, Version=1.21.0.0, ...'
```

功能不受影响，但每次启动都会记 Error。

## 根因（已复现）

Emby 4.10 `ApplicationHost`：

1. `DiscoverTypes()`（`Init` 中，早于 `FindParts()` / 插件实例化）调用
   `GetPluginAssemblies(ApplicationPaths.PluginsPath)`；
2. 它对 plugins **顶层** 的每个 `*.dll` 执行
   `Assembly.Load(File.ReadAllBytes(file))`（`LoadAssembly`），然后对每个程序集调用
   `assembly.GetTypes()`（`GetTypes`，捕获 `ReflectionTypeLoadException` 并逐条记 Error）；
3. 按字节加载的程序集不会在默认 `AssemblyLoadContext` 中按名字注册，因此它引用的
   `MonoMod.Utils` / `Mono.Cecil` / `MonoMod.Iced` 仍然走默认探测（Emby system 目录），
   找不到就抛 `FileNotFoundException`；
4. 插件自己的 `AppDomain.AssemblyResolve` 兜底是在 `Plugin` 静态构造函数里注册的，
   而静态构造函数要等 `FindParts()` 实例化插件时才执行——比 `DiscoverTypes()` 晚，
   救不了扫描阶段。

因此错误与 Harmony 运行时是否正常无关：扫描 `0Harmony.dll`、`MonoMod.Core.dll`、
`MonoMod.RuntimeDetour.dll`、`MonoMod.Utils.dll`、`Mono.Cecil.Mdb.dll`、
`Mono.Cecil.Pdb.dll` 时各报一次。

本地用生产 Emby 4.10 程序集按上述方式（`Assembly.Load(bytes)` + `GetTypes()`）复现，
输出与生产日志一致。

## 修复

依赖程序集不再放 plugins 顶层，而是放子目录：

```
<plugins>/
  StrmAssistantLite.dll
  zh/StrmAssistantLite.resources.dll
  zh-hant/StrmAssistantLite.resources.dll
  StrmAssistantLite.deps/
    0Harmony.dll
    MonoMod.Core.dll
    MonoMod.Utils.dll
    MonoMod.RuntimeDetour.dll
    MonoMod.Backports.dll
    MonoMod.ILHelpers.dll
    MonoMod.Iced.dll
    Mono.Cecil*.dll
    ChineseConverter.dll
    ToolGood.Words.Pinyin.dll
    System.*.dll
```

- Emby 只扫 plugins 顶层（`SearchOption.TopDirectoryOnly`），子目录不会被扫描；
- `Plugin` 的 `AssemblyResolve` 回调优先从 `<plugins>/<AssemblyName>.deps/` 按简单名
  加载，找不到再回退旧的顶层路径（兼容旧部署）；
- 加载前先检查 `AppDomain.CurrentDomain.GetAssemblies()`，同名程序集已存在则直接复用，
  绝不重复加载（避免 Harmony/MonoMod 两份类型标识）；
- 卫星程序集（`zh`、`zh-hant`）位置不变。

### 不要做的事

- 不要为了消除日志删除 `0Harmony.dll` / `MonoMod.*.dll`：Harmony mod 需要它们；
- 不要把 HarmonyX 的 0Harmony/MonoMod 重新 ILRepack 成一个程序集：会再次制造
  MonoMod 类型标识冲突（第二十一批已因此改用独立程序集）；
- 不要把这些依赖复制进 Emby system 目录。

## 升级迁移（一次性）

旧部署把依赖 DLL 放在 plugins 顶层。升级到新布局后需要把这些文件移到
`StrmAssistantLite.deps/`（或删除后由新部署重新生成），否则它们仍在顶层被扫描、
错误依旧。只移动本插件部署的文件：

```
0Harmony.dll MonoMod.Core.dll MonoMod.Utils.dll MonoMod.RuntimeDetour.dll
MonoMod.Backports.dll MonoMod.ILHelpers.dll MonoMod.Iced.dll
Mono.Cecil.dll Mono.Cecil.Mdb.dll Mono.Cecil.Pdb.dll Mono.Cecil.Rocks.dll
ChineseConverter.dll ToolGood.Words.Pinyin.dll
System.Buffers.dll System.Memory.dll System.Numerics.Vectors.dll
System.Runtime.CompilerServices.Unsafe.dll System.Runtime.Loader.dll
System.Text.Encodings.Web.dll System.Text.Json.dll System.Threading.Tasks.Extensions.dll
Microsoft.Bcl.AsyncInterfaces.dll
```

若同名 DLL 属于其它插件，不要动它。

## 验证

- 构建：`dotnet build -c Release` 0 error；
- 扫描复现：对旧布局（全部 DLL 在顶层）复现出上述三条 LoaderException；
  对新布局（仅主 DLL 在顶层 + `.deps` 子目录）扫描结果为
  `OK StrmAssistantLite.dll: 637 types`，无 `ReflectionTypeLoadException`；
- 运行时：按字节加载主 DLL，触发 `Plugin` 静态构造函数并设置 `_satelliteRoot` 后，
  `0Harmony` 从 `.deps/` 按字节成功加载，`new HarmonyLib.Harmony(...)` 可实例化，
  `MonoMod.Utils` / `Mono.Cecil` 随之解析成功。

以上验证在 macOS + 生产 Emby 4.10 程序集上完成；未在真实生产 Emby 进程内重启验证。
