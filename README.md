# FrameSync

Unity 6 双人 C/S 帧同步示例，包含独立 ASP.NET Core WebSocket 服务端和共用的确定性 C# 模拟库。

## 项目结构

- `Assets/`、`Packages/`、`ProjectSettings/`：Unity 客户端项目。
- `FrameSyncDemo/Shared/FrameSync.Shared/`：确定性模拟、Lockstep、输入缓存、预测与回滚，目标框架 netstandard2.1。
- `FrameSyncDemo/Server/FrameSync.Server/`：ASP.NET Core 服务端，目标框架 net10.0。
- `FrameSyncDemo/Tests/FrameSync.SimTests/`：模拟及回滚控制台验收测试。
- `FrameSyncDemo/Tests/FrameSync.NetworkTests/`：真实 WebSocket 网络诊断控制台验收测试。
- `Docs/NetworkDiagnostics/README.md`：网络诊断实现说明。
- `Docs/RemoteInterpolation/README.md`：远端玩家停走时的过度预测显示修复说明。

## 环境

- Unity **6000.5.3f1**，通过 Unity Hub 打开仓库根目录。
- .NET SDK **10.0**。
- 默认 WebSocket 地址：`ws://127.0.0.1:5000/ws`。

Unity 缓存、IDE 缓存及游戏构建产物不纳入版本管理，打开项目后由 Unity 重新生成。`Assets/Plugins/FrameSync.Shared.dll` 和 XML 文档作为 Unity 所需依赖保留在仓库中。

## 启动

在仓库根目录运行：

```powershell
dotnet run --project FrameSyncDemo/Server/FrameSync.Server
```

在 Unity 中打开 `Assets/Scenes/SampleScene.unity`，用当前源码构建一个客户端，然后同时运行新构建和编辑器 Play，或运行两个新构建。玩家 0 使用 WASD，玩家 1 使用方向键。Game 窗口现有调试器显示逻辑帧、回滚、RTT 和 Jitter 等数据。

房间为单局制。一名玩家断开后房间停止；开启新局可在服务端控制台输入 `restart`，然后重新进入两个客户端。输入 `stop` 停止服务端。

## 修改 Shared 后更新 Unity 插件

```powershell
dotnet build FrameSyncDemo/Shared/FrameSync.Shared
Copy-Item FrameSyncDemo/Shared/FrameSync.Shared/bin/Debug/netstandard2.1/FrameSync.Shared.dll Assets/Plugins/FrameSync.Shared.dll
Copy-Item FrameSyncDemo/Shared/FrameSync.Shared/bin/Debug/netstandard2.1/FrameSync.Shared.xml Assets/Plugins/FrameSync.Shared.xml
```

等待 Unity 重新导入 DLL，并重新构建客户端。

## 测试

```powershell
dotnet run --project FrameSyncDemo/Tests/FrameSync.SimTests
dotnet run --project FrameSyncDemo/Tests/FrameSync.NetworkTests
```

两个测试项目都是控制台程序，实际断言通过 `dotnet run` 执行。网络测试会自动启动服务端并使用本机 5000 端口，运行前请停止手工启动的服务端。网络测试在最小 Unity API 测试宿主中运行实际 NetworkClient 和 NativeWebSocket 源码，不代替 Unity 双客户端画面验收。

本地验证日志、修改前备份和依赖本机 Unity 安装路径的临时编译检查工程不上传。开发说明中的这些本地验证附件不会随克隆提供。
