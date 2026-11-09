using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace FrameSync.Shared
{
    /// <summary>
    /// 一帧游戏输入
    /// </summary>
    public sealed class FramePacket
    {
        public int Frame { get; }
        public int PlayerCount => commands.Length;
        private readonly PlayerCommand[] commands;
        public FramePacket(int frame, PlayerCommand[] commands)
        {
            if (frame < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frame));
            }
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }
            Frame = frame;
            this.commands = new PlayerCommand[commands.Length];
            Array.Copy(commands, this.commands, commands.Length);
        }
        /// <summary>
        /// 获取玩家输入
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public PlayerCommand GetCommand(int playerId)
        {
            if (playerId < 0 || playerId >= commands.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(playerId));
            }

            return commands[playerId];
        }
        /// <summary>
        /// 获取输入数组副本
        /// </summary>
        public PlayerCommand[] CopyCommands()
        {
            PlayerCommand[] copy =
                new PlayerCommand[commands.Length];

            Array.Copy(commands, copy, commands.Length);

            return copy;
        }
    }
}