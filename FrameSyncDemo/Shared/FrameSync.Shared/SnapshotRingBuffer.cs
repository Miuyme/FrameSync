using System;

namespace FrameSync.Shared
{
    /// <summary>
    /// 固定容量的游戏状态快照环形缓冲区
    /// </summary>
    public sealed class SnapshotRingBuffer
    {
        private sealed class SnapshotSlot
        {
            public int Frame = int.MinValue;

            public readonly PlayerState[] States =
                new PlayerState[GameWorld.PlayerCount];
        }

        private readonly SnapshotSlot[] slots;

        public int Capacity => slots.Length;

        public SnapshotRingBuffer(int capacity)
        {
            if (capacity <= 1)
                throw new ArgumentOutOfRangeException(nameof(capacity));

            slots = new SnapshotSlot[capacity];

            for (int i = 0; i < capacity; i++)
                slots[i] = new SnapshotSlot();
        }

        private int GetIndex(int frame)
        {
            int index = frame % slots.Length;
            return index < 0 ? index + slots.Length : index;
        }

        /// <summary>
        /// 保存当前帧结束后的状态
        /// </summary>
        public void Save(GameWorld world)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            int frame = world.CurrentFrame;
            SnapshotSlot slot = slots[GetIndex(frame)];

            world.CopyStateTo(slot.States);
            slot.Frame = frame;
        }

        /// <summary>
        /// 判断指定帧的快照是否存在
        /// </summary>
        public bool Contains(int frame)
        {
            return slots[GetIndex(frame)].Frame == frame;
        }

        /// <summary>Read a state copy without restoring or modifying the simulation.</summary>
        public bool TryGetPlayerState(int frame, int playerId, out PlayerState state)
        {
            state = default(PlayerState);
            if (playerId < 0 || playerId >= GameWorld.PlayerCount)
                return false;
            SnapshotSlot slot = slots[GetIndex(frame)];
            if (slot.Frame != frame)
                return false;
            state = slot.States[playerId];
            return true;
        }

        /// <summary>
        /// 将世界恢复到指定帧
        /// </summary>
        public bool TryRestore(GameWorld world, int frame)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            SnapshotSlot slot = slots[GetIndex(frame)];

            if (slot.Frame != frame)
                return false;

            world.RestoreStateFrom(frame, slot.States);
            return true;
        }

        /// <summary>
        /// 回滚时使指定帧之后的旧快照失效
        /// </summary>
        public void InvalidateFrom(int frame)
        {
            foreach (SnapshotSlot slot in slots)
            {
                if (slot.Frame >= frame)
                    slot.Frame = int.MinValue;
            }
        }
    }
}
