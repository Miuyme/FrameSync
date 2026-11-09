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
