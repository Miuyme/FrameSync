using System;
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
