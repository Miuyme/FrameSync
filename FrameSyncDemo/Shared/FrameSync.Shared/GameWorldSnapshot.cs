using System;

namespace FrameSync.Shared
{
    /// <summary>
    /// 游戏世界快照
    /// 保存某个逻辑帧结束后的完整状态
    /// </summary>
    public sealed class GameWorldSnapshot
    {
        public int Frame { get; }

        private readonly PlayerState[] states;

        public int PlayerCount => states.Length;

        public GameWorldSnapshot(int frame, PlayerState[] states)
        {
            if (states == null)
                throw new ArgumentNullException(nameof(states));

            Frame = frame;
            this.states = new PlayerState[states.Length];
            Array.Copy(states, this.states, states.Length);
        }

        public PlayerState GetPlayerState(int playerId)
        {
            if (playerId < 0 || playerId >= states.Length)
                throw new ArgumentOutOfRangeException(nameof(playerId));

            return states[playerId];
        }
    }
}