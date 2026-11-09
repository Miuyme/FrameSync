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
