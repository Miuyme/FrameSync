using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace FrameSync.Shared
{
    /// <summary>
    /// 服务器帧管理器
    /// 收集玩家输入，按照顺序生成完整帧
    /// </summary>
    public sealed class FrameManager
    {
        /// <summary>
        /// 尚未完成的帧
        /// </summary>
        private sealed class PendingFrame
        {
            // 玩家输入
            public readonly PlayerCommand[] Commands;

            // 玩家输入是否已经收到
            public readonly bool[] Received;

            // 已经收到输入的玩家数量
            public int ReceivedCount;

            public PendingFrame(int playerCount)
            {
                Commands = new PlayerCommand[playerCount];

                Received = new bool[playerCount];

                ReceivedCount = 0;
            }
        }

        private readonly int playerCount;

        // 允许提前提交的最大帧数
        private readonly int maxFutureFrames;

        // 所有尚未完成的帧
        private readonly SortedDictionary<int, PendingFrame> pendingFrames = new SortedDictionary<int, PendingFrame>();
            

        // 下一帧允许提交的帧编号
        public int NextFrameToCommit { get; private set; }

        // 当前缓存中有多少个未完成的帧
        public int PendingFrameCount => pendingFrames.Count;

        public FrameManager(
            int playerCount,
            int maxFutureFrames = 120)
        {
            if (playerCount <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(playerCount));

            if (maxFutureFrames < 0)
                throw new ArgumentOutOfRangeException(
                    nameof(maxFutureFrames));

            this.playerCount = playerCount;

            this.maxFutureFrames = maxFutureFrames;

            // 第一帧从0开始
            NextFrameToCommit = 0;
        }

        /// <summary>
        /// 提交某个玩家在指定帧的输入
        /// </summary>
        /// <param name="playerId">玩家ID</param>
        /// <param name="targetFrame">提交的帧ID</param>
        /// <param name="command">玩家指令</param>
        /// <returns></returns>
        public InputSubmitResult SubmitInput(
            int playerId,
            int targetFrame,
            PlayerCommand command)
        {
            // 检查玩家ID
            if (playerId < 0 || playerId >= playerCount)
            {
                return InputSubmitResult.InvalidPlayer;
            }

            // 检查帧编号
            if (targetFrame < 0)
            {
                return InputSubmitResult.InvalidFrame;
            }

            // 已经完成的帧不能重新提交
            if (targetFrame < NextFrameToCommit)
            {
                return InputSubmitResult.TooLate;
            }

            // 防止客户端提交过远的未来帧
            if (targetFrame - NextFrameToCommit > maxFutureFrames)
            {
                return InputSubmitResult.TooFarAhead;
            }

            // 查找对应帧
            if (!pendingFrames.TryGetValue(
                targetFrame,
                out PendingFrame pending))
            {
                pending = new PendingFrame(playerCount);

                pendingFrames.Add(targetFrame, pending);
            }

            // 检查是否重复提交
            if (pending.Received[playerId])
            {
                return InputSubmitResult.Duplicate;
            }

            // 保存输入
            pending.Commands[playerId] = command;

            pending.Received[playerId] = true;

            pending.ReceivedCount++;

            return InputSubmitResult.Accepted;
        }

        /// <summary>
        /// 尝试生成下一帧完整输入
        /// </summary>
        public bool TryBuildNextFrame(
            out FramePacket? packet)
        {
            packet = null;

            // 找到下一帧
            if (!pendingFrames.TryGetValue(
                NextFrameToCommit,
                out PendingFrame pending))
            {
                return false;
            }

            // 必须收集到所有玩家的输入
            if (pending.ReceivedCount != playerCount)
            {
                return false;
            }

            // 生成完整帧
            packet = new FramePacket(
                NextFrameToCommit,
                pending.Commands
            );

            // 删除已完成的缓存
            pendingFrames.Remove(NextFrameToCommit);

            // 下一帧编号递增
            NextFrameToCommit =
                checked(NextFrameToCommit + 1);

            return true;
        }
    }
}
