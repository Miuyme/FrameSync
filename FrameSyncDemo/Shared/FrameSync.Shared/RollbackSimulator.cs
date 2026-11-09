
using System;

namespace FrameSync.Shared
{
    /// <summary>
    /// 固定容量的客户端预测与回滚系统
    /// 当前版本固定支持两名玩家
    /// </summary>
    public sealed class RollbackSimulator
    {
        private sealed class FrameRecord
        {
            public int Frame = int.MinValue;

            public bool HasLocalInput;
            public PlayerCommand LocalCommand;

            public bool HasAuthoritative;
            public readonly PlayerCommand[] Authoritative =
                new PlayerCommand[GameWorld.PlayerCount];

            public bool Simulated;
            public readonly PlayerCommand[] Executed =
                new PlayerCommand[GameWorld.PlayerCount];

            public ulong StateHash;
            public ulong ServerHash;

            public void Reset(int frame)
            {
                Frame = frame;
                HasLocalInput = false;
                HasAuthoritative = false;
                Simulated = false;
                LocalCommand = default(PlayerCommand);
                StateHash = 0;
                ServerHash = 0;
            }
        }

        private readonly GameWorld world;
        private readonly SnapshotRingBuffer snapshots;
        private readonly FrameRecord[] history;

        private readonly int localPlayerId;
        private readonly int remotePlayerId;
        private readonly int maxRollbackFrames;
        private readonly int maxPredictionLead;

        private int lastAuthoritativeFrame = -1;

        public int CurrentFrame => world.CurrentFrame;
        public int LastAuthoritativeFrame => lastAuthoritativeFrame;
        public int PredictionLead => CurrentFrame - lastAuthoritativeFrame;

        public int RollbackCount { get; private set; }
        public int TotalReplayedFrames { get; private set; }
        public int LastRollbackFrame { get; private set; } = -1;
        public int LastReplayFrameCount { get; private set; }

        public int SnapshotCapacity => snapshots.Capacity;
        public int HistoryCapacity => history.Length;
        public ulong CurrentHash => world.CalculateHash();

        /// <summary>Only expose reconciled snapshots; never return speculative state for remote rendering.</summary>
        public bool TryGetAuthoritativePlayerState(int frame, int playerId, out PlayerState state)
        {
            state = default(PlayerState);
            return frame >= -1 && frame <= lastAuthoritativeFrame && snapshots.TryGetPlayerState(frame, playerId, out state);
        }

        public RollbackSimulator(GameWorld world, int localPlayerId, int maxRollbackFrames = 120, int maxPredictionLead = 8)
        {
            if (world == null)
                throw new ArgumentNullException(nameof(world));

            if (GameWorld.PlayerCount != 2)
                throw new InvalidOperationException("This simulator currently supports exactly 2 players");

            if (localPlayerId < 0 || localPlayerId >= GameWorld.PlayerCount)
                throw new ArgumentOutOfRangeException(nameof(localPlayerId));

            if (maxRollbackFrames < 2)
                throw new ArgumentOutOfRangeException(nameof(maxRollbackFrames));

            if (maxPredictionLead < 1 || maxPredictionLead >= maxRollbackFrames)
                throw new ArgumentOutOfRangeException(nameof(maxPredictionLead));

            if (world.CurrentFrame != -1)
                throw new InvalidOperationException("GameWorld must start at Frame -1");

            this.world = world;
            this.localPlayerId = localPlayerId;
            remotePlayerId = 1 - localPlayerId;
            this.maxRollbackFrames = maxRollbackFrames;
            this.maxPredictionLead = maxPredictionLead;

            int capacity = checked(maxRollbackFrames + maxPredictionLead + 4);

            snapshots = new SnapshotRingBuffer(capacity);
            history = new FrameRecord[capacity];

            for (int i = 0; i < history.Length; i++)
                history[i] = new FrameRecord();

            snapshots.Save(world);
        }

        private int GetIndex(int frame)
        {
            int index = frame % history.Length;
            return index < 0 ? index + history.Length : index;
        }

        private bool TryGetRecord(int frame, out FrameRecord record)
        {
            record = history[GetIndex(frame)];
            return record.Frame == frame;
        }

        private FrameRecord GetOrCreateRecord(int frame)
        {
            FrameRecord record = history[GetIndex(frame)];

            if (record.Frame == frame)
                return record;

            if (record.Frame != int.MinValue && frame - (long)record.Frame < history.Length)
                throw new InvalidOperationException($"Active history collision: {record.Frame} / {frame}");

            record.Reset(frame);
            return record;
        }

        /// <summary>
        /// 记录还没有执行过的本地输入
        /// </summary>
        public bool RecordLocalInput(int frame, PlayerCommand command)
        {
            if (frame < 0 || frame <= world.CurrentFrame)
                return false;

            if ((long)frame > (long)lastAuthoritativeFrame + maxPredictionLead)
                return false;

            FrameRecord record = GetOrCreateRecord(frame);

            if (record.HasLocalInput)
                return false;

            record.LocalCommand = command;
            record.HasLocalInput = true;

            return true;
        }

        /// <summary>
        /// 预测执行到指定帧
        /// 超过预测范围时自动停在允许的最大帧
        /// </summary>
        public bool AdvancePredictionThrough(int targetFrame, out string error)
        {
            error = null;

            int maximumFrame = lastAuthoritativeFrame + maxPredictionLead;
            int finalFrame = Math.Min(targetFrame, maximumFrame);

            while (world.CurrentFrame < finalFrame)
            {
                int frame = world.CurrentFrame + 1;
                FrameRecord record = GetOrCreateRecord(frame);

                if (!record.HasAuthoritative && !record.HasLocalInput)
                    break;

                if (!ExecuteFrame(frame, record, out error))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 收到服务端权威帧并校正预测
        /// </summary>
        public bool ReceiveAuthoritativeFrame(FramePacket packet, ulong serverHash, out bool rolledBack, out string error)
        {
            rolledBack = false;
            error = null;

            if (packet == null || packet.PlayerCount != GameWorld.PlayerCount)
            {
                error = "Invalid authoritative packet";
                return false;
            }

            int frame = packet.Frame;

            // 当前版本要求权威帧连续到达
            if (frame != lastAuthoritativeFrame + 1)
            {
                error = $"Authoritative frame order error: {frame}";
                return false;
            }

            if (frame < (long)world.CurrentFrame - maxRollbackFrames + 1)
            {
                error = $"Frame {frame} is outside rollback window";
                return false;
            }

            if ((long)frame > (long)world.CurrentFrame + maxPredictionLead)
            {
                error = $"Authoritative frame {frame} is too far ahead";
                return false;
            }

            FrameRecord record = GetOrCreateRecord(frame);

            for (int i = 0; i < GameWorld.PlayerCount; i++)
                record.Authoritative[i] = packet.GetCommand(i);

            record.HasAuthoritative = true;
            record.ServerHash = serverHash;

            // 该帧尚未执行：留待后续预测执行器处理
            if (!record.Simulated)
            {
                lastAuthoritativeFrame = frame;
                return true;
            }

            // 该帧已经执行：检查当时的预测输入
            if (!CommandsEqual(record.Executed, record.Authoritative))
            {
                if (!RollbackFrom(frame, out error))
                    return false;

                rolledBack = true;
            }
            else if (record.StateHash != serverHash)
            {
                error = $"State Hash mismatch at Frame {frame}";
                return false;
            }

            lastAuthoritativeFrame = frame;
            return true;
        }

        /// <summary>
        /// 执行一帧
        /// 不为输入数组分配新内存
        /// </summary>
        private bool ExecuteFrame(int frame, FrameRecord record, out string error)
        {
            error = null;

            if (record.HasAuthoritative)
            {
                Array.Copy(record.Authoritative, record.Executed, GameWorld.PlayerCount);
            }
            else
            {
                record.Executed[localPlayerId] = record.LocalCommand;

                if (frame == 0)
                {
                    record.Executed[remotePlayerId] = new PlayerCommand(0, 0);
                }
                else
                {
                    if (!TryGetRecord(frame - 1, out FrameRecord previous) || !previous.Simulated)
                    {
                        error = $"Previous input history {frame - 1} missing";
                        return false;
                    }

                    record.Executed[remotePlayerId] = previous.Executed[remotePlayerId];
                }
            }

            world.Step(record.Executed);

            record.Simulated = true;
            record.StateHash = world.CalculateHash();

            snapshots.Save(world);

            if (record.HasAuthoritative && record.StateHash != record.ServerHash)
            {
                error = $"Hash mismatch at Frame {frame}. Local={record.StateHash:X16}, Server={record.ServerHash:X16}";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 恢复到错误帧之前，重新执行到原预测帧
        /// </summary>
        private bool RollbackFrom(int frame, out string error)
        {
            error = null;

            int previousCurrentFrame = world.CurrentFrame;
            int baseFrame = frame - 1;

            if (!snapshots.TryRestore(world, baseFrame))
            {
                error = $"Snapshot {baseFrame} has expired; full resync required";
                return false;
            }

            snapshots.InvalidateFrom(frame);

            int replayed = 0;

            for (int f = frame; f <= previousCurrentFrame; f++)
            {
                if (!TryGetRecord(f, out FrameRecord record))
                {
                    error = $"Frame history {f} missing";
                    return false;
                }

                if (!record.HasAuthoritative && !record.HasLocalInput)
                {
                    error = $"Input history {f} missing";
                    return false;
                }

                if (!ExecuteFrame(f, record, out error))
                    return false;

                replayed++;
            }

            RollbackCount++;
            TotalReplayedFrames += replayed;
            LastRollbackFrame = frame;
            LastReplayFrameCount = replayed;

            return true;
        }

        private static bool CommandsEqual(PlayerCommand[] a, PlayerCommand[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].X != b[i].X || a[i].Y != b[i].Y)
                    return false;
            }

            return true;
        }
    }
}
