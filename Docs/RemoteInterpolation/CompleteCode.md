# 远端插值修复完整源代码

以下为本次新增和修改文件的完整内容，DLL/XML 为 Shared 构建产物。

## Assets/Scripts/View/RemoteInterpolationBuffer.cs

```csharp
using System;
using FrameSync.Shared;

/// <summary>Presentation-only timeline. Uses confirmed states and never extrapolates.</summary>
public sealed class RemoteInterpolationBuffer
{
    private readonly PlayerState[] states;
    private readonly int tickRate;
    private readonly int delayFrames;
    private int oldestFrame;
    private int newestFrame;
    private double renderFrame;
    private bool hasState;
    private bool playing;

    public RemoteInterpolationBuffer(int tickRate, int delayFrames = 2, int capacity = 64)
    {
        if (tickRate < 1 || delayFrames < 1 || capacity <= delayFrames + 1)
            throw new ArgumentOutOfRangeException(nameof(delayFrames));
        this.tickRate = tickRate;
        this.delayFrames = delayFrames;
        states = new PlayerState[capacity];
    }

    private int Index(int frame) => (frame % states.Length + states.Length) % states.Length;

    public bool Add(int frame, PlayerState state)
    {
        if (hasState && frame != newestFrame + 1)
            return false;
        if (!hasState)
        {
            oldestFrame = frame;
            renderFrame = frame;
            hasState = true;
        }
        states[Index(frame)] = state;
        newestFrame = frame;
        oldestFrame = Math.Max(oldestFrame, newestFrame - states.Length + 1);
        // Extremely long main-thread stalls can exhaust history; stay inside retained data.
        renderFrame = Math.Max(renderFrame, oldestFrame);
        return true;
    }

    public bool TrySample(double elapsedSeconds, out double x, out double y)
    {
        x = y = 0;
        if (!hasState || double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
            return false;
        if (!playing && newestFrame - oldestFrame >= delayFrames)
            playing = true;
        if (playing)
        {
            // Slowly drain excess jitter backlog without rewinding the render clock.
            double speed = newestFrame - renderFrame > delayFrames + 1 ? 1.1 : 1;
            renderFrame = Math.Min(newestFrame, renderFrame + elapsedSeconds * tickRate * speed);
        }
        int fromFrame = (int)Math.Floor(renderFrame);
        int toFrame = Math.Min(fromFrame + 1, newestFrame);
        double alpha = renderFrame - fromFrame;
        PlayerState from = states[Index(fromFrame)];
        PlayerState to = states[Index(toFrame)];
        x = from.X + ((double)to.X - from.X) * alpha;
        y = from.Y + ((double)to.Y - from.Y) * alpha;
        return true;
    }
}
```

## Assets/Scripts/View/RemoteInterpolationBuffer.cs.meta

```yaml
fileFormatVersion: 2
guid: ee7ee18cba7d425996375fcff78cfb23
```

## Assets/Scripts/View/VisualCorrection.cs

```csharp

using FrameSync.Shared;
using UnityEngine;

/// <summary>
/// 只负责视觉平滑，不修改GameWorld
/// </summary>
public sealed class VisualCorrection
{
    private readonly Transform[] views;
    private readonly Vector3[] offsets;

    private readonly float halfLife;
    private readonly float snapDistance;

    public VisualCorrection(Transform player0, Transform player1, float halfLife = 0.08f, float snapDistance = 2f)
    {
        views = new Transform[] { player0, player1 };
        offsets = new Vector3[GameWorld.PlayerCount];

        this.halfLife = Mathf.Max(0.001f, halfLife);
        this.snapDistance = Mathf.Max(0f, snapDistance);
    }

    private static Vector3 ToPosition(PlayerState state, float z)
    {
        return new Vector3(
            state.X / (float)GameWorld.PositionScale,
            state.Y / (float)GameWorld.PositionScale,
            z
        );
    }

    /// <summary>
    /// 初始化视觉位置
    /// </summary>
    public void Snap(PlayerState[] states)
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            if (views[i] == null)
                continue;

            views[i].position = ToPosition(states[i], views[i].position.z);
            offsets[i] = Vector3.zero;
        }
    }

    /// <summary>
    /// 发生回滚后重新计算视觉修正偏移
    /// </summary>
    public void OnRollback(PlayerState[] correctedStates, int predictedPlayerId = -1)
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            if ((predictedPlayerId >= 0 && i != predictedPlayerId) || views[i] == null)
                continue;

            Vector3 corrected = ToPosition(correctedStates[i], views[i].position.z);

            // 当前实际渲染位置与正确逻辑位置的差值
            Vector3 error = views[i].position - corrected;

            if (error.magnitude > snapDistance)
            {
                // 误差过大时直接纠正
                offsets[i] = Vector3.zero;
                views[i].position = corrected;
            }
            else
            {
                offsets[i] = error;
            }
        }
    }

    /// <summary>
    /// 每个Unity渲染帧更新视觉位置
    /// </summary>
    public void Render(PlayerState[] previous, PlayerState[] current, float alpha, float deltaTime, int predictedPlayerId = -1)
    {
        alpha = Mathf.Clamp01(alpha);
        deltaTime = Mathf.Max(0f, deltaTime);

        // 每经过一个halfLife，修正偏移减少一半
        float decay = Mathf.Pow(0.5f, deltaTime / halfLife);

        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            Transform view = views[i];

            if ((predictedPlayerId >= 0 && i != predictedPlayerId) || view == null)
                continue;

            Vector3 from = ToPosition(previous[i], view.position.z);
            Vector3 to = ToPosition(current[i], view.position.z);

            Vector3 interpolated = Vector3.Lerp(from, to, alpha);

            offsets[i] *= decay;

            view.position = interpolated + offsets[i];
        }
    }
}
```

## Assets/Scripts/GameWorld/NetworkGameRunner.cs

```csharp

using System;
using System.Globalization;
using FrameSync.Shared;
using UnityEngine;

public sealed class NetworkGameRunner : MonoBehaviour
{
    [Header("Network")]
    [SerializeField] private NetworkClient networkClient;

    [Header("Player Views")]
    [SerializeField] private Transform player0View;
    [SerializeField] private Transform player1View;

    [Header("Debug")]
    [SerializeField] private RollbackDebugger debugger;

    [Header("Visual Correction")]
    [SerializeField] private float correctionHalfLife = 0.08f;
    [SerializeField] private float snapDistance = 2f;

    private GameWorld world;
    private RollbackSimulator rollback;
    private VisualCorrection visuals;
    private RemoteInterpolationBuffer remoteVisuals;
    private Transform remoteView;
    [Header("Remote Presentation")]
    [SerializeField, Range(1, 6)] private int remoteInterpolationDelayFrames = 2;

    private InputCollector inputCollector;
    private CommandBuffer commandBuffer;
    private FrameClock inputClock;

    private PlayerState[] previousStates;
    private PlayerState[] currentStates;

    private int localPlayerId = -1;
    private int nextInputFrame;
    private int lastServerFrame = -1;
    private int inputDelay;

    private bool gameStarted;
    private bool inputInitialized;

    private string lastServerHash = "";

    private float renderElapsed;

    private const int MaxExtraLeadFrames = 2;
    private const int MaxInputTicksPerUpdate = 4;
    private const int MaxRollbackFrames = 120;
    private const int MaxPredictionLead = 8;

    private const float TickInterval = 1f / GameWorld.TickRate;

    private void Awake()
    {
        Application.runInBackground = true;

        world = new GameWorld();

        inputCollector = new InputCollector();
        commandBuffer = new CommandBuffer(0, 256);
        inputClock = new FrameClock(GameWorld.TickRate);

        previousStates = new PlayerState[GameWorld.PlayerCount];
        currentStates = new PlayerState[GameWorld.PlayerCount];

        visuals = new VisualCorrection(player0View, player1View, correctionHalfLife, snapDistance);

        CaptureStates();
        CopyCurrentToPrevious();
        visuals.Snap(currentStates);
    }

    private void OnEnable()
    {
        if (networkClient == null)
        {
            Debug.LogError("NetworkClient is not assigned");
            return;
        }

        networkClient.WelcomeReceived += OnWelcome;
        networkClient.GameStarted += OnGameStarted;
        networkClient.FrameReceived += OnFrameReceived;
        networkClient.ServerErrorReceived += OnServerError;
        networkClient.ConnectionClosed += OnConnectionClosed;
    }

    private void OnDisable()
    {
        if (networkClient == null)
            return;

        networkClient.WelcomeReceived -= OnWelcome;
        networkClient.GameStarted -= OnGameStarted;
        networkClient.FrameReceived -= OnFrameReceived;
        networkClient.ServerErrorReceived -= OnServerError;
        networkClient.ConnectionClosed -= OnConnectionClosed;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        if (gameStarted)
        {
            CollectInputTicks();
            renderElapsed += dt;
        }

        float alpha = Mathf.Clamp01(renderElapsed / TickInterval);

        visuals.Render(previousStates, currentStates, alpha, dt, localPlayerId);
        if (remoteVisuals != null && remoteView != null && remoteVisuals.TrySample(dt, out double remoteX, out double remoteY))
            remoteView.position = new Vector3((float)(remoteX / GameWorld.PositionScale), (float)(remoteY / GameWorld.PositionScale), remoteView.position.z);

        if (debugger != null)
        {
            debugger.Refresh(rollback, commandBuffer.Count, nextInputFrame, lastServerHash);
            debugger.RefreshNetwork(networkClient);
        }
    }

    private void OnWelcome(int playerId)
    {
        if (rollback != null)
        {
            StopSimulation("Duplicate welcome");
            return;
        }

        localPlayerId = playerId;
        remoteView = localPlayerId == 0 ? player1View : player0View;
        remoteVisuals = new RemoteInterpolationBuffer(GameWorld.TickRate, Mathf.Clamp(remoteInterpolationDelayFrames, 1, 6));
        remoteVisuals.Add(-1, world.GetPlayerState(1 - localPlayerId));

        rollback = new RollbackSimulator(world, localPlayerId, MaxRollbackFrames, MaxPredictionLead);

        Debug.Log($"[GAME] PlayerId={localPlayerId}");
    }

    private void OnGameStarted(int serverInputDelay)
    {
        if (localPlayerId < 0 || rollback == null)
        {
            StopSimulation("PlayerId not assigned");
            return;
        }

        if (inputInitialized)
            return;

        if (serverInputDelay < 0 || serverInputDelay > MaxPredictionLead)
        {
            StopSimulation("Invalid InputDelay");
            return;
        }

        inputDelay = serverInputDelay;
        inputInitialized = true;
        gameStarted = true;

        inputClock.Reset();

        nextInputFrame = 0;
        lastServerFrame = -1;

        PlayerCommand neutral = new PlayerCommand(0, 0);

        // 预填充输入延迟帧
        for (int i = 0; i < inputDelay; i++)
        {
            if (!RecordAndQueueInput(neutral))
            {
                StopSimulation("Initial input setup failed");
                return;
            }
        }

        // 第一条真实输入
        QueueLocalInput();
    }

    private bool RecordAndQueueInput(PlayerCommand command)
    {
        int frame = nextInputFrame;

        if (!rollback.RecordLocalInput(frame, command))
            return false;

        if (!commandBuffer.TryEnqueue(frame, command))
            return false;

        nextInputFrame++;
        return true;
    }

    /// <summary>
    /// 输入时钟驱动，限制输入领先量
    /// </summary>
    private void CollectInputTicks()
    {
        inputClock.Accumulate(Time.unscaledDeltaTime);

        int processed = 0;

        while (inputClock.IsTickDue && processed < MaxInputTicksPerUpdate)
        {
            inputClock.ConsumeTick();
            processed++;

            int maxInputFrame = lastServerFrame + inputDelay + MaxExtraLeadFrames;

            if (nextInputFrame > maxInputFrame)
            {
                // 等待服务器推进，避免输入缓存无限增长
                inputClock.Reset();
                break;
            }

            QueueLocalInput();

            if (!gameStarted)
                break;
        }

        if (gameStarted)
            FlushCommandBuffer();
    }

    private void QueueLocalInput()
    {
        PlayerCommand command = inputCollector.Collect(localPlayerId);

        if (!RecordAndQueueInput(command))
        {
            StopSimulation($"Input history failed at Frame {nextInputFrame}");
            return;
        }

        if (!AdvancePrediction())
            return;

        FlushCommandBuffer();
    }

    /// <summary>
    /// 预测时钟不依赖服务器返回上一帧
    /// </summary>
    private bool AdvancePrediction()
    {
        int oldFrame = world.CurrentFrame;
        int targetFrame = lastServerFrame + inputDelay + MaxExtraLeadFrames;

        if (!rollback.AdvancePredictionThrough(targetFrame, out string error))
        {
            StopSimulation(error);
            return false;
        }

        if (world.CurrentFrame != oldFrame)
        {
            CopyCurrentToPrevious();
            CaptureStates();
            renderElapsed = 0f;
        }

        return true;
    }

    /// <summary>
    /// 提交待发送输入
    /// 只有成功进入网络队列后才移除本地输入
    /// </summary>
    private void FlushCommandBuffer()
    {
        if (networkClient == null || !networkClient.IsOpen)
            return;

        int maxFrameToSend = lastServerFrame + inputDelay + MaxExtraLeadFrames;

        while (commandBuffer.TryPeek(out BufferedCommand next))
        {
            if (next.Frame > maxFrameToSend)
                break;

            if (!networkClient.EnqueueInput(next.Frame, next.Command))
            {
                StopSimulation($"Network enqueue failed at Frame {next.Frame}");
                return;
            }

            commandBuffer.TryDequeue(out BufferedCommand removed);
        }
    }

    /// <summary>
    /// 收到服务器权威帧
    /// </summary>
    private void OnFrameReceived(ServerMessage message)
    {
        if (!gameStarted || rollback == null)
            return;

        if (message.frame != lastServerFrame + 1)
        {
            StopSimulation($"Frame order error. Expected={lastServerFrame + 1}, Received={message.frame}");
            return;
        }

        if (message.inputs == null || message.inputs.Length != GameWorld.PlayerCount)
        {
            StopSimulation("Invalid frame inputs");
            return;
        }

        if (!ulong.TryParse(message.stateHash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong serverHash))
        {
            StopSimulation("Invalid server Hash");
            return;
        }

        PlayerCommand[] commands = new PlayerCommand[GameWorld.PlayerCount];

        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            if (message.inputs[i] == null)
            {
                StopSimulation("Null input entry");
                return;
            }

            commands[i] = new PlayerCommand(message.inputs[i].x, message.inputs[i].y);
        }

        FramePacket packet = new FramePacket(message.frame, commands);

        int oldFrame = world.CurrentFrame;

        if (!rollback.ReceiveAuthoritativeFrame(packet, serverHash, out bool rolledBack, out string error))
        {
            StopSimulation(error);
            return;
        }

        lastServerFrame = message.frame;
        lastServerHash = message.stateHash;

        int targetFrame = lastServerFrame + inputDelay + MaxExtraLeadFrames;

        if (!rollback.AdvancePredictionThrough(targetFrame, out error))
        {
            StopSimulation(error);
            return;
        }

        // Read the corrected snapshot at the confirmed frame, not world.CurrentFrame.
        if (!rollback.TryGetAuthoritativePlayerState(message.frame, 1 - localPlayerId, out PlayerState remoteState) || !remoteVisuals.Add(message.frame, remoteState))
        {
            StopSimulation("Remote authoritative presentation history missing");
            return;
        }

        if (rolledBack)
        {
            // 逻辑已经纠正到最新预测状态
            CaptureStates();
            CopyCurrentToPrevious();

            // 回滚期间不沿用旧逻辑帧的插值
            renderElapsed = TickInterval;

            // 保留旧视觉位置，逐渐衰减与正确坐标的误差
            visuals.OnRollback(currentStates, localPlayerId);

            Debug.LogWarning($"[ROLLBACK] From={message.frame}, Replayed={rollback.LastReplayFrameCount}, Total={rollback.RollbackCount}");
        }
        else if (world.CurrentFrame != oldFrame)
        {
            CopyCurrentToPrevious();
            CaptureStates();
            renderElapsed = 0f;
        }

        if (message.frame % GameWorld.TickRate == 0)
        {
            Debug.Log($"[SYNC] Authority={message.frame}, Prediction={world.CurrentFrame}, ServerHash={serverHash:X16}");
        }

        FlushCommandBuffer();
    }

    private void CaptureStates()
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
            currentStates[i] = world.GetPlayerState(i);
    }

    private void CopyCurrentToPrevious()
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
            previousStates[i] = currentStates[i];
    }

    private void OnServerError(string message)
    {
        StopSimulation("Server error: " + message);
    }

    private void OnConnectionClosed()
    {
        gameStarted = false;
        Debug.LogWarning("[GAME] Connection closed");
    }

    private void StopSimulation(string reason)
    {
        gameStarted = false;
        Debug.LogError("[GAME] Simulation stopped: " + reason);
    }
}
```

## FrameSyncDemo/Shared/FrameSync.Shared/SnapshotRingBuffer.cs

```csharp
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
```

## FrameSyncDemo/Shared/FrameSync.Shared/RollbackSimulator.cs

```csharp

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
```

## FrameSyncDemo/Tests/FrameSync.SimTests/Program.cs

```csharp
﻿using System;
using FrameSync.Shared;

internal class Program
{
    private static void Main()
    {
        Console.WriteLine("===== FrameSync Tests =====");

        TestFrameClock();

        TestStrictLockstep();

        TestOutOfOrderFrames();

        TestLocalLockstep();

        TestCommandBuffer();

        TestSnapshot();
        TestRollback();
        TestRemoteStopStartPresentation();
        Console.WriteLine();
        Console.WriteLine("===== ALL TESTS PASSED =====");
    }

    /// <summary>
    /// 测试帧时钟
    /// </summary>
    private static void TestFrameClock()
    {
        FrameClock clock = new FrameClock(20);

        clock.Accumulate(0.02);

        Assert(
            !clock.IsTickDue,
            "Clock should not be due"
        );

        clock.Accumulate(0.04);

        Assert(
            clock.IsTickDue,
            "Clock should be due"
        );

        clock.ConsumeTick();

        Assert(
            !clock.IsTickDue,
            "Clock should have consumed one tick"
        );

        Console.WriteLine("[PASS] FrameClock");
    }

    /// <summary>
    /// 测试严格Lockstep等待机制
    /// </summary>
    private static void TestStrictLockstep()
    {
        FrameManager manager =
            new FrameManager(GameWorld.PlayerCount);

        // 玩家0提交第0帧
        InputSubmitResult result =
            manager.SubmitInput(
                0,
                0,
                new PlayerCommand(1, 0)
            );

        Assert(
            result == InputSubmitResult.Accepted,
            "Player 0 input should be accepted"
        );

        // 玩家0重复提交
        result = manager.SubmitInput(
            0,
            0,
            new PlayerCommand(1, 0)
        );

        Assert(
            result == InputSubmitResult.Duplicate,
            "Duplicate input should be rejected"
        );

        // 此时玩家1尚未提交
        FramePacket packet;

        bool success = manager.TryBuildNextFrame(
            out packet
        );

        Assert(
            !success,
            "Frame should wait for Player 1"
        );

        // 玩家1提交第0帧
        result = manager.SubmitInput(
            1,
            0,
            new PlayerCommand(0, 1)
        );

        Assert(
            result == InputSubmitResult.Accepted,
            "Player 1 input should be accepted"
        );

        // 此时应该能够生成完整帧
        success = manager.TryBuildNextFrame(
            out packet
        );

        Assert(
            success,
            "Frame should be completed"
        );

        Assert(
            packet.Frame == 0,
            "Packet frame should be 0"
        );

        Assert(
            packet.GetCommand(0).X == 1,
            "Player 0 command incorrect"
        );

        Assert(
            packet.GetCommand(1).Y == 1,
            "Player 1 command incorrect"
        );

        // 已经提交的帧不能重新提交
        result = manager.SubmitInput(
            0,
            0,
            new PlayerCommand(0, 0)
        );

        Assert(
            result == InputSubmitResult.TooLate,
            "Old frame should be rejected"
        );

        Console.WriteLine("[PASS] Strict Lockstep");
    }

    /// <summary>
    /// 测试乱序输入
    /// </summary>
    private static void TestOutOfOrderFrames()
    {
        FrameManager manager =
            new FrameManager(GameWorld.PlayerCount);

        FramePacket packet;

        // 先收到第1帧
        manager.SubmitInput(
            0, 1, new PlayerCommand(1, 0));

        manager.SubmitInput(
            1, 1, new PlayerCommand(0, 1));

        // 第1帧完整，但是第0帧还没有完成
        bool success = manager.TryBuildNextFrame(
            out packet
        );

        Assert(
            !success,
            "Frame 1 must wait for Frame 0"
        );

        // 补充第0帧
        manager.SubmitInput(
            0, 0, new PlayerCommand(0, 1));

        manager.SubmitInput(
            1, 0, new PlayerCommand(-1, 0));

        // 应该先生成第0帧
        success = manager.TryBuildNextFrame(
            out packet
        );

        Assert(success, "Frame 0 should be ready");

        Assert(
            packet.Frame == 0,
            "First committed frame should be 0"
        );

        // 再生成第1帧
        success = manager.TryBuildNextFrame(
            out packet
        );

        Assert(success, "Frame 1 should be ready");

        Assert(
            packet.Frame == 1,
            "Second committed frame should be 1"
        );

        Console.WriteLine("[PASS] Out Of Order Frames");
    }

    /// <summary>
    /// 模拟一个完整的本地Lockstep项目
    /// </summary>
    private static void TestLocalLockstep()
    {
        const int totalFrames = 1000;

        // 服务端帧管理器
        FrameManager frameManager =
            new FrameManager(GameWorld.PlayerCount);

        // 三个完全独立的游戏世界
        GameWorld serverWorld = new GameWorld();

        GameWorld clientAWorld = new GameWorld();

        GameWorld clientBWorld = new GameWorld();

        // 三个独立执行器
        FrameExecutor serverExecutor =
            new FrameExecutor(serverWorld);

        FrameExecutor clientAExecutor =
            new FrameExecutor(clientAWorld);

        FrameExecutor clientBExecutor =
            new FrameExecutor(clientBWorld);

        // 输入记录器
        CommandRecorder recorder =
            new CommandRecorder();

        for (int frame = 0; frame < totalFrames; frame++)
        {
            // 模拟客户端生成输入
            PlayerCommand[] commands =
                BuildCommands(frame);

            // 模拟网络到达顺序：
            // 玩家1先到，玩家0后到
            InputSubmitResult result1 =
                frameManager.SubmitInput(
                    1,
                    frame,
                    commands[1]
                );

            InputSubmitResult result0 =
                frameManager.SubmitInput(
                    0,
                    frame,
                    commands[0]
                );

            Assert(
                result0 == InputSubmitResult.Accepted,
                "Player 0 submit failed"
            );

            Assert(
                result1 == InputSubmitResult.Accepted,
                "Player 1 submit failed"
            );

            // 服务端生成完整帧
            FramePacket packet;

            bool success =
                frameManager.TryBuildNextFrame(
                    out packet
                );

            Assert(
                success,
                "Frame packet generation failed"
            );

            // 保存帧输入
            recorder.Record(packet);

            // 服务端执行
            ulong serverHash =
                serverExecutor.Execute(packet);

            // 客户端A执行
            ulong clientAHash =
                clientAExecutor.Execute(packet);

            // 客户端B执行
            ulong clientBHash =
                clientBExecutor.Execute(packet);

            // 比较Hash
            Assert(
                serverHash == clientAHash,
                "Server / Client A Hash mismatch"
            );

            Assert(
                serverHash == clientBHash,
                "Server / Client B Hash mismatch"
            );

            // 逐项比较游戏状态
            string reason;

            Assert(
                StateChecker.TryCompare(
                    serverWorld,
                    clientAWorld,
                    out reason
                ),
                "Client A: " + reason
            );

            Assert(
                StateChecker.TryCompare(
                    serverWorld,
                    clientBWorld,
                    out reason
                ),
                "Client B: " + reason
            );
        }

        Console.WriteLine(
            "[PASS] Local Lockstep: " +
            totalFrames + " Frames"
        );

        Console.WriteLine(
            "Server Hash: 0x" +
            serverWorld.CalculateHash().ToString("X16")
        );

        Console.WriteLine(
            "Client A Hash: 0x" +
            clientAWorld.CalculateHash().ToString("X16")
        );

        Console.WriteLine(
            "Client B Hash: 0x" +
            clientBWorld.CalculateHash().ToString("X16")
        );

        // 测试输入重放
        TestReplay(
            recorder,
            serverWorld
        );
    }

    /// <summary>
    /// 使用记录的输入重新模拟游戏
    /// </summary>
    private static void TestReplay(
        CommandRecorder recorder,
        GameWorld originalWorld)
    {
        GameWorld replayWorld = new GameWorld();

        FrameExecutor replayExecutor =
            new FrameExecutor(replayWorld);

        for (int i = 0; i < recorder.Count; i++)
        {
            FramePacket packet = recorder.GetFrame(i);

            replayExecutor.Execute(packet);
        }

        string reason;

        Assert(
            StateChecker.TryCompare(
                originalWorld,
                replayWorld,
                out reason
            ),
            "Replay failed: " + reason
        );

        Console.WriteLine(
            "[PASS] Replay: " +
            recorder.Count + " Frames"
        );
    }

    /// <summary>
    /// 根据帧编号生成测试输入
    /// </summary>
    private static PlayerCommand[] BuildCommands(int frame)
    {
        int phase0 = frame % 120;

        PlayerCommand p0;

        if (phase0 < 30)
            p0 = new PlayerCommand(1, 0);
        else if (phase0 < 60)
            p0 = new PlayerCommand(1, 1);
        else if (phase0 < 90)
            p0 = new PlayerCommand(0, -1);
        else
            p0 = new PlayerCommand(0, 0);

        int phase1 = frame % 100;

        PlayerCommand p1;

        if (phase1 < 25)
            p1 = new PlayerCommand(-1, 0);
        else if (phase1 < 50)
            p1 = new PlayerCommand(0, 1);
        else if (phase1 < 75)
            p1 = new PlayerCommand(-1, -1);
        else
            p1 = new PlayerCommand(0, 0);

        return new PlayerCommand[]
        {
            p0,
            p1
        };
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
        {
            throw new Exception(
                "[FAIL] " + message
            );
        }
    }
    private static void TestCommandBuffer()
    {
        CommandBuffer buffer = new CommandBuffer(
            firstFrame: 0,
            capacity: 10
        );

        bool result;

        // 添加Frame 0
        result = buffer.TryEnqueue(
            0,
            new PlayerCommand(1, 0)
        );

        Assert(result, "Frame 0 enqueue failed");

        // 跳过Frame 1，直接添加Frame 2
        result = buffer.TryEnqueue(
            2,
            new PlayerCommand(0, 1)
        );

        Assert(!result, "Frame 2 should be rejected");

        // 正确添加Frame 1
        result = buffer.TryEnqueue(
            1,
            new PlayerCommand(0, 1)
        );

        Assert(result, "Frame 1 enqueue failed");

        // 检查顺序
        BufferedCommand command;

        Assert(
            buffer.TryPeek(out command),
            "Peek failed"
        );

        Assert(
            command.Frame == 0,
            "First frame should be 0"
        );

        Assert(
            buffer.TryDequeue(out command),
            "Dequeue failed"
        );

        Assert(
            command.Frame == 0,
            "Dequeue frame incorrect"
        );

        Assert(
            buffer.TryDequeue(out command),
            "Second dequeue failed"
        );

        Assert(
            command.Frame == 1,
            "Second frame should be 1"
        );

        Assert(
            buffer.Count == 0,
            "Buffer should be empty"
        );

        Console.WriteLine("[PASS] CommandBuffer");
    }
    private static void TestSnapshot()
    {
        GameWorld world = new GameWorld();

        world.Step(new PlayerCommand[]
        {
            new PlayerCommand(1, 0),
            new PlayerCommand(0, 1)
        });

        GameWorldSnapshot snapshot = world.CaptureSnapshot();

        ulong originalHash = world.CalculateHash();

        world.Step(new PlayerCommand[]
        {
            new PlayerCommand(1, 0),
            new PlayerCommand(-1, 0)
        });

        Assert(world.CurrentFrame == 1, "Frame should be 1");

        world.RestoreSnapshot(snapshot);

        Assert(world.CurrentFrame == 0, "Frame should return to 0");

        Assert(world.CalculateHash() == originalHash, "Snapshot restore failed");

        Console.WriteLine("[PASS] GameWorld Snapshot");
    }

    private static void TestRemoteStopStartPresentation()
    {
        const int count = 80;
        var authoritative = new GameWorld();
        var executor = new FrameExecutor(authoritative);
        var packets = new FramePacket[count];
        var hashes = new ulong[count];
        var confirmed = new PlayerState[count];
        for (int f = 0; f < count; f++)
        {
            // Repeated forward/stop, then finish stopped.
            var remote = new PlayerCommand(f < count - 8 && f % 6 < 3 ? 1 : 0, 0);
            packets[f] = new FramePacket(f, new[] { remote, new PlayerCommand(0, 0) });
            hashes[f] = executor.Execute(packets[f]);
            confirmed[f] = authoritative.GetPlayerState(0);
        }
        var predicted = new GameWorld();
        var simulator = new RollbackSimulator(predicted, 1);
        var presentation = new RemoteInterpolationBuffer(GameWorld.TickRate, 2);
        Assert(presentation.Add(-1, predicted.GetPlayerState(0)), "Initial presentation state");
        int nextLocal = 0;
        double previousX = -1000;
        bool observedOverprediction = false;
        for (int tick = 0; tick < count + 4; tick++)
        {
            while (nextLocal < count && nextLocal <= simulator.LastAuthoritativeFrame + 8)
            {
                Assert(simulator.RecordLocalInput(nextLocal, new PlayerCommand(0, 0)), "Local input history");
                nextLocal++;
            }
            Assert(simulator.AdvancePredictionThrough(Math.Min(count - 1, simulator.LastAuthoritativeFrame + 5), out string error), error);
            int frame = tick - 4; // Four ticks of inbound authority latency.
            if (frame >= 0)
            {
                Assert(simulator.ReceiveAuthoritativeFrame(packets[frame], hashes[frame], out bool rolledBack, out error), error);
                Assert(simulator.TryGetAuthoritativePlayerState(frame, 0, out PlayerState state), "Confirmed snapshot available");
                Assert(state.X == confirmed[frame].X && state.Y == confirmed[frame].Y, "Presentation reads confirmed rather than predicted state");
                Assert(!simulator.TryGetAuthoritativePlayerState(frame + 1, 0, out _), "Speculative snapshot is not exposed");
                Assert(presentation.Add(frame, state), "Ordered presentation state");
                observedOverprediction |= predicted.GetPlayerState(0).X > state.X;
            }
            double latestX = frame < 0 ? -1000 : confirmed[frame].X;
            for (int render = 0; render < 6; render++)
            {
                Assert(presentation.TrySample(1.0 / 120, out double x, out double y), "Presentation sample");
                Assert(x >= previousX - 0.00001, "Forward/stop authority must never render a rollback retreat");
                Assert(x <= latestX + 0.00001 && y == 0, "Remote view must never extrapolate beyond confirmed movement");
                previousX = x;
            }
        }
        Assert(observedOverprediction && simulator.RollbackCount > 0, "Test must exercise actual overprediction and rollback");
        Assert(presentation.TrySample(10, out double stoppedX, out _), "Drain presentation to newest");
        Assert(stoppedX == confirmed[count - 1].X, "Presentation reaches final authoritative stop");
        Assert(presentation.TrySample(10, out double heldX, out _) && heldX == stoppedX, "Missing packets hold position without drift");
        Assert(!presentation.Add(count - 1, confirmed[count - 1]), "Duplicate presentation frame rejected");
        Assert(predicted.CalculateHash() == hashes[count - 1], "Presentation does not change simulation hash");
        Console.WriteLine("[PASS] Remote stop/start: 80 frames, delayed authority, real rollbacks, no visual retreat or overshoot, stall holds position");
    }

    private static void TestRollback()
    {
        const int frameCount = 8;

        GameWorld predictedWorld = new GameWorld();
        GameWorld authoritativeWorld = new GameWorld();

        RollbackSimulator simulator = new RollbackSimulator(predictedWorld, 0, 120);
        FrameExecutor authorityExecutor = new FrameExecutor(authoritativeWorld);

        PlayerCommand[] localCommands = new PlayerCommand[frameCount];
        PlayerCommand[] remoteCommands = new PlayerCommand[frameCount];

        FramePacket[] packets = new FramePacket[frameCount];
        ulong[] serverHashes = new ulong[frameCount];

        // 生成本地真实输入
        for (int i = 0; i < frameCount; i++)
        {
            localCommands[i] = i < 4 ? new PlayerCommand(1, 0) : new PlayerCommand(0, 1);

            Assert(simulator.RecordLocalInput(i, localCommands[i]), $"Record local input {i} failed");
        }

        // 先让客户端预测执行全部8帧
        Assert(simulator.AdvancePredictionThrough(frameCount - 1, out string error), error);

        Assert(predictedWorld.CurrentFrame == 7, "Prediction frame should be 7");

        // 生成服务器真实输入
        remoteCommands[0] = new PlayerCommand(0, 0);
        remoteCommands[1] = new PlayerCommand(0, 1);
        remoteCommands[2] = new PlayerCommand(0, 1);
        remoteCommands[3] = new PlayerCommand(-1, 0);
        remoteCommands[4] = new PlayerCommand(-1, 0);
        remoteCommands[5] = new PlayerCommand(0, -1);
        remoteCommands[6] = new PlayerCommand(0, -1);
        remoteCommands[7] = new PlayerCommand(0, 0);

        // 模拟服务器执行并保存权威帧
        for (int i = 0; i < frameCount; i++)
        {
            PlayerCommand[] commands = new PlayerCommand[]
            {
                localCommands[i],
                remoteCommands[i]
            };

            packets[i] = new FramePacket(i, commands);

            serverHashes[i] = authorityExecutor.Execute(packets[i]);
        }

        int rollbackCount = 0;

        // 模拟服务器依次广播权威帧
        for (int i = 0; i < frameCount; i++)
        {
            bool rolledBack;

            bool success = simulator.ReceiveAuthoritativeFrame(packets[i], serverHashes[i], out rolledBack, out error);

            Assert(success, $"Frame {i} reconciliation failed: {error}");

            if (rolledBack)
                rollbackCount++;
        }

        // 比较最终状态
        Assert(rollbackCount > 0, "Rollback should have occurred");

        Assert(StateChecker.TryCompare(authoritativeWorld, predictedWorld, out error), error);

        Console.WriteLine($"[PASS] Rollback: {rollbackCount} corrections");
        Console.WriteLine($"[PASS] Final Hash: {predictedWorld.CalculateHash():X16}");
    }
}
```

## FrameSyncDemo/Tests/FrameSync.SimTests/FrameSync.SimTests.csproj

```xml
﻿<Project Sdk="Microsoft.NET.Sdk">

  <ItemGroup>
    <Compile Include="../../../Assets/Scripts/View/RemoteInterpolationBuffer.cs" Link="RemoteInterpolationBuffer.cs" />
    <ProjectReference Include="..\..\Shared\FrameSync.Shared\FrameSync.Shared.csproj" />
  </ItemGroup>

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```
