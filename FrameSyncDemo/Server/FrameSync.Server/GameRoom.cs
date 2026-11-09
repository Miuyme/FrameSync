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
