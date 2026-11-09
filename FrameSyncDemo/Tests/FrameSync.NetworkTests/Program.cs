using System.Diagnostics;
using System.Reflection;
using System.Text;
using FrameSync.Shared;
using NativeWebSocket;
using UnityEngine;

internal static class Program
{
    private static readonly MainThreadContext context = new();
    private static readonly List<NetworkClient> clients = new();
    private static readonly List<string> errors = new();
    private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(context);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../.."));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root
        };
        start.ArgumentList.Add(Path.Combine(root, "FrameSyncDemo/Server/FrameSync.Server/bin/Debug/net10.0/FrameSync.Server.dll"));
        using var server = Process.Start(start);
        var output = new StringBuilder();
        server.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        server.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        server.BeginOutputReadLine();
        server.BeginErrorReadLine();
        try
        {
            Wait(() => { lock (output) return output.ToString().Contains("Application started"); }, 10000);
            TestClock();
            NetworkClient a = Connect();
            Wait(() => a.Clock.Initialized);
            Check(a.Clock.ServerFrame == -1, "No published frame before game start");
            NetworkClient b = Connect();
            Wait(() => a.Clock.Initialized && b.Clock.Initialized && a.ClientLocalClockFrame >= 0 && b.ClientLocalClockFrame >= 0);
            Console.WriteLine("[PASS] Two clients ping simultaneously, before and after start");
            double baseline = Measure(a, false, 0, 0, 0);
            double outgoing = Measure(a, true, 50, 0, 0);
            double incoming = Measure(a, true, 0, 50, 0);
            double rtt200 = Measure(a, true, 100, 100, 0);
            Check(outgoing >= baseline + 35 && incoming >= baseline + 35, "50 ms directional simulation measured");
            Check(rtt200 >= baseline + 180 && rtt200 < baseline + 270, "200 ms RTT measured");
            Measure(a, true, 50, 50, 20);
            Check(a.Clock.JitterMs > 1, "20 ms jitter changes measured RTT");
            Console.WriteLine("[PASS] Actual client delay queues: outgoing 50, incoming 50, RTT 200, jitter 20");
            TestFrames(a, b);
            TestInvalidPong(a);
            TestFifo(a);
            TestOverflowAndDisconnect(a, b);
            Console.WriteLine("===== ALL NETWORK TESTS PASSED =====");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            lock (output) File.WriteAllText(Path.Combine(root, "Docs/NetworkDiagnostics/network-test-server.log"), output.ToString());
            return 1;
        }
        finally
        {
            foreach (var client in clients)
                Call(client, "OnDestroy");
            if (!server.HasExited)
            {
                server.StandardInput.WriteLine("stop");
                if (!server.WaitForExit(5000))
                    server.Kill(true);
            }
        }
    }

    private static NetworkClient Connect()
    {
        var client = new NetworkClient();
        client.ServerErrorReceived += errors.Add;
        Set(client, "pingIntervalSeconds", 0.02f);
        clients.Add(client);
        Call(client, "Awake");
        _ = (Task)Call(client, "ConnectAsync");
        Wait(() => client.IsOpen && client.PlayerId >= 0);
        return client;
    }

    private static double Measure(NetworkClient client, bool enabled, int outgoing, int incoming, int jitter)
    {
        var settings = Get<NetworkSimulationSettings>(client, "simulation");
        settings.enabled = enabled;
        settings.outgoingDelayMs = outgoing;
        settings.incomingDelayMs = incoming;
        settings.jitterMs = jitter;
        int start = client.Clock.LastSequence;
        Wait(() => client.Clock.LastSequence >= start + 25, 5000);
        var samples = new List<double>();
        for (int i = 0; i < 12; i++)
        {
            int sequence = client.Clock.LastSequence;
            Wait(() => client.Clock.LastSequence > sequence);
            samples.Add(client.Clock.RttMs);
        }
        double average = samples.Average();
        Console.WriteLine($"[RTT] enabled={enabled}, out={outgoing}, in={incoming}, jitter={jitter}: mean={average:F1} ms, EMA={client.Clock.SmoothedRttMs:F1}, jitterEMA={client.Clock.JitterMs:F1}");
        Check(errors.Count == 0, "No transport errors during frequent ping");
        return average;
    }

    private static void TestFrames(NetworkClient a, NetworkClient b)
    {
        var world = new GameWorld();
        var executor = new FrameExecutor(world);
        var frames = new List<ServerMessage>();
        a.FrameReceived += frames.Add;
        for (int i = 0; i < 16; i++)
        {
            Check(a.EnqueueInput(i, new PlayerCommand(1, 0)), "A input");
            Check(b.EnqueueInput(i, new PlayerCommand(0, 1)), "B input");
        }
        Wait(() => frames.Count == 16, 10000);
        for (int i = 0; i < frames.Count; i++)
        {
            Check(frames[i].frame == i, "Authority frame order");
            ulong hash = executor.Execute(new FramePacket(i, new[] { new PlayerCommand(1, 0), new PlayerCommand(0, 1) }));
            Check(frames[i].stateHash == hash.ToString("X16"), "Authority hash unchanged");
        }
        Wait(() => a.Clock.ServerFrame == 15 && b.Clock.ServerFrame == 15);
        Check(errors.Count == 0, "No concurrent send / input errors");
        Console.WriteLine("[PASS] High frequency ping + inputs: 16 ordered frames with exact baseline hashes, published frame=15");
    }

    private static void TestInvalidPong(NetworkClient client)
    {
        int accepted = client.Clock.LastSequence;
        Call(client, "ReceivePong", new ServerMessage { type = "pong", sequence = int.MaxValue, clientSendTimeMs = 0 });
        Check(client.Clock.LastSequence == accepted, "Unknown sequence ignored");
        Console.WriteLine("[PASS] Unknown pong sequence ignored");
    }

    private static void TestFifo(NetworkClient client)
    {
        var settings = Get<NetworkSimulationSettings>(client, "simulation");
        settings.incomingDelayMs = 200;
        settings.jitterMs = 200;
        var observed = new List<int>();
        Action<ServerMessage> handler = m => { if (m.frame >= 100) observed.Add(m.frame); };
        client.FrameReceived += handler;
        for (int i = 0; i < 40; i++)
            Call(client, "ScheduleIncomingMessage", Encoding.UTF8.GetBytes(JsonUtility.ToJson(new ServerMessage { type = "frame", frame = 100 + i })));
        Wait(() => observed.Count == 40);
        Check(observed.SequenceEqual(Enumerable.Range(100, 40)), "Jitter preserves FIFO");
        client.FrameReceived -= handler;
        Console.WriteLine("[PASS] Jitter preserves FIFO for all 40 messages");
    }

    private static void TestOverflowAndDisconnect(NetworkClient a, NetworkClient b)
    {
        int closes = 0;
        a.ConnectionClosed += () => closes++;
        var settings = Get<NetworkSimulationSettings>(a, "simulation");
        settings.maxQueueSize = 1;
        settings.incomingDelayMs = 500;
        settings.jitterMs = 0;
        byte[] frame = Encoding.UTF8.GetBytes("{\"type\":\"frame\",\"frame\":999}");
        Call(a, "ScheduleIncomingMessage", frame);
        Call(a, "ScheduleIncomingMessage", frame);
        Check(!a.IsOpen && !a.Clock.Initialized && closes == 1 && errors.Any(e => e.Contains("overflow")), "Overflow terminates explicitly");
        int sequence = a.PingSequence;
        PumpFor(300);
        Check(a.PingSequence == sequence, "Disconnected client stops ping");
        Check(Get<System.Collections.IDictionary>(a, "pendingPings").Count == 0, "Pending ping state cleared");
        // Exercise outgoing overflow on the remaining connection before its close event is pumped.
        settings = Get<NetworkSimulationSettings>(b, "simulation");
        settings.enabled = true;
        settings.outgoingDelayMs = 500;
        settings.maxQueueSize = 1;
        Call(b, "EnqueueMessage", "{}");
        Call(b, "EnqueueMessage", "{}");
        Check(!b.IsOpen, "Outgoing overflow terminates explicitly");
        Console.WriteLine("[PASS] Incoming/outgoing overflow fail closed; disconnect clears metrics, stops ping and emits one close event");
    }

    private static void TestClock()
    {
        var clock = new ClockSynchronizer();
        var pong = new ServerMessage { sequence = 1, clientSendTimeMs = 1000, serverReceiveTimeMs = 6050, serverSendTimeMs = 6070, serverFrame = 10 };
        Check(clock.AddSample(pong, 1120), "Clock initializes with different epochs");
        Check(clock.RttMs == 100 && clock.ClockOffsetMs == 5000, "RTT excludes processing; NTP offset");
        Check(Math.Abs(clock.EstimateServerFrame(1120, 20) - 11) < 0.001, "Frame extrapolation");
        Check(!clock.AddSample(pong, 1120), "Duplicate pong rejected");
        pong.sequence = 2;
        Check(!clock.AddSample(pong, 1001), "Impossible RTT rejected");
        Check(clock.AddSample(pong, 1140), "Second sample accepted");
        Check(clock.SmoothedRttMs == 102.5 && clock.JitterMs == 2.5, "RTT and jitter EMA");
        clock.Reset();
        Check(!clock.Initialized && clock.ServerFrame == -1 && clock.LastSequence == 0, "Clock reset");
        Console.WriteLine("[PASS] Clock: separate epochs, processing subtraction, EMA, duplicate and invalid samples, reset");
    }

    private static void Pump()
    {
        context.Pump();
        foreach (var client in clients)
            Call(client, "Update");
        Thread.Sleep(1);
    }
    private static void PumpFor(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) Pump();
    }
    private static void Wait(Func<bool> condition, int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs) throw new Exception("Timed out waiting for network condition");
            Pump();
        }
    }
    private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("[FAIL] " + message);
    }
}
