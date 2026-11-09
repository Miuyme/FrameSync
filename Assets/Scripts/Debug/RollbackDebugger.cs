
using FrameSync.Shared;
using UnityEngine;

/// <summary>
/// 帧同步运行时调试信息
/// </summary>
public sealed class RollbackDebugger : MonoBehaviour
{
    [Header("Frame State")]
    [SerializeField] private int serverFrame = -1;
    [SerializeField] private int predictedFrame = -1;
    [SerializeField] private int predictionLead;

    [Header("Rollback Statistics")]
    [SerializeField] private int rollbackCount;
    [SerializeField] private int lastRollbackFrame = -1;
    [SerializeField] private int lastReplayFrames;
    [SerializeField] private int totalReplayedFrames;

    [Header("Buffers")]
    [SerializeField] private int snapshotCapacity;
    [SerializeField] private int historyCapacity;
    [SerializeField] private int queuedInputs;
    [SerializeField] private int nextInputFrame;

    [Header("State Hash")]
    [SerializeField] private string currentPredictedHash;
    [SerializeField] private string lastServerHash;

    [Header("Network Diagnostics")]
    [SerializeField] private double rttMs;
    [SerializeField] private double smoothedRttMs;
    [SerializeField] private double jitterMs;
    [SerializeField] private int pingSequence;
    [SerializeField] private int pongSequence;
    [SerializeField] private int publishedServerFrame = -1;
    [SerializeField] private int clientLocalClockFrame = -1;
    [SerializeField] private double estimatedServerFrame = -1;
    [SerializeField] private double frameOffset;
    [SerializeField] private double clockOffsetMs;
    [SerializeField] private bool clockSynchronizerInitialized;

    public void RefreshNetwork(NetworkClient client)
    {
        if (client == null)
            return;
        rttMs = client.Clock.RttMs;
        smoothedRttMs = client.Clock.SmoothedRttMs;
        jitterMs = client.Clock.JitterMs;
        pingSequence = client.PingSequence;
        pongSequence = client.Clock.LastSequence;
        publishedServerFrame = client.Clock.ServerFrame;
        clientLocalClockFrame = client.ClientLocalClockFrame;
        estimatedServerFrame = client.EstimatedServerFrame;
        frameOffset = client.FrameOffset;
        clockOffsetMs = client.Clock.ClockOffsetMs;
        clockSynchronizerInitialized = client.Clock.Initialized;
    }

    [Header("Display")]
    [SerializeField] private bool showOverlay = true;

    public void Refresh(RollbackSimulator simulator, int queuedInputCount, int nextInput, string serverHash)
    {
        if (simulator == null)
            return;

        serverFrame = simulator.LastAuthoritativeFrame;
        predictedFrame = simulator.CurrentFrame;
        predictionLead = simulator.PredictionLead;

        rollbackCount = simulator.RollbackCount;
        lastRollbackFrame = simulator.LastRollbackFrame;
        lastReplayFrames = simulator.LastReplayFrameCount;
        totalReplayedFrames = simulator.TotalReplayedFrames;

        snapshotCapacity = simulator.SnapshotCapacity;
        historyCapacity = simulator.HistoryCapacity;

        queuedInputs = queuedInputCount;
        nextInputFrame = nextInput;

        currentPredictedHash = simulator.CurrentHash.ToString("X16");
        lastServerHash = serverHash;
    }

    private void OnGUI()
    {
        if (!showOverlay)
            return;

        GUILayout.BeginArea(new Rect(10, 10, 440, 430), GUI.skin.box);

        GUILayout.Label("FrameSync Debugger");
        GUILayout.Label($"Server Frame: {serverFrame}");
        GUILayout.Label($"Predicted Frame: {predictedFrame}");
        GUILayout.Label($"Prediction Lead: {predictionLead}");
        GUILayout.Label($"Rollback Count: {rollbackCount}");
        GUILayout.Label($"Last Rollback: {lastRollbackFrame}");
        GUILayout.Label($"Last Replay Frames: {lastReplayFrames}");
        GUILayout.Label($"Total Replayed: {totalReplayedFrames}");
        GUILayout.Label($"Queued Inputs: {queuedInputs}");
        GUILayout.Label($"Local Hash: {currentPredictedHash}");
        GUILayout.Label($"Server Hash: {lastServerHash}");

        GUILayout.Space(8);
        GUILayout.Label("Network Diagnostics (estimates only)");
        GUILayout.Label($"RTT: {rttMs:F1} ms | EMA: {smoothedRttMs:F1} ms | Jitter: {jitterMs:F1} ms");
        GUILayout.Label($"Ping Sequence: {pingSequence} | Pong: {pongSequence}");
        GUILayout.Label($"Published Server Frame: {publishedServerFrame}");
        GUILayout.Label($"Client Local Clock Frame: {clientLocalClockFrame}");
        GUILayout.Label($"Estimated Server Frame: {estimatedServerFrame:F2} | Offset: {frameOffset:F2}");
        GUILayout.Label($"Clock Offset: {clockOffsetMs:F1} ms | Initialized: {clockSynchronizerInitialized}");
        GUILayout.EndArea();
    }
}
