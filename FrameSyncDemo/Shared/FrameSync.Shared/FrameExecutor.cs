using System;

namespace FrameSync.Shared
{
    /// <summary>
    /// 游戏逻辑帧执行器
    /// </summary>
    public sealed class FrameExecutor
    {
        private readonly GameWorld world;

        public FrameExecutor(GameWorld world)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            this.world = world;
        }

        /// <summary>
        /// 执行一帧
        /// </summary>
        public ulong Execute(FramePacket packet)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));

            // 检查玩家数量
            if (packet.PlayerCount != GameWorld.PlayerCount)
            {
                throw new InvalidOperationException(
                    "Invalid player count");
            }

            // 下一帧必须严格连续
            int expectedFrame = world.CurrentFrame + 1;

            if (packet.Frame != expectedFrame)
            {
                throw new InvalidOperationException(
                    $"Frame mismatch. Expected: {expectedFrame}, " +
                    $"Received: {packet.Frame}");
            }

            // 获取输入副本
            PlayerCommand[] commands = packet.CopyCommands();

            // 执行逻辑
            world.Step(commands);

            // 返回当前状态Hash
            return world.CalculateHash();
        }
    }
}