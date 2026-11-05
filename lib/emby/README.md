# Emby 服务端程序集（不纳入版本库）

Emby 插件的程序集版本必须与服务器**完全一致**，否则 Emby 拒绝加载
（`LoaderException: Could not load file or assembly 'MediaBrowser.Model, Version=public-ip-xxx'`）。

做法：从运行中的 Emby 容器里取出以下 DLL，放到本目录：

    docker cp emby:/system/MediaBrowser.Model.dll   lib/emby/
    docker cp emby:/system/MediaBrowser.Controller.dll lib/emby/
    docker cp emby:/system/MediaBrowser.Common.dll  lib/emby/
    docker cp emby:/system/Emby.Naming.dll          lib/emby/
    docker cp emby:/system/Emby.Media.Model.dll     lib/emby/
    docker cp emby:/system/Emby.Web.GenericEdit.dll lib/emby/

csproj 会自动检测本目录（不存在时回退 NuGet SDK 包）。DLL 属 Emby 专有组件，**不要提交**。
