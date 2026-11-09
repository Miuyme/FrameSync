using System;
using System.Collections.Generic;

namespace FrameSync.Shared
{
    /// <summary>
    /// 帧输入记录器
    /// 保存已经提交的完整帧
    /// </summary>
    public sealed class CommandRecorder
    {
        private readonly List<FramePacket> frames =
            new List<FramePacket>();

        public int Count => frames.Count;

        /// <summary>
        /// 记录完整帧
        /// </summary>
        public void Record(FramePacket packet)
        {
            if (packet == null)
                throw new ArgumentNullException(nameof(packet));

            // 必须按照顺序记录
            if (packet.Frame != frames.Count)
            {
                throw new InvalidOperationException(
                    $"Invalid record frame. Expected: {frames.Count}, " +
                    $"Received: {packet.Frame}");
            }

            // 创建独立副本
            FramePacket copy = new FramePacket(
                packet.Frame,
                packet.CopyCommands()
            );

            frames.Add(copy);
        }

        /// <summary>
        /// 获取指定帧
        /// 返回独立副本
        /// </summary>
        public FramePacket GetFrame(int frame)
        {
            if (frame < 0 || frame >= frames.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frame));
            }

            FramePacket packet = frames[frame];

            return new FramePacket(
                packet.Frame,
                packet.CopyCommands()
            );
        }
    }
}