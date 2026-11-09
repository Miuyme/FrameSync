using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;

using NativeWebSocket;

using FrameSync.Shared;

using UnityEngine;

public class NetworkClient : MonoBehaviour
{
    [Header("Server")]
    [SerializeField]
    private string serverUrl =
        "ws://127.0.0.1:5000/ws";

    private WebSocket websocket;

    // 待发送消息
    private readonly Queue<DelayedNetworkMessage<string>> sendQueue = new Queue<DelayedNetworkMessage<string>>();


    private SynchronizationContext mainContext;
    private bool stopped;
    private int pingSequence;
    private double nextPingTime;
    private double localClockStart = -1;
    private readonly Dictionary<int, ClientPingMessage> pendingPings = new Dictionary<int, ClientPingMessage>();
    private readonly List<int> expiredPings = new List<int>();
    [Header("Diagnostics")]
    [SerializeField, Range(0.02f, 5f)] private float pingIntervalSeconds = 1f;
    public ClockSynchronizer Clock { get; } = new ClockSynchronizer();
    public int PingSequence => pingSequence;
    public int ClientLocalClockFrame => localClockStart < 0 ? -1 : (int)((Time.realtimeSinceStartupAsDouble - localClockStart) * GameWorld.TickRate) - 1;
    public double EstimatedServerFrame => Clock.EstimateServerFrame(Time.realtimeSinceStartupAsDouble * 1000, GameWorld.TickRate);
    public double FrameOffset => EstimatedServerFrame - ClientLocalClockFrame;

    private void Awake()
    {
        mainContext = SynchronizationContext.Current;
    }

    // All queues, Unity APIs and subscriber events belong to the Unity main thread.
    private void OnMainThread(Action action)
    {
        if (SynchronizationContext.Current == mainContext)
            action();
        else
            mainContext.Post(_ => action(), null);
    }

    // 是否正在发送
    private bool isSending;

    public int PlayerId { get; private set; } = -1;

    public bool IsOpen => !stopped && websocket != null && websocket.State == WebSocketState.Open;
    public int MaxFrameRate = 180;

    [Header("Network Simulation")]
    [SerializeField] private NetworkSimulationSettings simulation = new NetworkSimulationSettings();


    private readonly Queue<DelayedNetworkMessage<byte[]>> delayedIncoming = new Queue<DelayedNetworkMessage<byte[]>>();

    private double lastOutgoingDueTime;
    private double lastIncomingDueTime;


    public event Action<int> WelcomeReceived;

    // 修改：现在会传入服务器配置的InputDelay
    public event Action<int> GameStarted;

    public event Action<ServerMessage> FrameReceived;

    public event Action<string> ServerErrorReceived;

    public event Action ConnectionClosed;

    private async void Start()
    {
        Application.targetFrameRate = MaxFrameRate;
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        ResetDiagnostics();
        stopped = false;
        websocket = new WebSocket(serverUrl);

        websocket.OnOpen += () => OnMainThread(() =>
        {
            Debug.Log("[WS] Connected");
        });

        websocket.OnError += error => OnMainThread(() => FailConnection("[WS ERROR] " + error));
        websocket.OnClose += code => OnMainThread(() => StopConnection());
        websocket.OnMessage += bytes => OnMainThread(() =>
        {
            if (!stopped)
                ScheduleIncomingMessage(bytes);
        });

        try
        {
            await websocket.Connect();
        }
        catch (Exception ex)
        {
            FailConnection("[WS CONNECT ERROR] " + ex.Message);
        }
    }

    /// <summary>
    /// Unity每帧检查发送队列
    /// </summary>
    private void Update()
    {
        if (stopped)
            return;
        DispatchIncomingMessages();
        if (stopped || !IsOpen)
            return;
        UpdatePing();

        if (!isSending && IsOpen && sendQueue.Count > 0)
            _ = FlushSendQueueAsync();
    }

    /// <summary>
    /// 接收服务器消息
    /// </summary>
    private void HandleMessage(byte[] bytes)
    {
        string json = System.Text.Encoding.UTF8.GetString(bytes);


        ServerMessage message;

        try
        {
            message =
                JsonUtility.FromJson<ServerMessage>(json);
        }
        catch (Exception ex)
        {
            Debug.LogError(
                "[WS JSON ERROR] " + ex.Message
            );

            return;
        }

        if (message == null)
            return;

        switch (message.type)
        {
            case "welcome":

                PlayerId = message.playerId;

                Debug.Log(
                    $"[ROOM] PlayerId={PlayerId}"
                );

                WelcomeReceived?.Invoke(PlayerId);

                break;

            case "pong":
                ReceivePong(message);
                break;

            case "start":
                if (localClockStart < 0)
                    localClockStart = Time.realtimeSinceStartupAsDouble;

                Debug.Log(
                    $"[ROOM] Started. InputDelay={message.inputDelay}"
                );

                GameStarted?.Invoke(message.inputDelay);

                break;

            case "frame":

                FrameReceived?.Invoke(message);

                break;

            case "error":

                Debug.LogError("[SERVER ERROR] " + message.message);

                ServerErrorReceived?.Invoke(message.message);

                break;
        }
    }

    /// <summary>
    /// 将输入放入发送队列
    /// 不等待网络发送
    /// </summary>
    public bool EnqueueInput(int targetFrame, PlayerCommand command)
    {
        if (!IsOpen)
            return false;

        ClientInputMessage message = new ClientInputMessage
        {
            type = "input",
            targetFrame = targetFrame,
            x = command.X,
            y = command.Y
        };

        return EnqueueMessage(JsonUtility.ToJson(message));
    }

    /// <summary>
    /// 唯一的WebSocket发送循环
    /// </summary>
    private async Task FlushSendQueueAsync()
    {
        if (isSending)
            return;

        isSending = true;

        try
        {
            while (IsOpen && sendQueue.Count > 0)
            {
                DelayedNetworkMessage<string> item = sendQueue.Peek();

                if (item.DueTime > Time.realtimeSinceStartupAsDouble)
                    break;

                // Remove before await: OnClose/OnDestroy can clear the queue while sending.
                sendQueue.Dequeue();
                await websocket.SendText(item.Data);
            }
        }
        catch (Exception ex)
        {
            FailConnection("[WS SEND ERROR] " + ex.Message);
        }
        finally
        {
            isSending = false;
        }
    }

    private void OnDestroy()
    {
        StopConnection();
        websocket?.CancelConnection();
    }
    private void DispatchIncomingMessages()
    {
        double now = Time.realtimeSinceStartupAsDouble;

        while (!stopped && delayedIncoming.Count > 0)
        {
            DelayedNetworkMessage<byte[]> item = delayedIncoming.Peek();

            if (item.DueTime > now)
                break;

            delayedIncoming.Dequeue();

            HandleMessage(item.Data);
        }
    }
    private double CalculateDueTime(int baseDelayMs, ref double lastDueTime)
    {
        double now = Time.realtimeSinceStartupAsDouble;

        double jitter = simulation.enabled ? UnityEngine.Random.Range(-simulation.jitterMs, simulation.jitterMs) : 0;

        double delay = simulation.enabled ? Math.Max(0, baseDelayMs + jitter) / 1000.0 : 0;

        double dueTime = Math.Max(now + delay, lastDueTime);

        lastDueTime = dueTime;

        return dueTime;
    }
    private void ScheduleIncomingMessage(byte[] bytes)
    {
        if (delayedIncoming.Count >= Math.Max(1, simulation.maxQueueSize))
        {
            FailConnection("[NETSIM] Incoming queue overflow; connection stopped to preserve frame order");
            return;
        }

        byte[] copy = new byte[bytes.Length];

        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);

        double dueTime = CalculateDueTime(simulation.incomingDelayMs, ref lastIncomingDueTime);

        delayedIncoming.Enqueue(new DelayedNetworkMessage<byte[]>(copy, dueTime));
    }

    private bool EnqueueMessage(string json)
    {
        if (stopped || !IsOpen)
            return false;
        if (sendQueue.Count >= Math.Max(1, simulation.maxQueueSize))
        {
            FailConnection("[NETSIM] Outgoing queue overflow; connection stopped");
            return false;
        }

        double dueTime = CalculateDueTime(simulation.outgoingDelayMs, ref lastOutgoingDueTime);

        sendQueue.Enqueue(new DelayedNetworkMessage<string>(json, dueTime));

        return true;
    }

    private void UpdatePing()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        expiredPings.Clear();
        foreach (var entry in pendingPings)
        {
            if (now * 1000 - entry.Value.clientSendTimeMs > 10000)
                expiredPings.Add(entry.Key);
        }
        foreach (int sequence in expiredPings)
            pendingPings.Remove(sequence);
        if (now < nextPingTime || pendingPings.Count >= 128)
            return;
        nextPingTime = now + Math.Max(0.02, pingIntervalSeconds);
        if (pingSequence == int.MaxValue)
            return;
        var ping = new ClientPingMessage
        {
            type = "ping",
            sequence = ++pingSequence,
            clientFrame = ClientLocalClockFrame,
            clientSendTimeMs = (long)(now * 1000)
        };
        // t0 precedes simulated outgoing delay; t3 follows simulated incoming delay.
        pendingPings.Add(ping.sequence, ping);
        if (!EnqueueMessage(JsonUtility.ToJson(ping)))
            pendingPings.Remove(ping.sequence);
    }

    private void ReceivePong(ServerMessage message)
    {
        if (!pendingPings.TryGetValue(message.sequence, out ClientPingMessage ping))
            return;
        if (message.clientFrame != ping.clientFrame || message.clientSendTimeMs != ping.clientSendTimeMs)
            return;
        if (Clock.AddSample(message, Time.realtimeSinceStartupAsDouble * 1000))
            pendingPings.Remove(message.sequence);
    }

    private void ResetDiagnostics()
    {
        sendQueue.Clear();
        delayedIncoming.Clear();
        pendingPings.Clear();
        lastOutgoingDueTime = lastIncomingDueTime = 0;
        nextPingTime = 0;
        localClockStart = -1;
        PlayerId = -1;
        Clock.Reset();
        // Sequence remains increasing across connections, rejecting stale echoes.
    }

    private void StopConnection()
    {
        if (stopped)
            return;
        stopped = true;
        ResetDiagnostics();
        ConnectionClosed?.Invoke();
    }

    private void FailConnection(string reason)
    {
        if (stopped)
            return;
        Debug.LogError(reason);
        StopConnection();
        ServerErrorReceived?.Invoke(reason);
        // CancelConnection also stops a connection attempt or a blocked send.
        websocket?.CancelConnection();
    }
}
