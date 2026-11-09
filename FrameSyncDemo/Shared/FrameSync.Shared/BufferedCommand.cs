namespace FrameSync.Shared
{
    /// <summary>
    /// 客户端待发送的帧输入
    /// </summary>
    public struct BufferedCommand
    {
        /// <summary>
        /// 帧序号
        /// </summary>
        public int Frame;
        /// <summary>
        /// 该帧输入
        /// </summary>
        public PlayerCommand Command;

        public BufferedCommand(int frame, PlayerCommand command)
        {
            Frame = frame;
            Command = command;
        }
    }
}