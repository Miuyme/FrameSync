
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
