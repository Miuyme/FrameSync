# 新增和修改文件完整源代码

下面每个代码块均为交付时工作区文件的完整内容。DLL/XML 插件为 Shared 实际编译产物，不以文本重复收录。

## FrameSyncDemo/Shared/FrameSync.Shared/ProtocolMessages.cs

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace FrameSync.Shared
{
    /// <summary>
    /// 客户端发送的输入消息
    /// </summary>
    [Serializable]
    public sealed class ClientInputMessage
    {
        public string type;

        public int targetFrame;

        public int x;

        public int y;
    }
    /// <summary>
    /// 可序列化的客户端ping消息类
    /// 用于客户端与服务器之间的通信，用于检测网络延迟和同步
    /// </summary>
    [Serializable]  // 标记该类可以被序列化，以便在网络传输中转换为可传输的数据格式
    public sealed class ClientPingMessage  // sealed关键字表示该类不可被继承
    {
        public string type;  // 消息类型标识，用于区分不同类型的消息
        public int sequence;  // 消息序列号，用于消息的排序和去重
        public int clientFrame;  // 客户端帧数，用于游戏状态同步
        public long clientSendTimeMs;  // 客户端发送消息的时间戳（毫秒），用于计算网络延迟
    }

    /// <summary>
    /// 网络传输用的单个玩家输入
    /// </summary>
    [Serializable]
    public sealed class InputCommandDto
    {
        public int x;

        public int y;
    }

    /// <summary>
    /// 服务端发送给客户端的消息
    /// </summary>
    [Serializable]
    public sealed class ServerMessage
    {
        /// <summary>
        /// welcome / start / frame / error
        /// </summary>
        public string type;

        /// <summary>
        /// welcome时使用
        /// </summary>
        public int playerId;

        public int playerCount;

        /// <summary>
        /// frame时使用
        /// </summary>
        public int frame;

        public InputCommandDto[] inputs;

        /// <summary>
        /// 服务端模拟后的状态Hash
        /// </summary>
        public string stateHash;

        /// <summary>
        /// error时使用
        /// </summary>
        public string message;

        /// <summary>
        /// 服务器指定的输入延迟
        /// </summary>
        public int inputDelay;
        public int sequence;
        public int clientFrame;
        public long clientSendTimeMs;
        public int serverFrame;
        public long serverTimeMs;
        /// <summary>Server monotonic receive timestamp t1, in milliseconds.</summary>
        public long serverReceiveTimeMs;
        /// <summary>Server monotonic send timestamp t2, in milliseconds.</summary>
        public long serverSendTimeMs;
    }
}
```

## FrameSyncDemo/Server/FrameSync.Server/ProtocolJson.cs

```csharp
using System.Text.Json;
using FrameSync.Shared;

namespace FrameSync.Server
{
    internal static class ProtocolJson
    {
        public static ClientPingMessage DeserializePing(string json)
        {
            return JsonSerializer.Deserialize<ClientPingMessage>(json, Options);
        }

        public static readonly JsonSerializerOptions Options =
            new JsonSerializerOptions
            {
                // 允许序列化 public field
                IncludeFields = true,

                // JSON字段名称不区分大小写
                PropertyNameCaseInsensitive = true
            };
        /// <summary>
        /// 将服务器消息序列化成json
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        public static string Serialize(ServerMessage message)
        {
            return JsonSerializer.Serialize(
                message,
                Options
            );
        }
        /// <summary>
        /// 将json反序列化成玩家输入信息
        /// </summary>
        /// <param name="json"></param>
        /// <returns></returns>
        public static ClientInputMessage DeserializeInput(
            string json)
        {
            return JsonSerializer.Deserialize<ClientInputMessage>(
                json,
                Options
            );
        }
    }
}
```

## FrameSyncDemo/Server/FrameSync.Server/Program.cs

```csharp
using System;
using System.IO;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;

using FrameSync.Shared;
using FrameSync.Server;

using Microsoft.AspNetCore.Builder;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // 整个进程只保留一个控制台读取器，避免重启后多个读取器抢命令。
        var commands = Channel.CreateUnbounded<string>();
        _ = Task.Run(() =>
        {
            string command;
            while ((command = Console.ReadLine()) != null)
                commands.Writer.TryWrite(command);

            commands.Writer.TryComplete();
        });

        while (await RunServerAsync(args, commands.Reader))
            Console.WriteLine("[SERVER] Restarting with a new room...");
    }

    private static async Task<bool> RunServerAsync(
        string[] args,
        ChannelReader<string> commands)
    {
        var builder = WebApplication.CreateBuilder(args);

        // 监听所有网卡
        builder.WebHost.UseUrls(
            "http://0.0.0.0:5000"
        );

        await using var app = builder.Build();

        // 启用WebSocket
        app.UseWebSockets(
            new WebSocketOptions
            {
                KeepAliveInterval =
                    TimeSpan.FromSeconds(20)
            }
        );

        // 创建唯一游戏房间
        GameRoom room = new GameRoom();

        app.MapGet("/", () =>
        {
            return "FrameSync Server Running";
        });

        // WebSocket入口
        app.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            WebSocket socket =
                await context.WebSockets.AcceptWebSocketAsync();

            // 停机时中止旧连接，避免活跃 WebSocket 阻塞服务器重启。
            using var stoppingRegistration =
                app.Lifetime.ApplicationStopping.Register(socket.Abort);

            ClientSession session = room.TryJoin(socket);

            // 房间已经满员或已经停止
            if (session == null)
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await socket.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Room full or already started",
                    closeCts.Token
                    );
                }
                finally
                {
                    socket.Dispose();
                }

                return;
            }

            using CancellationTokenSource sendCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    context.RequestAborted,
                    app.Lifetime.ApplicationStopping
                );

            // 每个客户端只有一个发送循环
            Task sendTask =
                session.SendLoopAsync(sendCts.Token);

            try
            {
                // 当前连接唯一的接收循环
                await ReceiveLoopAsync(
                    socket,
                    session,
                    room,
                    sendCts.Token
                );
            }
            catch (OperationCanceledException)
            {
                // 客户端或服务器取消连接
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[WS ERROR] {ex.Message}"
                );
            }
            finally
            {
                room.Leave(session);

                session.Complete();

                sendCts.Cancel();

                try
                {
                    await sendTask;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Console.WriteLine(
                        $"[SEND ERROR] {ex.Message}"
                    );
                }

                try
                {
                    if (socket.State == WebSocketState.Open ||
                        socket.State == WebSocketState.CloseReceived)
                    {
                        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closing",
                            closeCts.Token
                        );
                    }
                }
                catch
                {
                    // 连接可能已经关闭
                }

                socket.Dispose();
            }
        });

        await app.StartAsync();

        // 启动服务端逻辑帧循环
        Task tickTask =
            room.RunTickLoopAsync(
                app.Lifetime.ApplicationStopping
            );

        Console.WriteLine(
            "FrameSync Server Starting..."
        );

        Console.WriteLine(
            "HTTP: http://127.0.0.1:5000"
        );

        Console.WriteLine(
            "WebSocket: ws://127.0.0.1:5000/ws"
        );

        Console.WriteLine("Commands: restart / stop (press Enter)");
        bool restartRequested = false;
        Task commandTask = HandleCommandsAsync();
        try
        {
            await app.WaitForShutdownAsync();
        }
        finally
        {
            app.Lifetime.StopApplication();
            await commandTask;
            try
            {
                await tickTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // 离开方法前 await using 会释放旧应用，再由 Main 创建新应用和房间。
        return restartRequested;

        async Task HandleCommandsAsync()
        {
            try
            {
                await foreach (string command in commands.ReadAllAsync(
                    app.Lifetime.ApplicationStopping))
                {
                    switch (command.Trim().ToLowerInvariant())
                    {
                        case "restart":
                            restartRequested = true;
                            app.Lifetime.StopApplication();
                            return;
                        case "stop":
                        case "exit":
                            app.Lifetime.StopApplication();
                            return;
                        case "":
                            break;
                        default:
                            Console.WriteLine("Unknown command. Use restart or stop.");
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C 或应用停机时结束命令等待。
            }
        }
    }

    /// <summary>
    /// WebSocket接收循环
    /// </summary>
    private static async Task ReceiveLoopAsync(
        WebSocket socket,
        ClientSession session,
        GameRoom room,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using MemoryStream stream = new MemoryStream();

            WebSocketReceiveResult result;

            // 处理可能被拆分的WebSocket消息
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer),cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }    
                
                if (result.MessageType != WebSocketMessageType.Text)  
                {
                    room.SendError(session,"Only text messages are supported");

                    return;
                }

                // 限制单条消息最大4KB
                if (stream.Length + result.Count > 4096)
                {
                    socket.Abort(); // CloseAsync would race the session's sole sender.
                        
                    return;
                }

                stream.Write(buffer, 0,result.Count);
            } while (!result.EndOfMessage);

            long receiveTimeMs = (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
            string json = Encoding.UTF8.GetString(
                stream.ToArray()
            );

            ClientInputMessage message;

            try
            {
                message = ProtocolJson.DeserializeInput(json);   
                if (message != null && message.type == "ping")
                {
                    ClientPingMessage ping = ProtocolJson.DeserializePing(json);
                    if (ping.sequence <= 0 || ping.clientFrame < -1 || ping.clientSendTimeMs < 0)
                        room.SendError(session, "Invalid ping");
                    else if (!session.EnqueuePong(ping, room, receiveTimeMs))
                        return;
                    continue;
                }
            }
            catch (JsonException)
            {
                room.SendError(
                    session,
                    "Invalid JSON"
                );

                continue;
            }

            if (message == null ||
                message.type != "input")
            {
                room.SendError(
                    session,
                    "Invalid message type"
                );

                continue;
            }

            // 将输入交给GameRoom
            room.SubmitInput(
                session,
                message
            );
        }
    }
}
```

## FrameSyncDemo/Server/FrameSync.Server/ClientSession.cs

```csharp
using System.Net.WebSockets;
using System;
using FrameSync.Shared;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FrameSync.Server
{
    /// <summary>
    /// 一个客户端的网络会话
    /// </summary>
    public sealed class ClientSession
    {
        /// <summary>
        /// 玩家编号
        /// </summary>
        public int PlayerId { get; }

        /// <summary>
        /// WebSocket连接
        /// </summary>
        public WebSocket Socket { get; }

        /// <summary>
        /// 线程安全的发送消息队列
        /// </summary>
        private readonly Channel<Func<string>> sendQueue =
            Channel.CreateUnbounded<Func<string>>(
                new UnboundedChannelOptions
                {
                    SingleReader = true, //  设置为单读者模式，即同一时间只有一个消费者可以读取消息
                    SingleWriter = false //  设置为多写者模式，允许多个生产者同时写入消息
                }
            );

        public ClientSession(int playerId, WebSocket socket)
        {
            PlayerId = playerId;
            Socket = socket;
        }

        /// <summary>
        /// 将消息放入发送队列
        /// </summary>
        public bool Enqueue(string message)
        {
            return sendQueue.Writer.TryWrite(() => message);
        }

        // Deferred serialization includes time spent waiting behind frame messages in t2-t1.
        public bool EnqueuePong(ClientPingMessage ping, GameRoom room, long receiveTimeMs)
        {
            return sendQueue.Writer.TryWrite(() =>
            {
                room.GetPublishedClock(out int frame, out long sendTimeMs);
                return ProtocolJson.Serialize(new ServerMessage
                {
                    type = "pong",
                    sequence = ping.sequence,
                    clientFrame = ping.clientFrame,
                    clientSendTimeMs = ping.clientSendTimeMs,
                    serverFrame = frame,
                    serverReceiveTimeMs = receiveTimeMs,
                    serverSendTimeMs = sendTimeMs,
                    serverTimeMs = sendTimeMs
                });
            });
        }

        /// <summary>
        /// 关闭发送队列
        /// </summary>
        public void Complete()
        {
            sendQueue.Writer.TryComplete();
        }

        /// <summary>
        /// 异步发送消息循环方法，唯一的WebSocket发送循环
        /// </summary>
        /// <param name="cancellationToken">取消令牌，用于取消异步操作</param>
        /// <returns>一个Task，代表异步操作的完成</returns>
        public async Task SendLoopAsync(CancellationToken cancellationToken)   
        {
            try
            {
                await foreach (Func<string> createMessage in sendQueue.Reader.ReadAllAsync(cancellationToken))
                {
                    if (Socket.State != WebSocketState.Open)
                        break;

                    byte[] data = Encoding.UTF8.GetBytes(createMessage());
                    await Socket.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Text, true, cancellationToken);
                }
            }
            catch
            {
                Socket.Abort(); // Unblock ReceiveAsync when the sender fails or is cancelled.
                throw;
            }
        }
    }
}
```

## FrameSyncDemo/Server/FrameSync.Server/GameRoom.cs

```csharp
using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

using FrameSync.Shared;

namespace FrameSync.Server
{
    /// <summary>
    /// 一个双人游戏房间
    /// </summary>
    public sealed class GameRoom
    {
        private readonly object gate = new object();

        // 两个玩家的网络会话
        private readonly ClientSession[] sessions =
            new ClientSession[GameWorld.PlayerCount];

        // 帧管理器
        private readonly FrameManager frameManager =
            new FrameManager(GameWorld.PlayerCount);

        // 服务端自己的游戏世界
        private readonly GameWorld serverWorld =
            new GameWorld();

        // 服务端执行器
        private readonly FrameExecutor serverExecutor;

        private bool started;

        private bool stopped;

        public const int InputDelayFrames = 3;
        private int lastBroadcastFrame = -1;

        public int LastBroadcastFrame => Volatile.Read(ref lastBroadcastFrame);

        // Publication means enqueueing to every current session, not delivery acknowledgement.
        public void GetPublishedClock(out int frame, out long timeMs)
        {
            lock (gate)
            {
                frame = lastBroadcastFrame;
                timeMs = (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
            }
        }

        public GameRoom()
        {
            serverExecutor = new FrameExecutor(serverWorld);
        }

        /// <summary>
        /// 玩家加入房间
        /// </summary>
        public ClientSession TryJoin(WebSocket socket)
        {
            lock (gate)
            {
                // 本版本只允许一局游戏
                if (started || stopped)
                    return null;

                int playerId = -1;

                for (int i = 0; i < sessions.Length; i++)
                {
                    if (sessions[i] == null)
                    {
                        playerId = i;
                        break;
                    }
                }

                // 房间已满
                if (playerId == -1)
                    return null;

                ClientSession session =
                    new ClientSession(playerId, socket);

                sessions[playerId] = session;

                Console.WriteLine(
                    $"[JOIN] Player {playerId} connected"
                );

                // 通知当前客户端自己的玩家编号
                SendLocked(
                    session,
                    new ServerMessage
                    {
                        type = "welcome",
                        playerId = playerId,
                        playerCount = GameWorld.PlayerCount
                    }
                );

                // 两名玩家都已加入
                if (sessions[0] != null &&
                    sessions[1] != null)
                {
                    started = true;

                    Console.WriteLine("[ROOM] Game started");

                    BroadcastLocked(
                        new ServerMessage
                        {
                            type = "start",
                            playerCount = GameWorld.PlayerCount,
                            inputDelay=InputDelayFrames
                        }
                    );
                }

                return session;
            }
        }

        /// <summary>
        /// 接收玩家输入
        /// </summary>
        public void SubmitInput(
            ClientSession session,
            ClientInputMessage message)
        {
            lock (gate)
            {
                if (stopped || !started)
                    return;

                // 确认该Session仍然属于房间
                if (session.PlayerId < 0 ||
                    session.PlayerId >= sessions.Length ||
                    !ReferenceEquals(
                        sessions[session.PlayerId],
                        session))
                {
                    return;
                }

                PlayerCommand command =
                    new PlayerCommand(
                        message.x,
                        message.y
                    );

                InputSubmitResult result =
                    frameManager.SubmitInput(
                        session.PlayerId,
                        message.targetFrame,
                        command
                    );

                if (result != InputSubmitResult.Accepted)
                {
                    SendErrorLocked(
                        session,
                        $"Input rejected: {result}"
                    );

                    Console.WriteLine(
                        $"[INPUT ERROR] P{session.PlayerId} " +
                        $"Frame {message.targetFrame}: {result}"
                    );

                    return;
                }

                Console.WriteLine(
                    $"[INPUT] P{session.PlayerId} " +
                    $"Frame {message.targetFrame} " +
                    $"({message.x},{message.y})"
                );
            }
        }

        /// <summary>
        /// 服务端固定逻辑帧循环
        /// </summary>
        public async Task RunTickLoopAsync(
            CancellationToken cancellationToken)
        {
            TimeSpan interval =
                TimeSpan.FromSeconds(
                    1.0 / GameWorld.TickRate
                );

            using (PeriodicTimer timer =
                new PeriodicTimer(interval))
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    lock (gate)
                    {
                        if (!started || stopped)
                            continue;

                        // 尝试生成下一帧
                        if (!frameManager.TryBuildNextFrame(
                            out FramePacket packet))
                        {
                            // 输入未收齐，等待下一次Tick
                            continue;
                        }

                        // 服务端执行这帧
                        ulong hash = serverExecutor.Execute(packet);

                        // 将FramePacket转换成网络DTO
                        InputCommandDto[] inputs = new InputCommandDto[packet.PlayerCount];
                            

                        for (int i = 0; i < packet.PlayerCount; i++)
                        {
                            PlayerCommand command = packet.GetCommand(i);
                                

                            inputs[i] = new InputCommandDto
                            {
                                x = command.X,
                                y = command.Y
                            };
                        }

                        // 创建广播消息
                        ServerMessage message =
                            new ServerMessage
                            {
                                type = "frame",
                                frame = packet.Frame,
                                inputs = inputs,
                                stateHash = hash.ToString("X16")
                            };

                        // 广播给所有客户端
                        BroadcastLocked(message);
                        Volatile.Write(ref lastBroadcastFrame, packet.Frame);

                        Console.WriteLine(
                            $"[FRAME] {packet.Frame} " +
                            $"Hash={hash:X16}"
                        );
                    }
                }
            }
        }

        /// <summary>
        /// 客户端断开连接
        /// </summary>
        public void Leave(ClientSession session)
        {
            lock (gate)
            {
                int playerId = session.PlayerId;

                if (playerId < 0 ||
                    playerId >= sessions.Length)
                {
                    return;
                }

                if (!ReferenceEquals(
                    sessions[playerId],
                    session))
                {
                    return;
                }

                sessions[playerId] = null;

                Console.WriteLine(
                    $"[LEAVE] Player {playerId} disconnected"
                );

                if (!stopped)
                {
                    stopped = true;

                    BroadcastLocked(
                        new ServerMessage
                        {
                            type = "error",
                            message =
                                $"Player {playerId} disconnected. " +
                                "Room stopped."
                        }
                    );
                }
            }
        }

        /// <summary>
        /// 向指定客户端发送错误
        /// </summary>
        public void SendError(
            ClientSession session,
            string message)
        {
            lock (gate)
            {
                SendErrorLocked(session, message);
            }
        }

        private void SendErrorLocked(
            ClientSession session,
            string message)
        {
            SendLocked(
                session,
                new ServerMessage
                {
                    type = "error",
                    message = message
                }
            );
        }

        /// <summary>
        /// 向指定玩家发送消息
        /// </summary>
        private void SendLocked(
            ClientSession session,
            ServerMessage message)
        {
            string json = ProtocolJson.Serialize(message);

            session.Enqueue(json);
        }

        /// <summary>
        /// 广播消息
        /// </summary>
        private void BroadcastLocked(ServerMessage message)
        {
            string json = ProtocolJson.Serialize(message);

            for (int i = 0; i < sessions.Length; i++)
            {
                if (sessions[i] != null)
                {
                    sessions[i].Enqueue(json);
                }
            }
        }
    }
}
```

## Assets/Scripts/Network/NetworkClient.cs

```csharp
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

using NativeWebSocket;

using FrameSync.Shared;

using UnityEngine;

public class NetworkClient : MonoBehaviour
{
    [Header("Server")]
    [SerializeField]
    private string serverUrl =
        "ws://127.0.0.1:5000/ws";

    private WebSocket websocket;

    // 待发送消息
    private readonly Queue<DelayedNetworkMessage<string>> sendQueue = new Queue<DelayedNetworkMessage<string>>();


    private SynchronizationContext mainContext;
    private bool stopped;
    private int pingSequence;
    private double nextPingTime;
    private double localClockStart = -1;
    private readonly Dictionary<int, ClientPingMessage> pendingPings = new Dictionary<int, ClientPingMessage>();
    private readonly List<int> expiredPings = new List<int>();
    [Header("Diagnostics")]
    [SerializeField, Range(0.02f, 5f)] private float pingIntervalSeconds = 1f;
    public ClockSynchronizer Clock { get; } = new ClockSynchronizer();
    public int PingSequence => pingSequence;
    public int ClientLocalClockFrame => localClockStart < 0 ? -1 : (int)((Time.realtimeSinceStartupAsDouble - localClockStart) * GameWorld.TickRate) - 1;
    public double EstimatedServerFrame => Clock.EstimateServerFrame(Time.realtimeSinceStartupAsDouble * 1000, GameWorld.TickRate);
    public double FrameOffset => EstimatedServerFrame - ClientLocalClockFrame;

    private void Awake()
    {
        mainContext = SynchronizationContext.Current;
    }

    // All queues, Unity APIs and subscriber events belong to the Unity main thread.
    private void OnMainThread(Action action)
    {
        if (SynchronizationContext.Current == mainContext)
            action();
        else
            mainContext.Post(_ => action(), null);
    }

    // 是否正在发送
    private bool isSending;

    public int PlayerId { get; private set; } = -1;

    public bool IsOpen => !stopped && websocket != null && websocket.State == WebSocketState.Open;
    public int MaxFrameRate = 180;

    [Header("Network Simulation")]
    [SerializeField] private NetworkSimulationSettings simulation = new NetworkSimulationSettings();


    private readonly Queue<DelayedNetworkMessage<byte[]>> delayedIncoming = new Queue<DelayedNetworkMessage<byte[]>>();

    private double lastOutgoingDueTime;
    private double lastIncomingDueTime;


    public event Action<int> WelcomeReceived;

    // 修改：现在会传入服务器配置的InputDelay
    public event Action<int> GameStarted;

    public event Action<ServerMessage> FrameReceived;

    public event Action<string> ServerErrorReceived;

    public event Action ConnectionClosed;

    private async void Start()
    {
        Application.targetFrameRate = MaxFrameRate;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        ResetDiagnostics();
        stopped = false;
        websocket = new WebSocket(serverUrl);

        websocket.OnOpen += () => OnMainThread(() =>
        {
            Debug.Log("[WS] Connected");
        });

        websocket.OnError += error => OnMainThread(() => FailConnection("[WS ERROR] " + error));
        websocket.OnClose += code => OnMainThread(() => StopConnection());
        websocket.OnMessage += bytes => OnMainThread(() =>
        {
            if (!stopped)
                ScheduleIncomingMessage(bytes);
        });

        try
        {
            await websocket.Connect();
        }
        catch (Exception ex)
        {
            FailConnection("[WS CONNECT ERROR] " + ex.Message);
        }
    }

    /// <summary>
    /// Unity每帧检查发送队列
    /// </summary>
    private void Update()
    {
        if (stopped)
            return;
        DispatchIncomingMessages();
        if (stopped || !IsOpen)
            return;
        UpdatePing();

        if (!isSending && IsOpen && sendQueue.Count > 0)
            _ = FlushSendQueueAsync();
    }

    /// <summary>
    /// 接收服务器消息
    /// </summary>
    private void HandleMessage(byte[] bytes)
    {
        string json = System.Text.Encoding.UTF8.GetString(bytes);


        ServerMessage message;

        try
        {
            message =
                JsonUtility.FromJson<ServerMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[WS JSON ERROR] " + ex.Message
            );

            return;
        }

        if (message == null)
            return;

        switch (message.type)
        {
            case "welcome":

                PlayerId = message.playerId;

                Debug.Log(
                    $"[ROOM] PlayerId={PlayerId}"
                );

                WelcomeReceived?.Invoke(PlayerId);

                break;

            case "pong":
                ReceivePong(message);
                break;

            case "start":
                if (localClockStart < 0)
                    localClockStart = Time.realtimeSinceStartupAsDouble;

                Debug.Log(
                    $"[ROOM] Started. InputDelay={message.inputDelay}"
                );

                GameStarted?.Invoke(message.inputDelay);

                break;

            case "frame":

                FrameReceived?.Invoke(message);

                break;

            case "error":

                Debug.LogError("[SERVER ERROR] " + message.message);

                ServerErrorReceived?.Invoke(message.message);

                break;
        }
    }

    /// <summary>
    /// 将输入放入发送队列
    /// 不等待网络发送
    /// </summary>
    public bool EnqueueInput(int targetFrame, PlayerCommand command)
    {
        if (!IsOpen)
            return false;

        ClientInputMessage message = new ClientInputMessage
        {
            type = "input",
            targetFrame = targetFrame,
            x = command.X,
            y = command.Y
        };

        return EnqueueMessage(JsonUtility.ToJson(message));
    }

    /// <summary>
    /// 唯一的WebSocket发送循环
    /// </summary>
    private async Task FlushSendQueueAsync()
    {
        if (isSending)
            return;

        isSending = true;

        try
        {
            while (IsOpen && sendQueue.Count > 0)
            {
                DelayedNetworkMessage<string> item = sendQueue.Peek();

                if (item.DueTime > Time.realtimeSinceStartupAsDouble)
                    break;

                // Remove before await: OnClose/OnDestroy can clear the queue while sending.
                sendQueue.Dequeue();
                await websocket.SendText(item.Data);
            }
        }
        catch (Exception ex)
        {
            FailConnection("[WS SEND ERROR] " + ex.Message);
        }
        finally
        {
            isSending = false;
        }
    }

    private void OnDestroy()
    {
        StopConnection();
        websocket?.CancelConnection();
    }
    private void DispatchIncomingMessages()
    {
        double now = Time.realtimeSinceStartupAsDouble;

        while (!stopped && delayedIncoming.Count > 0)
        {
            DelayedNetworkMessage<byte[]> item = delayedIncoming.Peek();

            if (item.DueTime > now)
                break;

            delayedIncoming.Dequeue();

            HandleMessage(item.Data);
        }
    }
    private double CalculateDueTime(int baseDelayMs, ref double lastDueTime)
    {
        double now = Time.realtimeSinceStartupAsDouble;

        double jitter = simulation.enabled ? UnityEngine.Random.Range(-simulation.jitterMs, simulation.jitterMs) : 0;

        double delay = simulation.enabled ? Math.Max(0, baseDelayMs + jitter) / 1000.0 : 0;

        double dueTime = Math.Max(now + delay, lastDueTime);

        lastDueTime = dueTime;

        return dueTime;
    }
    private void ScheduleIncomingMessage(byte[] bytes)
    {
        if (delayedIncoming.Count >= Math.Max(1, simulation.maxQueueSize))
        {
            FailConnection("[NETSIM] Incoming queue overflow; connection stopped to preserve frame order");
            return;
        }

        byte[] copy = new byte[bytes.Length];

        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);

        double dueTime = CalculateDueTime(simulation.incomingDelayMs, ref lastIncomingDueTime);

        delayedIncoming.Enqueue(new DelayedNetworkMessage<byte[]>(copy, dueTime));
    }

    private bool EnqueueMessage(string json)
    {
        if (stopped || !IsOpen)
            return false;
        if (sendQueue.Count >= Math.Max(1, simulation.maxQueueSize))
        {
            FailConnection("[NETSIM] Outgoing queue overflow; connection stopped");
            return false;
        }

        double dueTime = CalculateDueTime(simulation.outgoingDelayMs, ref lastOutgoingDueTime);

        sendQueue.Enqueue(new DelayedNetworkMessage<string>(json, dueTime));

        return true;
    }

    private void UpdatePing()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        expiredPings.Clear();
        foreach (var entry in pendingPings)
        {
            if (now * 1000 - entry.Value.clientSendTimeMs > 10000)
                expiredPings.Add(entry.Key);
        }
        foreach (int sequence in expiredPings)
            pendingPings.Remove(sequence);
        if (now < nextPingTime || pendingPings.Count >= 128)
            return;
        nextPingTime = now + Math.Max(0.02, pingIntervalSeconds);
        if (pingSequence == int.MaxValue)
            return;
        var ping = new ClientPingMessage
        {
            type = "ping",
            sequence = ++pingSequence,
            clientFrame = ClientLocalClockFrame,
            clientSendTimeMs = (long)(now * 1000)
        };
        // t0 precedes simulated outgoing delay; t3 follows simulated incoming delay.
        pendingPings.Add(ping.sequence, ping);
        if (!EnqueueMessage(JsonUtility.ToJson(ping)))
            pendingPings.Remove(ping.sequence);
    }

    private void ReceivePong(ServerMessage message)
    {
        if (!pendingPings.TryGetValue(message.sequence, out ClientPingMessage ping))
            return;
        if (message.clientFrame != ping.clientFrame || message.clientSendTimeMs != ping.clientSendTimeMs)
            return;
        if (Clock.AddSample(message, Time.realtimeSinceStartupAsDouble * 1000))
            pendingPings.Remove(message.sequence);
    }

    private void ResetDiagnostics()
    {
        sendQueue.Clear();
        delayedIncoming.Clear();
        pendingPings.Clear();
        lastOutgoingDueTime = lastIncomingDueTime = 0;
        nextPingTime = 0;
        localClockStart = -1;
        PlayerId = -1;
        Clock.Reset();
        // Sequence remains increasing across connections, rejecting stale echoes.
    }

    private void StopConnection()
    {
        if (stopped)
            return;
        stopped = true;
        ResetDiagnostics();
        ConnectionClosed?.Invoke();
    }

    private void FailConnection(string reason)
    {
        if (stopped)
            return;
        Debug.LogError(reason);
        StopConnection();
        ServerErrorReceived?.Invoke(reason);
        // CancelConnection also stops a connection attempt or a blocked send.
        websocket?.CancelConnection();
    }
}
```

## Assets/Scripts/Network/ClockSynchronizer.cs

```csharp
using System;
using FrameSync.Shared;

/// <summary>Diagnostics only. Times are monotonic milliseconds in each peer's own epoch.</summary>
public sealed class ClockSynchronizer
{
    private const double Alpha = 0.125;
    private double anchorClientTimeMs;
    private double anchorFrame;
    public bool Initialized { get; private set; }
    public double RttMs { get; private set; }
    public double SmoothedRttMs { get; private set; }
    public double JitterMs { get; private set; }
    public double ClockOffsetMs { get; private set; }
    public int LastSequence { get; private set; }
    public int ServerFrame { get; private set; } = -1;

    public bool AddSample(ServerMessage pong, double receiveTimeMs)
    {
        if (pong == null)
            return false;
        double t0 = pong.clientSendTimeMs;
        double t1 = pong.serverReceiveTimeMs;
        double t2 = pong.serverSendTimeMs;
        double t3 = receiveTimeMs;
        double rtt = (t3 - t0) - (t2 - t1);
        if (double.IsNaN(t3) || double.IsInfinity(t3) || pong.sequence <= LastSequence || pong.serverFrame < -1 || t0 < 0 || t1 < 0 || t2 < t1 || t3 < t0 || rtt < -1 || t3 - t0 > 10000)
            return false;
        rtt = Math.Max(0, rtt); // Integer timestamp quantization can contribute up to 1 ms.
        double offset = ((t1 - t0) + (t2 - t3)) / 2;
        if (!Initialized)
        {
            SmoothedRttMs = rtt;
            ClockOffsetMs = offset;
        }
        else
        {
            JitterMs += Alpha * (Math.Abs(rtt - RttMs) - JitterMs);
            SmoothedRttMs += Alpha * (rtt - SmoothedRttMs);
            ClockOffsetMs += Alpha * (offset - ClockOffsetMs);
        }
        RttMs = rtt;
        LastSequence = pong.sequence;
        ServerFrame = pong.serverFrame;
        anchorFrame = pong.serverFrame;
        anchorClientTimeMs = t2 - ClockOffsetMs;
        Initialized = true;
        return true;
    }

    // Nominal extrapolation; lockstep may stall while waiting for input.
    public double EstimateServerFrame(double clientTimeMs, int tickRate)
    {
        if (!Initialized || ServerFrame < 0)
            return -1;
        return anchorFrame + Math.Max(0, clientTimeMs - anchorClientTimeMs) * tickRate / 1000;
    }

    public void Reset()
    {
        Initialized = false;
        RttMs = SmoothedRttMs = JitterMs = ClockOffsetMs = 0;
        LastSequence = 0;
        ServerFrame = -1;
        anchorClientTimeMs = anchorFrame = 0;
    }
}
```

## Assets/Scripts/Network/ClockSynchronizer.cs.meta

```yaml
fileFormatVersion: 2
guid: 439ad70e6079290459946ca81d028c61
```

## Assets/Scripts/Debug/RollbackDebugger.cs

```csharp

using FrameSync.Shared;
using UnityEngine;

/// <summary>
/// 帧同步运行时调试信息
/// </summary>
public sealed class RollbackDebugger : MonoBehaviour
{
    [Header("Frame State")]
    [SerializeField] private int serverFrame = -1;
    [SerializeField] private int predictedFrame = -1;
    [SerializeField] private int predictionLead;

    [Header("Rollback Statistics")]
    [SerializeField] private int rollbackCount;
    [SerializeField] private int lastRollbackFrame = -1;
    [SerializeField] private int lastReplayFrames;
    [SerializeField] private int totalReplayedFrames;

    [Header("Buffers")]
    [SerializeField] private int snapshotCapacity;
    [SerializeField] private int historyCapacity;
    [SerializeField] private int queuedInputs;
    [SerializeField] private int nextInputFrame;

    [Header("State Hash")]
    [SerializeField] private string currentPredictedHash;
    [SerializeField] private string lastServerHash;

    [Header("Network Diagnostics")]
    [SerializeField] private double rttMs;
    [SerializeField] private double smoothedRttMs;
    [SerializeField] private double jitterMs;
    [SerializeField] private int pingSequence;
    [SerializeField] private int pongSequence;
    [SerializeField] private int publishedServerFrame = -1;
    [SerializeField] private int clientLocalClockFrame = -1;
    [SerializeField] private double estimatedServerFrame = -1;
    [SerializeField] private double frameOffset;
    [SerializeField] private double clockOffsetMs;
    [SerializeField] private bool clockSynchronizerInitialized;

    public void RefreshNetwork(NetworkClient client)
    {
        if (client == null)
            return;
        rttMs = client.Clock.RttMs;
        smoothedRttMs = client.Clock.SmoothedRttMs;
        jitterMs = client.Clock.JitterMs;
        pingSequence = client.PingSequence;
        pongSequence = client.Clock.LastSequence;
        publishedServerFrame = client.Clock.ServerFrame;
        clientLocalClockFrame = client.ClientLocalClockFrame;
        estimatedServerFrame = client.EstimatedServerFrame;
        frameOffset = client.FrameOffset;
        clockOffsetMs = client.Clock.ClockOffsetMs;
        clockSynchronizerInitialized = client.Clock.Initialized;
    }

    [Header("Display")]
    [SerializeField] private bool showOverlay = true;

    public void Refresh(RollbackSimulator simulator, int queuedInputCount, int nextInput, string serverHash)
    {
        if (simulator == null)
            return;

        serverFrame = simulator.LastAuthoritativeFrame;
        predictedFrame = simulator.CurrentFrame;
        predictionLead = simulator.PredictionLead;

        rollbackCount = simulator.RollbackCount;
        lastRollbackFrame = simulator.LastRollbackFrame;
        lastReplayFrames = simulator.LastReplayFrameCount;
        totalReplayedFrames = simulator.TotalReplayedFrames;

        snapshotCapacity = simulator.SnapshotCapacity;
        historyCapacity = simulator.HistoryCapacity;

        queuedInputs = queuedInputCount;
        nextInputFrame = nextInput;

        currentPredictedHash = simulator.CurrentHash.ToString("X16");
        lastServerHash = serverHash;
    }

    private void OnGUI()
    {
        if (!showOverlay)
            return;

        GUILayout.BeginArea(new Rect(10, 10, 440, 430), GUI.skin.box);

        GUILayout.Label("FrameSync Debugger");
        GUILayout.Label($"Server Frame: {serverFrame}");
        GUILayout.Label($"Predicted Frame: {predictedFrame}");
        GUILayout.Label($"Prediction Lead: {predictionLead}");
        GUILayout.Label($"Rollback Count: {rollbackCount}");
        GUILayout.Label($"Last Rollback: {lastRollbackFrame}");
        GUILayout.Label($"Last Replay Frames: {lastReplayFrames}");
        GUILayout.Label($"Total Replayed: {totalReplayedFrames}");
        GUILayout.Label($"Queued Inputs: {queuedInputs}");
        GUILayout.Label($"Local Hash: {currentPredictedHash}");
        GUILayout.Label($"Server Hash: {lastServerHash}");

        GUILayout.Space(8);
        GUILayout.Label("Network Diagnostics (estimates only)");
        GUILayout.Label($"RTT: {rttMs:F1} ms | EMA: {smoothedRttMs:F1} ms | Jitter: {jitterMs:F1} ms");
        GUILayout.Label($"Ping Sequence: {pingSequence} | Pong: {pongSequence}");
        GUILayout.Label($"Published Server Frame: {publishedServerFrame}");
        GUILayout.Label($"Client Local Clock Frame: {clientLocalClockFrame}");
        GUILayout.Label($"Estimated Server Frame: {estimatedServerFrame:F2} | Offset: {frameOffset:F2}");
        GUILayout.Label($"Clock Offset: {clockOffsetMs:F1} ms | Initialized: {clockSynchronizerInitialized}");
        GUILayout.EndArea();
    }
}
```

## Assets/Scripts/GameWorld/NetworkGameRunner.cs

```csharp

using System;
using System.Globalization;
using FrameSync.Shared;
using UnityEngine;

public sealed class NetworkGameRunner : MonoBehaviour
{
    [Header("Network")]
    [SerializeField] private NetworkClient networkClient;

    [Header("Player Views")]
    [SerializeField] private Transform player0View;
    [SerializeField] private Transform player1View;

    [Header("Debug")]
    [SerializeField] private RollbackDebugger debugger;

    [Header("Visual Correction")]
    [SerializeField] private float correctionHalfLife = 0.08f;
    [SerializeField] private float snapDistance = 2f;

    private GameWorld world;
    private RollbackSimulator rollback;
    private VisualCorrection visuals;

    private InputCollector inputCollector;
    private CommandBuffer commandBuffer;
    private FrameClock inputClock;

    private PlayerState[] previousStates;
    private PlayerState[] currentStates;

    private int localPlayerId = -1;
    private int nextInputFrame;
    private int lastServerFrame = -1;
    private int inputDelay;

    private bool gameStarted;
    private bool inputInitialized;

    private string lastServerHash = "";

    private float renderElapsed;

    private const int MaxExtraLeadFrames = 2;
    private const int MaxInputTicksPerUpdate = 4;
    private const int MaxRollbackFrames = 120;
    private const int MaxPredictionLead = 8;

    private const float TickInterval = 1f / GameWorld.TickRate;

    private void Awake()
    {
        Application.runInBackground = true;

        world = new GameWorld();

        inputCollector = new InputCollector();
        commandBuffer = new CommandBuffer(0, 256);
        inputClock = new FrameClock(GameWorld.TickRate);

        previousStates = new PlayerState[GameWorld.PlayerCount];
        currentStates = new PlayerState[GameWorld.PlayerCount];

        visuals = new VisualCorrection(player0View, player1View, correctionHalfLife, snapDistance);

        CaptureStates();
        CopyCurrentToPrevious();
        visuals.Snap(currentStates);
    }

    private void OnEnable()
    {
        if (networkClient == null)
        {
            Debug.LogError("NetworkClient is not assigned");
            return;
        }

        networkClient.WelcomeReceived += OnWelcome;
        networkClient.GameStarted += OnGameStarted;
        networkClient.FrameReceived += OnFrameReceived;
        networkClient.ServerErrorReceived += OnServerError;
        networkClient.ConnectionClosed += OnConnectionClosed;
    }

    private void OnDisable()
    {
        if (networkClient == null)
            return;

        networkClient.WelcomeReceived -= OnWelcome;
        networkClient.GameStarted -= OnGameStarted;
        networkClient.FrameReceived -= OnFrameReceived;
        networkClient.ServerErrorReceived -= OnServerError;
        networkClient.ConnectionClosed -= OnConnectionClosed;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        if (gameStarted)
        {
            CollectInputTicks();
            renderElapsed += dt;
        }

        float alpha = Mathf.Clamp01(renderElapsed / TickInterval);

        visuals.Render(previousStates, currentStates, alpha, dt);

        if (debugger != null)
        {
            debugger.Refresh(rollback, commandBuffer.Count, nextInputFrame, lastServerHash);
            debugger.RefreshNetwork(networkClient);
        }
    }

    private void OnWelcome(int playerId)
    {
        if (rollback != null)
        {
            StopSimulation("Duplicate welcome");
            return;
        }

        localPlayerId = playerId;

        rollback = new RollbackSimulator(world, localPlayerId, MaxRollbackFrames, MaxPredictionLead);

        Debug.Log($"[GAME] PlayerId={localPlayerId}");
    }

    private void OnGameStarted(int serverInputDelay)
    {
        if (localPlayerId < 0 || rollback == null)
        {
            StopSimulation("PlayerId not assigned");
            return;
        }

        if (inputInitialized)
            return;

        if (serverInputDelay < 0 || serverInputDelay > MaxPredictionLead)
        {
            StopSimulation("Invalid InputDelay");
            return;
        }

        inputDelay = serverInputDelay;
        inputInitialized = true;
        gameStarted = true;

        inputClock.Reset();

        nextInputFrame = 0;
        lastServerFrame = -1;

        PlayerCommand neutral = new PlayerCommand(0, 0);

        // 预填充输入延迟帧
        for (int i = 0; i < inputDelay; i++)
        {
            if (!RecordAndQueueInput(neutral))
            {
                StopSimulation("Initial input setup failed");
                return;
            }
        }

        // 第一条真实输入
        QueueLocalInput();
    }

    private bool RecordAndQueueInput(PlayerCommand command)
    {
        int frame = nextInputFrame;

        if (!rollback.RecordLocalInput(frame, command))
            return false;

        if (!commandBuffer.TryEnqueue(frame, command))
            return false;

        nextInputFrame++;
        return true;
    }

    /// <summary>
    /// 输入时钟驱动，限制输入领先量
    /// </summary>
    private void CollectInputTicks()
    {
        inputClock.Accumulate(Time.unscaledDeltaTime);

        int processed = 0;

        while (inputClock.IsTickDue && processed < MaxInputTicksPerUpdate)
        {
            inputClock.ConsumeTick();
            processed++;

            int maxInputFrame = lastServerFrame + inputDelay + MaxExtraLeadFrames;

            if (nextInputFrame > maxInputFrame)
            {
                // 等待服务器推进，避免输入缓存无限增长
                inputClock.Reset();
                break;
            }

            QueueLocalInput();

            if (!gameStarted)
                break;
        }

        if (gameStarted)
            FlushCommandBuffer();
    }

    private void QueueLocalInput()
    {
        PlayerCommand command = inputCollector.Collect(localPlayerId);

        if (!RecordAndQueueInput(command))
        {
            StopSimulation($"Input history failed at Frame {nextInputFrame}");
            return;
        }

        if (!AdvancePrediction())
            return;

        FlushCommandBuffer();
    }

    /// <summary>
    /// 预测时钟不依赖服务器返回上一帧
    /// </summary>
    private bool AdvancePrediction()
    {
        int oldFrame = world.CurrentFrame;
        int targetFrame = lastServerFrame + inputDelay + MaxExtraLeadFrames;

        if (!rollback.AdvancePredictionThrough(targetFrame, out string error))
        {
            StopSimulation(error);
            return false;
        }

        if (world.CurrentFrame != oldFrame)
        {
            CopyCurrentToPrevious();
            CaptureStates();
            renderElapsed = 0f;
        }

        return true;
    }

    /// <summary>
    /// 提交待发送输入
    /// 只有成功进入网络队列后才移除本地输入
    /// </summary>
    private void FlushCommandBuffer()
    {
        if (networkClient == null || !networkClient.IsOpen)
            return;

        int maxFrameToSend = lastServerFrame + inputDelay + MaxExtraLeadFrames;

        while (commandBuffer.TryPeek(out BufferedCommand next))
        {
            if (next.Frame > maxFrameToSend)
                break;

            if (!networkClient.EnqueueInput(next.Frame, next.Command))
            {
                StopSimulation($"Network enqueue failed at Frame {next.Frame}");
                return;
            }

            commandBuffer.TryDequeue(out BufferedCommand removed);
        }
    }

    /// <summary>
    /// 收到服务器权威帧
    /// </summary>
    private void OnFrameReceived(ServerMessage message)
    {
        if (!gameStarted || rollback == null)
            return;

        if (message.frame != lastServerFrame + 1)
        {
            StopSimulation($"Frame order error. Expected={lastServerFrame + 1}, Received={message.frame}");
            return;
        }

        if (message.inputs == null || message.inputs.Length != GameWorld.PlayerCount)
        {
            StopSimulation("Invalid frame inputs");
            return;
        }

        if (!ulong.TryParse(message.stateHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong serverHash))
        {
            StopSimulation("Invalid server Hash");
            return;
        }

        PlayerCommand[] commands = new PlayerCommand[GameWorld.PlayerCount];

        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            if (message.inputs[i] == null)
            {
                StopSimulation("Null input entry");
                return;
            }

            commands[i] = new PlayerCommand(message.inputs[i].x, message.inputs[i].y);
        }

        FramePacket packet = new FramePacket(message.frame, commands);

        int oldFrame = world.CurrentFrame;

        if (!rollback.ReceiveAuthoritativeFrame(packet, serverHash, out bool rolledBack, out string error))
        {
            StopSimulation(error);
            return;
        }

        lastServerFrame = message.frame;
        lastServerHash = message.stateHash;

        int targetFrame = lastServerFrame + inputDelay + MaxExtraLeadFrames;

        if (!rollback.AdvancePredictionThrough(targetFrame, out error))
        {
            StopSimulation(error);
            return;
        }

        if (rolledBack)
        {
            // 逻辑已经纠正到最新预测状态
            CaptureStates();
            CopyCurrentToPrevious();

            // 回滚期间不沿用旧逻辑帧的插值
            renderElapsed = TickInterval;

            // 保留旧视觉位置，逐渐衰减与正确坐标的误差
            visuals.OnRollback(currentStates);

            Debug.LogWarning($"[ROLLBACK] From={message.frame}, Replayed={rollback.LastReplayFrameCount}, Total={rollback.RollbackCount}");
        }
        else if (world.CurrentFrame != oldFrame)
        {
            CopyCurrentToPrevious();
            CaptureStates();
            renderElapsed = 0f;
        }

        if (message.frame % GameWorld.TickRate == 0)
        {
            Debug.Log($"[SYNC] Authority={message.frame}, Prediction={world.CurrentFrame}, ServerHash={serverHash:X16}");
        }

        FlushCommandBuffer();
    }

    private void CaptureStates()
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
            currentStates[i] = world.GetPlayerState(i);
    }

    private void CopyCurrentToPrevious()
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
            previousStates[i] = currentStates[i];
    }

    private void OnServerError(string message)
    {
        StopSimulation("Server error: " + message);
    }

    private void OnConnectionClosed()
    {
        gameStarted = false;
        Debug.LogWarning("[GAME] Connection closed");
    }

    private void StopSimulation(string reason)
    {
        gameStarted = false;
        Debug.LogError("[GAME] Simulation stopped: " + reason);
    }
}
```

## FrameSyncDemo/Tests/FrameSync.NetworkTests/FrameSync.NetworkTests.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <NoWarn>CS0067</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../../Shared/FrameSync.Shared/FrameSync.Shared.csproj" />
    <ProjectReference Include="../../Server/FrameSync.Server/FrameSync.Server.csproj" ReferenceOutputAssembly="false" />
    <Compile Include="../../../Assets/Scripts/Network/*.cs" Link="Client/%(Filename)%(Extension)" />
    <Compile Include="../../../Assets/WebSocket/WebSocket.cs" Link="Transport/WebSocket.cs" />
    <Compile Include="../../../Assets/WebSocket/WebSocketTypes.cs" Link="Transport/WebSocketTypes.cs" />
    <Compile Include="../../../Assets/WebSocket/IWebSocket.cs" Link="Transport/IWebSocket.cs" />
  </ItemGroup>
</Project>
```

## FrameSyncDemo/Tests/FrameSync.NetworkTests/UnityTestHost.cs

```csharp
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

// Minimal host for executing the actual NetworkClient and transport outside Unity.
// Unity serialization/rendering are validated separately against real Unity assemblies.
namespace UnityEngine
{
    public class MonoBehaviour { }
    public sealed class HeaderAttribute : Attribute { public HeaderAttribute(string text) { } }
    public sealed class SerializeField : Attribute { }
    public sealed class RangeAttribute : Attribute { public RangeAttribute(float min, float max) { } }
    public static class Application { public static int targetFrameRate; }
    public static class Time
    {
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        public static double realtimeSinceStartupAsDouble => clock.Elapsed.TotalSeconds;
    }
    public static class Random
    {
        private static readonly System.Random random = new System.Random(12345);
        public static int Range(int min, int max) => random.Next(min, max);
    }
    public static class Debug
    {
        public static void Log(object message) { }
        public static void LogWarning(object message) { }
        public static void LogError(object message) => Console.WriteLine(message);
    }
    public static class JsonUtility
    {
        private static readonly JsonSerializerOptions options = new JsonSerializerOptions { IncludeFields = true };
        public static string ToJson(object value) => JsonSerializer.Serialize(value, options);
        public static T FromJson<T>(string json) => JsonSerializer.Deserialize<T>(json, options);
    }
}

internal sealed class MainThreadContext : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object State)> queue = new();
    public override void Post(SendOrPostCallback callback, object state) => queue.Enqueue((callback, state));
    public void Pump()
    {
        while (queue.TryDequeue(out var work))
            work.Callback(work.State);
    }
}
```

## FrameSyncDemo/Tests/FrameSync.NetworkTests/Program.cs

```csharp
using System.Diagnostics;
using System.Reflection;
using System.Text;
using FrameSync.Shared;
using NativeWebSocket;
using UnityEngine;

internal static class Program
{
    private static readonly MainThreadContext context = new();
    private static readonly List<NetworkClient> clients = new();
    private static readonly List<string> errors = new();
    private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(context);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(Path.Combine(root, "FrameSyncDemo/Server/FrameSync.Server/bin/Debug/net10.0/FrameSync.Server.dll"));
        using var server = Process.Start(start);
        var output = new StringBuilder();
        server.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            Wait(() => { lock (output) return output.ToString().Contains("Application started"); }, 10000);
            TestClock();
            NetworkClient a = Connect();
            Wait(() => a.Clock.Initialized);
            Check(a.Clock.ServerFrame == -1, "No published frame before game start");
            NetworkClient b = Connect();
            Wait(() => a.Clock.Initialized && b.Clock.Initialized && a.ClientLocalClockFrame >= 0 && b.ClientLocalClockFrame >= 0);
            Console.WriteLine("[PASS] Two clients ping simultaneously, before and after start");
            double baseline = Measure(a, false, 0, 0, 0);
            double outgoing = Measure(a, true, 50, 0, 0);
            double incoming = Measure(a, true, 0, 50, 0);
            double rtt200 = Measure(a, true, 100, 100, 0);
            Check(outgoing >= baseline + 35 && incoming >= baseline + 35, "50 ms directional simulation measured");
            Check(rtt200 >= baseline + 180 && rtt200 < baseline + 270, "200 ms RTT measured");
            Measure(a, true, 50, 50, 20);
            Check(a.Clock.JitterMs > 1, "20 ms jitter changes measured RTT");
            Console.WriteLine("[PASS] Actual client delay queues: outgoing 50, incoming 50, RTT 200, jitter 20");
            TestFrames(a, b);
            TestInvalidPong(a);
            TestFifo(a);
            TestOverflowAndDisconnect(a, b);
            Console.WriteLine("===== ALL NETWORK TESTS PASSED =====");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            lock (output) File.WriteAllText(Path.Combine(root, "Docs/NetworkDiagnostics/network-test-server.log"), output.ToString());
            return 1;
        }
        finally
        {
            foreach (var client in clients)
                Call(client, "OnDestroy");
            if (!server.HasExited)
            {
                server.StandardInput.WriteLine("stop");
                if (!server.WaitForExit(5000))
                    server.Kill(true);
            }
        }
    }

    private static NetworkClient Connect()
    {
        var client = new NetworkClient();
        client.ServerErrorReceived += errors.Add;
        Set(client, "pingIntervalSeconds", 0.02f);
        clients.Add(client);
        Call(client, "Awake");
        _ = (Task)Call(client, "ConnectAsync");
        Wait(() => client.IsOpen && client.PlayerId >= 0);
        return client;
    }

    private static double Measure(NetworkClient client, bool enabled, int outgoing, int incoming, int jitter)
    {
        var settings = Get<NetworkSimulationSettings>(client, "simulation");
        settings.enabled = enabled;
        settings.outgoingDelayMs = outgoing;
        settings.incomingDelayMs = incoming;
        settings.jitterMs = jitter;
        int start = client.Clock.LastSequence;
        Wait(() => client.Clock.LastSequence >= start + 25, 5000);
        var samples = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            int sequence = client.Clock.LastSequence;
            Wait(() => client.Clock.LastSequence > sequence);
            samples.Add(client.Clock.RttMs);
        }
        double average = samples.Average();
        Console.WriteLine($"[RTT] enabled={enabled}, out={outgoing}, in={incoming}, jitter={jitter}: mean={average:F1} ms, EMA={client.Clock.SmoothedRttMs:F1}, jitterEMA={client.Clock.JitterMs:F1}");
        Check(errors.Count == 0, "No transport errors during frequent ping");
        return average;
    }

    private static void TestFrames(NetworkClient a, NetworkClient b)
    {
        var world = new GameWorld();
        var executor = new FrameExecutor(world);
        var frames = new List<ServerMessage>();
        a.FrameReceived += frames.Add;
        for (int i = 0; i < 16; i++)
        {
            Check(a.EnqueueInput(i, new PlayerCommand(1, 0)), "A input");
            Check(b.EnqueueInput(i, new PlayerCommand(0, 1)), "B input");
        }
        Wait(() => frames.Count == 16, 10000);
        for (int i = 0; i < frames.Count; i++)
        {
            Check(frames[i].frame == i, "Authority frame order");
            ulong hash = executor.Execute(new FramePacket(i, new[] { new PlayerCommand(1, 0), new PlayerCommand(0, 1) }));
            Check(frames[i].stateHash == hash.ToString("X16"), "Authority hash unchanged");
        }
        Wait(() => a.Clock.ServerFrame == 15 && b.Clock.ServerFrame == 15);
        Check(errors.Count == 0, "No concurrent send / input errors");
        Console.WriteLine("[PASS] High frequency ping + inputs: 16 ordered frames with exact baseline hashes, published frame=15");
    }

    private static void TestInvalidPong(NetworkClient client)
    {
        int accepted = client.Clock.LastSequence;
        Call(client, "ReceivePong", new ServerMessage { type = "pong", sequence = int.MaxValue, clientSendTimeMs = 0 });
        Check(client.Clock.LastSequence == accepted, "Unknown sequence ignored");
        Console.WriteLine("[PASS] Unknown pong sequence ignored");
    }

    private static void TestFifo(NetworkClient client)
    {
        var settings = Get<NetworkSimulationSettings>(client, "simulation");
        settings.incomingDelayMs = 200;
        settings.jitterMs = 200;
        var observed = new List<int>();
        Action<ServerMessage> handler = m => { if (m.frame >= 100) observed.Add(m.frame); };
        client.FrameReceived += handler;
        for (int i = 0; i < 40; i++)
            Call(client, "ScheduleIncomingMessage", Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ServerMessage { type = "frame", frame = 100 + i })));
        Wait(() => observed.Count == 40);
        Check(observed.SequenceEqual(Enumerable.Range(100, 40)), "Jitter preserves FIFO");
        client.FrameReceived -= handler;
        Console.WriteLine("[PASS] Jitter preserves FIFO for all 40 messages");
    }

    private static void TestOverflowAndDisconnect(NetworkClient a, NetworkClient b)
    {
        int closes = 0;
        a.ConnectionClosed += () => closes++;
        var settings = Get<NetworkSimulationSettings>(a, "simulation");
        settings.maxQueueSize = 1;
        settings.incomingDelayMs = 500;
        settings.jitterMs = 0;
        byte[] frame = Encoding.UTF8.GetBytes("{\"type\":\"frame\",\"frame\":999}");
        Call(a, "ScheduleIncomingMessage", frame);
        Call(a, "ScheduleIncomingMessage", frame);
        Check(!a.IsOpen && !a.Clock.Initialized && closes == 1 && errors.Any(e => e.Contains("overflow")), "Overflow terminates explicitly");
        int sequence = a.PingSequence;
        PumpFor(300);
        Check(a.PingSequence == sequence, "Disconnected client stops ping");
        Check(Get<System.Collections.IDictionary>(a, "pendingPings").Count == 0, "Pending ping state cleared");
        // Exercise outgoing overflow on the remaining connection before its close event is pumped.
        settings = Get<NetworkSimulationSettings>(b, "simulation");
        settings.enabled = true;
        settings.outgoingDelayMs = 500;
        settings.maxQueueSize = 1;
        Call(b, "EnqueueMessage", "{}");
        Call(b, "EnqueueMessage", "{}");
        Check(!b.IsOpen, "Outgoing overflow terminates explicitly");
        Console.WriteLine("[PASS] Incoming/outgoing overflow fail closed; disconnect clears metrics, stops ping and emits one close event");
    }

    private static void TestClock()
    {
        var clock = new ClockSynchronizer();
        var pong = new ServerMessage { sequence = 1, clientSendTimeMs = 1000, serverReceiveTimeMs = 6050, serverSendTimeMs = 6070, serverFrame = 10 };
        Check(clock.AddSample(pong, 1120), "Clock initializes with different epochs");
        Check(clock.RttMs == 100 && clock.ClockOffsetMs == 5000, "RTT excludes processing; NTP offset");
        Check(Math.Abs(clock.EstimateServerFrame(1120, 20) - 11) < 0.001, "Frame extrapolation");
        Check(!clock.AddSample(pong, 1120), "Duplicate pong rejected");
        pong.sequence = 2;
        Check(!clock.AddSample(pong, 1001), "Impossible RTT rejected");
        Check(clock.AddSample(pong, 1140), "Second sample accepted");
        Check(clock.SmoothedRttMs == 102.5 && clock.JitterMs == 2.5, "RTT and jitter EMA");
        clock.Reset();
        Check(!clock.Initialized && clock.ServerFrame == -1 && clock.LastSequence == 0, "Clock reset");
        Console.WriteLine("[PASS] Clock: separate epochs, processing subtraction, EMA, duplicate and invalid samples, reset");
    }

    private static void Pump()
    {
        context.Pump();
        foreach (var client in clients)
            Call(client, "Update");
        Thread.Sleep(1);
    }
    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) Pump();
    }
    private static void Wait(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new Exception("Timed out waiting for network condition");
            Pump();
        }
    }
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("[FAIL] " + message);
    }
}
```

## Docs/NetworkDiagnostics/UnityCompile/UnityCompile.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>netstandard2.1</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DefineConstants>UNITY_EDITOR</DefineConstants></PropertyGroup><ItemGroup><ProjectReference Include="../../../FrameSyncDemo/Shared/FrameSync.Shared/FrameSync.Shared.csproj"/><Compile Include="../../../Assets/Scripts/**/*.cs"/><Compile Include="../../../Assets/WebSocket/*.cs"/><Reference Include="UnityEngine"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.dll</HintPath></Reference><Reference Include="UnityEngine.AIModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AIModule.dll</HintPath></Reference><Reference Include="UnityEngine.ARModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ARModule.dll</HintPath></Reference><Reference Include="UnityEngine.AccessibilityModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AccessibilityModule.dll</HintPath></Reference><Reference Include="UnityEngine.AdaptivePerformanceModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AdaptivePerformanceModule.dll</HintPath></Reference><Reference Include="UnityEngine.AndroidJNIModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AndroidJNIModule.dll</HintPath></Reference><Reference Include="UnityEngine.AnimationModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AnimationModule.dll</HintPath></Reference><Reference Include="UnityEngine.AssetBundleModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AssetBundleModule.dll</HintPath></Reference><Reference Include="UnityEngine.AudioModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.AudioModule.dll</HintPath></Reference><Reference Include="UnityEngine.ClothModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ClothModule.dll</HintPath></Reference><Reference Include="UnityEngine.ClusterInputModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ClusterInputModule.dll</HintPath></Reference><Reference Include="UnityEngine.ClusterRendererModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ClusterRendererModule.dll</HintPath></Reference><Reference Include="UnityEngine.ContentLoadModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ContentLoadModule.dll</HintPath></Reference><Reference Include="UnityEngine.CoreModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.CoreModule.dll</HintPath></Reference><Reference Include="UnityEngine.CrashReportingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.CrashReportingModule.dll</HintPath></Reference><Reference Include="UnityEngine.DSPGraphModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.DSPGraphModule.dll</HintPath></Reference><Reference Include="UnityEngine.DirectorModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.DirectorModule.dll</HintPath></Reference><Reference Include="UnityEngine.GIModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.GIModule.dll</HintPath></Reference><Reference Include="UnityEngine.GameCenterModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.GameCenterModule.dll</HintPath></Reference><Reference Include="UnityEngine.GraphicsStateCollectionSerializerModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.GraphicsStateCollectionSerializerModule.dll</HintPath></Reference><Reference Include="UnityEngine.GridModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.GridModule.dll</HintPath></Reference><Reference Include="UnityEngine.HierarchyCoreModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.HierarchyCoreModule.dll</HintPath></Reference><Reference Include="UnityEngine.HotReloadModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.HotReloadModule.dll</HintPath></Reference><Reference Include="UnityEngine.IMGUIModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.IMGUIModule.dll</HintPath></Reference><Reference Include="UnityEngine.IdentifiersModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.IdentifiersModule.dll</HintPath></Reference><Reference Include="UnityEngine.ImageConversionModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ImageConversionModule.dll</HintPath></Reference><Reference Include="UnityEngine.InputModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.InputModule.dll</HintPath></Reference><Reference Include="UnityEngine.InputForUIModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.InputForUIModule.dll</HintPath></Reference><Reference Include="UnityEngine.InputLegacyModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.InputLegacyModule.dll</HintPath></Reference><Reference Include="UnityEngine.InsightsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.InsightsModule.dll</HintPath></Reference><Reference Include="UnityEngine.JSONSerializeModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.JSONSerializeModule.dll</HintPath></Reference><Reference Include="UnityEngine.LocalizationModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.LocalizationModule.dll</HintPath></Reference><Reference Include="UnityEngine.MarshallingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.MarshallingModule.dll</HintPath></Reference><Reference Include="UnityEngine.MathematicsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.MathematicsModule.dll</HintPath></Reference><Reference Include="UnityEngine.MultiplayerModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.MultiplayerModule.dll</HintPath></Reference><Reference Include="UnityEngine.ParticleSystemModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ParticleSystemModule.dll</HintPath></Reference><Reference Include="UnityEngine.PerformanceReportingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.PerformanceReportingModule.dll</HintPath></Reference><Reference Include="UnityEngine.PhysicsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.PhysicsModule.dll</HintPath></Reference><Reference Include="UnityEngine.Physics2DModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.Physics2DModule.dll</HintPath></Reference><Reference Include="UnityEngine.PhysicsBackendPhysXModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.PhysicsBackendPhysXModule.dll</HintPath></Reference><Reference Include="UnityEngine.PhysicsCore2DModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.PhysicsCore2DModule.dll</HintPath></Reference><Reference Include="UnityEngine.PropertiesModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.PropertiesModule.dll</HintPath></Reference><Reference Include="UnityEngine.RenderAs2DModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.RenderAs2DModule.dll</HintPath></Reference><Reference Include="UnityEngine.RuntimeInitializeOnLoadManagerInitializerModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.RuntimeInitializeOnLoadManagerInitializerModule.dll</HintPath></Reference><Reference Include="UnityEngine.ScreenCaptureModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ScreenCaptureModule.dll</HintPath></Reference><Reference Include="UnityEngine.ScriptingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ScriptingModule.dll</HintPath></Reference><Reference Include="UnityEngine.ShaderVariantAnalyticsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.ShaderVariantAnalyticsModule.dll</HintPath></Reference><Reference Include="UnityEngine.SharedInternalsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.SharedInternalsModule.dll</HintPath></Reference><Reference Include="UnityEngine.SpriteMaskModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.SpriteMaskModule.dll</HintPath></Reference><Reference Include="UnityEngine.SpriteShapeModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.SpriteShapeModule.dll</HintPath></Reference><Reference Include="UnityEngine.StreamingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.StreamingModule.dll</HintPath></Reference><Reference Include="UnityEngine.SubstanceModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.SubstanceModule.dll</HintPath></Reference><Reference Include="UnityEngine.SubsystemsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.SubsystemsModule.dll</HintPath></Reference><Reference Include="UnityEngine.TLSModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TLSModule.dll</HintPath></Reference><Reference Include="UnityEngine.TerrainModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TerrainModule.dll</HintPath></Reference><Reference Include="UnityEngine.TerrainPhysicsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TerrainPhysicsModule.dll</HintPath></Reference><Reference Include="UnityEngine.TextCoreFontEngineModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TextCoreFontEngineModule.dll</HintPath></Reference><Reference Include="UnityEngine.TextCoreTextEngineModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TextCoreTextEngineModule.dll</HintPath></Reference><Reference Include="UnityEngine.TextRenderingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TextRenderingModule.dll</HintPath></Reference><Reference Include="UnityEngine.TilemapModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.TilemapModule.dll</HintPath></Reference><Reference Include="UnityEngine.U2DRuntimeModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.U2DRuntimeModule.dll</HintPath></Reference><Reference Include="UnityEngine.UIModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UIModule.dll</HintPath></Reference><Reference Include="UnityEngine.UIElementsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UIElementsModule.dll</HintPath></Reference><Reference Include="UnityEngine.UmbraModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UmbraModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityAnalyticsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityAnalyticsModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityAnalyticsCommonModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityAnalyticsCommonModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityConnectModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityConnectModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityConsentModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityConsentModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityCurlModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityCurlModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityWebRequestModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityWebRequestModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityWebRequestAssetBundleModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityWebRequestAssetBundleModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityWebRequestAudioModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityWebRequestAudioModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityWebRequestTextureModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityWebRequestTextureModule.dll</HintPath></Reference><Reference Include="UnityEngine.UnityWebRequestWWWModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.UnityWebRequestWWWModule.dll</HintPath></Reference><Reference Include="UnityEngine.VFXModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.VFXModule.dll</HintPath></Reference><Reference Include="UnityEngine.VectorGraphicsModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.VectorGraphicsModule.dll</HintPath></Reference><Reference Include="UnityEngine.VehiclesModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.VehiclesModule.dll</HintPath></Reference><Reference Include="UnityEngine.VideoModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.VideoModule.dll</HintPath></Reference><Reference Include="UnityEngine.VirtualTexturingModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.VirtualTexturingModule.dll</HintPath></Reference><Reference Include="UnityEngine.WindModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.WindModule.dll</HintPath></Reference><Reference Include="UnityEngine.XRModule"><HintPath>D:\unityDocuments\Editor\6000.5.3f1\Editor\Data\Managed\UnityEngine\UnityEngine.XRModule.dll</HintPath></Reference><Reference Include="UnityEngine.UI"><HintPath>D:\unityFiles\OnlineGame\Library\ScriptAssemblies\UnityEngine.UI.dll</HintPath></Reference><Reference Include="Unity.InputSystem"><HintPath>D:\unityFiles\OnlineGame\Library\ScriptAssemblies\Unity.InputSystem.dll</HintPath></Reference><Reference Include="Unity.TextMeshPro"><HintPath>D:\unityFiles\OnlineGame\Library\ScriptAssemblies\Unity.TextMeshPro.dll</HintPath></Reference></ItemGroup></Project>
```
