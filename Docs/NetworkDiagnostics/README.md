# 网络诊断交付说明

## 实际架构与接入点

本次在修改前执行了 `git status --short` 和 `git -C FrameSyncDemo status --short`。两个目录都不是 Git 仓库，无法提供 Git diff；原始修改文件和 Shared 插件已保存到 `Before/`。没有创建新游戏项目或修改 Lockstep 核心。

实际目录如下（省略 Unity 缓存和构建产物）：

```text
OnlineGame/
  Assets/
    Scenes/SampleScene.unity
    Plugins/FrameSync.Shared.dll、FrameSync.Shared.xml
    WebSocket/                 NativeWebSocket 源码及 WebGL 支持
    Scripts/
      Network/                 NetworkClient、NetworkSimulationSettings、DelayedNetworkMessage
      GameWorld/               NetworkGameRunner、GameWorldRunner
      Input/InputCollector.cs
      View/VisualCorrection.cs
      Debug/RollbackDebugger.cs
      UI/FPSDisplay.cs
  FrameSyncDemo/
    Shared/FrameSync.Shared/   netstandard2.1 类库
    Server/FrameSync.Server/   net10.0 ASP.NET Core 服务
    Tests/FrameSync.SimTests/  原有控制台测试
    Tests/FrameSync.NetworkTests/ 本次新增网络验收控制台程序
  Docs/NetworkDiagnostics/     本报告、完整代码、备份及验证日志
```

### 服务端接收

`Program.RunServerAsync()` 的 `/ws` 路由通过 `AcceptWebSocketAsync()` 接受连接，`room.TryJoin(socket)` 创建 `ClientSession`。每个连接分别启动一个发送任务和一个接收任务。

**ClientSession 没有 switch 的原因：它从来不负责接收或协议分发。真正入口是 Program.ReceiveLoopAsync()。** 该方法使用 `WebSocket.ReceiveAsync(ArraySegment<byte>, cancellationToken)`，在 `MemoryStream` 中拼接分片直到 `EndOfMessage`，仅接受文本、限制完整消息 4096 字节，再用 UTF-8 转为 JSON 字符串。

原来它只调用 `ProtocolJson.DeserializeInput()`，检查 `type == "input"`，然后 `room.SubmitInput(session, message)`。现在在原来的反序列化 try 中增加一个 `type == "ping"` 分支，使用已有 `ClientPingMessage` DTO 反序列化，再调用 `session.EnqueuePong()` 并 continue；原有 input 分支保留。不在 ClientSession 新建接收分发系统。

### 协议与发送

`ProtocolJson` 使用 `System.Text.Json`，统一 `IncludeFields = true`、`PropertyNameCaseInsensitive = true`。input 仍由 `DeserializeInput` 解析，Ping 用新增 `DeserializePing`，服务端消息统一由 `Serialize(ServerMessage)` 输出。

Unity 原来就使用 `JsonUtility.ToJson/FromJson`，本次仍沿用它。两端共用 `[Serializable]` public-field DTO，没有引入新的 JSON 库。已有 `ClientPingMessage`、ServerMessage 中的 sequence/clientFrame/clientSendTimeMs/serverFrame/serverTimeMs 已存在，但没有实际处理代码。本次只新增两个时间戳字段，不改变原有输入和 frame 字段含义。serverTimeMs 保留，Pong 中与 serverSendTimeMs 相同。

所有服务端消息仍走 `ClientSession` 的 Channel 和唯一 `SendLoopAsync()`。Channel 从字符串元素扩展为延迟生成字符串的工厂，普通消息仍通过原 `Enqueue(string)`，Pong 则在出队时构造 DTO，避免把发送队列等待误算成网络时间。没有额外 SendAsync、发送锁或 Pong 广播。发送异常会 Abort socket，让接收循环也能退出；最终清理仍在 `/ws` 路由的 finally 中。超长消息直接 Abort，避免接收任务中的 CloseAsync 与唯一发送任务发生发送并发。

### 权威帧

`GameRoom.RunTickLoopAsync()` 使用 20 Hz `PeriodicTimer`；在 gate 锁内调用 `TryBuildNextFrame()`，输入不齐就等待。生成完整包后执行 `serverExecutor.Execute(packet)`，转换 inputs、附上 Hash，然后广播到当前会话的发送队列。

仓库已存在 `LastBroadcastFrame`，初始值 -1，但原来在 Execute 后、广播前就更新。本次移动到 BroadcastLocked 后写入。它表示**已执行且已向当前会话发布入队的最后一个权威帧**，不表示下一帧，也不表示客户端已收到。新增 `GetPublishedClock(out frame, out timeMs)` 在同一个 gate 锁内读取帧及单调时间，Pong 在出队时调用它，避免帧和时间来自不一致的发布状态。

### Unity 接收、发送和逻辑

实际库是仓库的 `Assets/WebSocket` NativeWebSocket 实现。native 版本后台 Receive 使用 ConfigureAwait(false)，但构造时捕获 SynchronizationContext，通过 Post 在 Unity 主线程派发 OnMessage；没有同步上下文时才需要库的 DispatchMessageQueue。WebGL 则从 JS 回调派发。

NetworkClient 原来已有 Update：排空到期接收消息，启动唯一异步发送循环。发送和接收各有模拟延迟、随机抖动和 `max(now + delay, lastDueTime)` 保序。原代码还遗留未使用的 delayedOutgoing 和 MaxSendQueueSize，本次移除。原接收队列满时只记录错误并丢弃，本次改为报错、停止连接、清空状态并取消 transport；发送溢出也采取同样处理。

现在 Awake 捕获 Unity 主线程上下文，所有 WebSocket 回调统一通过 OnMainThread 进入；模拟队列、Unity 时间 API、随机数和事件都在主线程处理。无后台线程直接读写模拟队列。异步发送前先从队列移出元素，避免 await 期间断线清空队列后再次 Dequeue。销毁时取消 socket，包括尚在连接中的情况。

NetworkGameRunner 原有事件订阅、inputClock/InputDelay/CommandBuffer、预测和权威帧校验不变。world.CurrentFrame 是已经执行的预测逻辑帧；inputClock 只累积输入 tick，并会在领先过多时 Reset。本次为诊断单独记录收到 start 后的单调时间，Local Clock Frame = floor(elapsed × TickRate) - 1，表示名义本地墙钟帧，**不等于 nextInputFrame 或预测帧**。它不会驱动 GameWorld。原来的 RollbackDebugger、SnapshotRingBuffer、VisualCorrection 均已存在，本次只扩展现有 debugger。

## 时间测量与估算

- t0：Unity 单调毫秒，Ping 入模拟发送队列前记录。
- t1：服务端收到完整 WebSocket 文本后、JSON 解码前记录。
- t2：Pong 从服务端唯一发送队列取出后、序列化和 SendAsync 前记录。
- t3：Unity 接收模拟队列到期、处理 Pong 时读取。

服务端使用 Stopwatch.GetTimestamp / Frequency，客户端使用 Time.realtimeSinceStartupAsDouble。两端起点无需相同。

RTT = (t3 - t0) - (t2 - t1)。其中 t2-t1 包含服务器解析、处理和发送排队；序列化、底层 SendAsync 调度、Unity 主线程派发和模拟队列调度等仍会计入应用层 RTT。时间戳以整毫秒传输，允许不超过 1 ms 的量化误差并钳制为 0。

RTT EMA 的 alpha 为 1/8；Jitter EMA 是连续有效 RTT 差值绝对值的 EMA，同样 alpha=1/8。第一条样本初始化 RTT EMA，Jitter 从 0 开始。

时钟偏差使用 NTP 四时间戳估算：offset = ((t1-t0)+(t2-t3))/2，并用 alpha=1/8 平滑。offset 表示 server clock - client clock。**假设上下行延迟对称**；50 ms 单边模拟延迟会引入约 25 ms 偏差，调度不对称、时钟频率漂移、毫秒量化也会造成误差，不能把该值当作精确绝对时钟。

Server Frame 估算用最近 Pong 的已发布 frame 和 t2-offset 作为客户端时间锚点，按 20 Hz 名义频率外推；frame=-1 时不外推。Frame Offset = 估算服务器帧 - 本地名义墙钟帧。Lockstep 等输入可能停帧，所以外推可能领先实际已发布帧；初始化前不应解读 offset。该功能只供诊断，不跳帧、不修改 world.CurrentFrame、不调整 InputDelay。

Ping 默认每秒一次，可在 Inspector 中调整到 0.02～5 秒。sequence 单调递增；pending 最多 128 项、10 秒过期。只接受已发起、回显时间和帧一致、时序合法且新于上次有效样本的 Pong；重复、过期、未知及无效样本被忽略。断线重置测量、时钟锚点及模拟队列，sequence 保持递增以拒绝旧连接的回显。

## 修改清单

| 文件 | 原职责 | 本次修改及原因 |
|---|---|---|
| FrameSyncDemo/Shared/FrameSync.Shared/ProtocolMessages.cs | 公共协议字段 | 扩展 ServerMessage 的 t1/t2，复用已有 Ping DTO |
| FrameSyncDemo/Server/FrameSync.Server/ProtocolJson.cs | JSON 适配 | 增加现有 Ping DTO 的反序列化，沿用同一 Options |
| FrameSyncDemo/Server/FrameSync.Server/Program.cs | 连接与接收循环 | 在真实入口识别 Ping、记录 t1、单播排队；避免超长消息关闭操作与 sender 并发 |
| FrameSyncDemo/Server/FrameSync.Server/ClientSession.cs | 每连接发送 Channel | 延迟构造 Pong、采样 t2，保持唯一 sender，异常解除接收等待 |
| FrameSyncDemo/Server/FrameSync.Server/GameRoom.cs | 权威帧推进和广播 | 修正发布标记时机、增加锁内帧与时钟只读接口 |
| Assets/Scripts/Network/NetworkClient.cs | Unity 网络与模拟队列 | 周期 Ping、校验回显、队列容量/清理、主线程约束和诊断属性；旧编码转 UTF-8 |
| Assets/Scripts/Network/ClockSynchronizer.cs、.meta | 新增 | 独立 RTT/Jitter/时钟及名义服务器帧估算，无 Unity 状态依赖 |
| Assets/Scripts/Debug/RollbackDebugger.cs | Inspector 和 Game 调试显示 | 在现有调试器增加独立网络指标区；旧编码转 UTF-8 |
| Assets/Scripts/GameWorld/NetworkGameRunner.cs | 输入、预测、校验、视图更新 | 仅增加 debugger.RefreshNetwork(networkClient) 一行 |
| Assets/Plugins/FrameSync.Shared.dll、.xml | Unity 引用 Shared | 更新为实际编译结果，DLL 与 Shared 构建结果 SHA256 相同 |
| FrameSyncDemo/Tests/FrameSync.NetworkTests/* | 新增 | 运行实际客户端网络源码和真实服务端，验证延迟、时钟和协议共存 |
| Docs/NetworkDiagnostics/* | 新增 | 原文件备份、完整代码、编译和验收日志、Unity 程序集编译检查工程 |

NetworkSimulationSettings、DelayNetworkMessage、现有 WebSocket 库及所有模拟核心源码均未修改。未添加动态 InputDelay、重连或状态重同步。

完整新增/修改源代码逐文件收录于 [CompleteCode.md](CompleteCode.md)，也可直接打开表中工作区文件。Before 是原文件副本，不参与 Unity 编译。

## 编译与测试结果

在项目根目录执行：

```powershell
dotnet build FrameSyncDemo/Server/FrameSync.Server --no-restore -m:1
dotnet build FrameSyncDemo/Tests/FrameSync.NetworkTests -m:1
dotnet run --project FrameSyncDemo/Tests/FrameSync.SimTests --no-restore
dotnet run --project FrameSyncDemo/Tests/FrameSync.NetworkTests --no-build
dotnet build Docs/NetworkDiagnostics/UnityCompile --no-restore -m:1
dotnet test FrameSyncDemo/Tests/FrameSync.SimTests --no-restore
```

所有命令成功退出。注意两个 Tests 项目都是控制台验收程序，并非 xUnit/NUnit 工程；`dotnet test` 成功但不运行这些断言，真正的测试结果来自上述 `dotnet run`。

网络测试自动启动真实服务端并连接两个真实 WebSocket 客户端，使用原 NetworkClient、模拟队列和 NativeWebSocket 源码。Unity API 使用最小测试宿主（单调时间/JSON/主线程上下文），不是两个运行中的 Unity 游戏实例。另用本机 Unity 6000.5.3f1 的实际引擎及项目程序集编译全部 Scripts 和 WebSocket 源码，0 错误、6 个 Inspector 序列化引用字段 CS0649 警告。没有将该编译检查冒充 Unity 编辑器运行验收。

服务端和网络测试构建为 0 错误。Shared 全量编译记录 73 个警告，主要是原有 CS8618/CS8625 可空引用和 CS1591 缺少 XML 文档；原测试也存在可空引用警告。未顺带重构这些系统。增量服务端构建日志为 0 警告。

原有回归全部通过：FrameClock、严格 Lockstep、乱序输入、1000 帧双客户端模拟、Replay、CommandBuffer、Snapshot、Rollback。修改前后 1000 帧 Hash 均为 `1D1C0C083F1A7602`，Rollback 最终 Hash 均为 `517F0F2EC8B97B7D`。

本机最终应用层网络测量：

| 配置（发送/接收/抖动 ms） | 实测平均 RTT |
|---|---:|
| 关闭模拟 | 15.6 ms |
| 50 / 0 / 0 | 78.6 ms |
| 0 / 50 / 0 | 78.8 ms |
| 100 / 100 / 0 | 231.9 ms |
| 50 / 50 / 20 | 123.9 ms，Jitter EMA 16.7 ms |

这些数值包含本机操作系统、TCP 和主线程应用调度开销，配置 200 ms 延迟不保证应用层恰好测得 200 ms。

| 验收项 | 结果 |
|---|---|
| 无模拟 Ping/Pong | 通过，包括游戏 start 前 serverFrame=-1 |
| 50 ms 发送方向延迟 | 通过 |
| 50 ms 接收方向延迟 | 通过 |
| 20 ms 随机抖动 | 通过，Jitter 有响应 |
| 配置约 200 ms RTT | 通过，实测含调度约 232 ms |
| 两个客户端同时 Ping | 通过 |
| 高频 Ping 发送并发 | 通过，每客户端 20 ms 周期，与 input 共存无发送错误 |
| 断线停止 Ping | 通过，pending/测量状态清空，close 事件一次 |
| 原 input 处理 | 通过，16 个连续权威帧与独立模拟 Hash 完全一致，Pong 返回最后已发布帧 15 |
| 原 Lockstep/Rollback | 全部通过，Hash 与基线一致 |
| 额外检查 | 时钟不同原点、扣除服务端处理时间、EMA、重复/无效/未知 Pong、40 条消息抖动 FIFO、双向队列溢出明确终止 |

详细证据见 network-tests.log、simulation-tests.log、network-build.log、server-build.log、unity-compile.log。最初沙箱中本机连接失败；授权在沙箱外重跑后通过，首次服务端日志保留为 initial-sandbox-server.log。

## Unity 启动与人工验收

1. 根目录运行 `dotnet run --project FrameSyncDemo/Server/FrameSync.Server`，服务监听 `ws://127.0.0.1:5000/ws`。测试程序也使用端口 5000，自动化验收和手工服务请分开运行。
2. 使用 Unity 6000.5.3f1 打开当前项目，等待 Shared DLL 重新导入，打开 `Assets/Scenes/SampleScene.unity`。现有场景已经绑定 NetworkGameRunner 的 NetworkClient 和 RollbackDebugger，并开启 overlay；不需要新建组件。
3. 用当前源码重新 Build 一个 Windows 客户端，再运行这个新构建和编辑器 Play（也可运行两个新构建）。现有 Build/OnlineGame.exe 是修改前产物，未重新打包，不能用它验证新增功能。
4. 两客户端进入后，Game 窗口原逻辑帧信息下方新增 Network Diagnostics，Inspector 的 RollbackDebugger 也能看到全部网络字段。Ping 默认为每秒一次，Initialized 应变 true，Pong Sequence 持续增长。
5. 在编辑器 NetworkClient 的 Network Simulation 中依次测试：enabled=false；enabled=true/outgoing=50/incoming=0/jitter=0；0/50/0；100/100/0；50/50/20。每次等待数秒让 EMA 收敛。只对一个客户端启用即可观察该连接的双向延迟。随机抖动范围是每方向 ±jitter，负延迟钳制到 0，FIFO 约束会改变实际分布。
6. 玩家 0 用 WASD，玩家 1 用方向键；观察帧连续、Hash 无错配，远端输入变化可触发原回滚。调低 Ping Interval Seconds 至 0.02 观察高频 Ping。不要将网络诊断的名义帧估算当作逻辑执行帧。
7. 关闭一个客户端：其 Ping 停止，另一端收到原房间停止消息。当前架构是一局制，开新局前在服务端输入 restart（或重启进程），退出并重新进入两个客户端。新增功能没有改变这个生命周期。
8. 可用小队列和大延迟验证溢出会明确报错并停止连接，不静默漏过权威帧。自动测试已覆盖溢出，无需在正常游戏中长期使用这种配置。

## 已完成与后续

已完成实际接收入口 Ping 接入、Pong 单播、单 sender、RTT/Jitter EMA、时钟偏差/名义帧估算、双向延迟抖动 FIFO、模拟队列上限及溢出终止、断线清理、现有 debugger 扩展、控制台与真实网络自动验收，以及 Unity 程序集编译检查。

尚需用户在 Unity 中进行画面/Inspector 实际运行验收并重新构建客户端；没有执行 Unity 编辑器 Play 或重打 Windows 包。WebGL 运行平台也尚未验收。

后续可单独考虑非对称延迟校准、更可靠的服务器停帧估计、断线重连和状态重同步。当前沿用服务端原无界 Channel，NativeWebSocket 内部也有其自身缓冲；本次容量控制针对 NetworkClient 的两条应用模拟队列，不宣称已为底层所有网络缓冲添加全链路背压。动态 InputDelay 明确留待后续阶段。
