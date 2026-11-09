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
