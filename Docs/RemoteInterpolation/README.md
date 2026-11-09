# 远端玩家反复停走时的过度预测修复

## 原因

RollbackSimulator.ExecuteFrame 对未知的远端输入采用 previous.Executed[remotePlayerId]，所以最后一次已知输入为移动时，会持续预测移动。NetworkGameRunner 允许预测到 lastServerFrame + inputDelay + 2；默认 inputDelay=3、TickRate=20，最多领先约 250 ms。远端突然停止时，这些帧的移动是错误预测，权威停止输入到达后回滚会使位置退回。

原 VisualCorrection 将两个玩家的预测结果都直接用于显示，然后衰减回滚位置差。它只能平滑回退，无法消除错误预测；单纯加大 correctionHalfLife 会延长回退过程。

## 修改

- 本地玩家仍显示预测状态，并保留原来的回滚视觉校正，维持操作响应。
- 远端玩家显示已确认状态之间的插值，不再显示预测位置或套用回滚视觉偏移。
- 在 SnapshotRingBuffer 增加只读状态副本接口，RollbackSimulator 只向显示层暴露已确认帧的快照。取的是权威 message.frame 对应的已校正状态，不是回滚并重演到未来后的 world.CurrentFrame。
- 新增 RemoteInterpolationBuffer，默认容量 64、初始缓冲 2 帧，按单调向前的显示时间在相邻已确认状态之间插值。数据不足就停在最新确认位置，不延续上一条移动指令。
- 抖动积压超过目标缓冲时以 1.1 倍速度温和追赶，不倒退显示时钟。超长卡顿超过缓存容量时会前移到最早仍保留的样本，可能产生追赶跳变；这与预测回拉不同。
- 权威帧执行、PlayerCommand、GameWorld、预测输入策略、Hash、InputDelay 和服务器消息均不改变。逻辑层仍可能发生回滚，Rollback Count 不一定降低；本次解决的是远端画面暴露错误预测的问题。

## 文件

| 文件 | 修改 |
|---|---|
| Assets/Scripts/View/RemoteInterpolationBuffer.cs、.meta | 已确认状态显示缓存和插值 |
| Assets/Scripts/View/VisualCorrection.cs | 可只处理本地预测玩家，默认参数保留旧调用兼容性 |
| Assets/Scripts/GameWorld/NetworkGameRunner.cs | 喂入已确认远端状态，本地预测显示与远端确认显示分开 |
| FrameSyncDemo/Shared/FrameSync.Shared/SnapshotRingBuffer.cs | 读取指定帧玩家状态的副本 |
| FrameSyncDemo/Shared/FrameSync.Shared/RollbackSimulator.cs | 只读访问已确认快照 |
| FrameSyncDemo/Tests/FrameSync.SimTests/Program.cs、.csproj | 增加真实过度预测、回滚与远端显示的回归测试 |
| Assets/Plugins/FrameSync.Shared.dll、.xml | 更新 Shared 接口编译产物供 Unity 使用 |

修改前文件保存在 Before/，当前目录仍然没有 Git 元数据。完整新增和修改源代码收录于 CompleteCode.md。

## 验证

执行并通过：

```powershell
dotnet run --project FrameSyncDemo/Tests/FrameSync.SimTests --no-restore
dotnet build Docs/NetworkDiagnostics/UnityCompile --no-restore -m:1
dotnet build FrameSyncDemo/Server/FrameSync.Server --no-restore -m:1
```

新增测试使用真实 RollbackSimulator，远端 80 帧反复前进/停止，权威数据延迟 4 tick；断言实际发生过度预测和回滚，但远端插值位置没有倒退、没有越过已确认位置，断流停住，最终状态与服务器一致。该测试也覆盖了 64 槽显示缓存的环绕。

原有 Lockstep、Replay、Snapshot、Rollback 测试全部通过。1000 帧 Hash 仍为 1D1C0C083F1A7602，Rollback 测试最终 Hash 仍为 517F0F2EC8B97B7D。Unity 程序集编译 0 错误，6 个原有 Inspector 引用字段未赋值警告；原 Shared 和控制台测试仍有原有文档/可空引用警告。详细结果见同目录日志。

没有运行 Unity 编辑器 Play，画面效果需双客户端人工确认。未改变网络协议，未重复运行此前的网络诊断全套验收。

## 使用与取舍

1. 等待 Unity 重新导入 Shared DLL，用当前源码重新构建客户端；旧 Build/OnlineGame.exe 不含本修复。
2. 启动服务端和两个新客户端。在 NetworkGameRunner 的 Remote Presentation 中，Remote Interpolation Delay Frames 默认 2。该配置在加入房间时读取，调参后重新进入一局。
3. C1 反复前进/停止，从 C2 观察 C1；也交换控制者测试。远端应按已确认路径停走，不再先越过停止位置再回退。调试器中仍可能出现正常的 Rollback。
4. 默认 2 帧约 100 ms 的初始显示缓冲，叠加既有输入延迟和网络传输时间。可改 1 帧降低显示延迟；较强抖动可用 3 帧。减少缓冲会增加断续的可能，不会让本实现外推远端位置。

不能同时做到未知远端输入零延迟、永远正确且完全不校正。本次选择让本地玩家保持响应，让远端玩家接受少量显示延迟，避免把错误的远端预测展示出来。今后若加入玩家碰撞或近身战斗，需要另行处理预测逻辑位置与远端显示位置的时间差；当前工程只有简单移动。
