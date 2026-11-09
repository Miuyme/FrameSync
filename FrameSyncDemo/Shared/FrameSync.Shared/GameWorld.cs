using System;

namespace FrameSync.Shared
{
    public sealed class GameWorld
    {
        public const int PlayerCount = 2;
        /// <summary>
        /// 1000逻辑单位= 1 Unity单位
        /// </summary>
        public const int PositionScale = 1000;
        /// <summary>
        /// 游戏逻辑帧率
        /// </summary>
        public const int TickRate = 20;
        /// <summary>
        /// 每逻辑帧移动100个单位
        /// </summary>
        public const int MoveUnitsPerTick = 100;
        /// <summary>
        /// 对角线的整数近似系数
        /// </summary>
        private const int DiagonalScale = 707;
        /// <summary>
        /// 所有玩家的逻辑状态
        /// </summary>
        private readonly PlayerState[] players;
        public int CurrentFrame { get; private set; }

        public GameWorld()
        {
            players = new PlayerState[PlayerCount];
            players[0] = new PlayerState(-1000, 0);
            players[1] = new PlayerState(1000, 0);
            CurrentFrame = -1;
        }
        /// <summary>
        /// 获取玩家状态
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns>返回struct副本，外部不能直接修改内部状态</returns>
        public PlayerState GetPlayerState(int playerId)
        {
            if (playerId < 0 || playerId >= PlayerCount)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerId));
            }

            return players[playerId];
        }

        public void Step(PlayerCommand[] commands)
        {
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            if (commands.Length != PlayerCount)
            {
                throw new ArgumentException(
                    "Invalid player command count");
            }
            for (int i = 0; i < PlayerCount; i++)
            {
                PlayerState state = players[i];

                int inputX = NormalizeAxis(commands[i].X);
                int inputY = NormalizeAxis(commands[i].Y);

                int deltaX = inputX * MoveUnitsPerTick;
                int deltaY = inputY * MoveUnitsPerTick;

                if(inputX!=0&&inputY!=0)
                {
                    deltaX = deltaX * DiagonalScale / 1000;
                    deltaY = deltaY * DiagonalScale / 1000;
                }
                state.X = checked(state.X + deltaX);
                state.Y = checked(state.Y + deltaY);
                players[i] = state;
            }
            CurrentFrame = checked(CurrentFrame + 1);
        }

        private int NormalizeAxis(int value)
        {
            if (value > 0) return 1;
            if (value < 0) return -1;
            return 0;
        }
        /// <summary>
        /// 计算当前世界状态的hash，用于检查不同模拟实例是否一致
        /// </summary>
        /// <returns></returns>
        public ulong CalculateHash()
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                hash = AddInt(hash, CurrentFrame);
                for (int i = 0; i < PlayerCount; i++)
                {
                    hash = AddInt(hash, players[i].X);
                    hash = AddInt(hash, players[i].Y);
                }
                return hash;
            }
        }
        /// <summary>
        /// FNV-1a 64为Hash
        /// 按固定字节顺序处理整数
        /// </summary>
        /// <param name="hash"></param>
        /// <param name="value"></param>
        /// <returns></returns>
        private ulong AddInt(ulong hash, int value)
        {
            unchecked
            {
                uint data = (uint)value;
                for (int i = 0; i < 4; i++)
                {
                    hash ^= (byte)(data & 0xFF);
                    hash *= 1099511628211UL;
                    data >>= 8;
                }
                return hash;
            }
        }
        #region 状态保存与回滚
        
        /// <summary>
        /// 保存当前游戏世界状态
        /// </summary>
        public GameWorldSnapshot CaptureSnapshot()
        {
            return new GameWorldSnapshot(CurrentFrame, players);
        }

        /// <summary>
        /// 恢复游戏世界状态
        /// </summary>
        public void RestoreSnapshot(GameWorldSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            if (snapshot.PlayerCount != PlayerCount)
                throw new InvalidOperationException("Player count mismatch");

            for (int i = 0; i < PlayerCount; i++)
            {
                players[i] = snapshot.GetPlayerState(i);
            }

            CurrentFrame = snapshot.Frame;
        }
        #endregion
        /// <summary>
        /// 复制当前世界状态到外部预分配数组
        /// </summary>
        public void CopyStateTo(PlayerState[] destination)
        {
            if (destination == null || destination.Length != PlayerCount)
                throw new ArgumentException("Invalid destination");

            Array.Copy(players, destination, PlayerCount);
        }

        /// <summary>
        /// 从预分配数组恢复完整世界状态
        /// </summary>
        public void RestoreStateFrom(int frame, PlayerState[] source)
        {
            if (frame < -1)
                throw new ArgumentOutOfRangeException(nameof(frame));

            if (source == null || source.Length != PlayerCount)
                throw new ArgumentException("Invalid source");

            Array.Copy(source, players, PlayerCount);

            CurrentFrame = frame;
        }
    }
}