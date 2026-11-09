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
