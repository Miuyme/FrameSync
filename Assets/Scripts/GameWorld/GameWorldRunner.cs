using System;
using FrameSync.Shared;
using UnityEngine;
using UnityEngine.InputSystem;

public class GameWorldRunner : MonoBehaviour
{
    [Header("Player Views")]
    [SerializeField] private Transform player0View;
    [SerializeField] private Transform player1View;

    // 逻辑模拟器
    private GameWorld world;
    // 上一逻辑帧状态
    private PlayerState[] previousStates =
    new PlayerState[GameWorld.PlayerCount];

    // 当前逻辑帧状态
    private PlayerState[] currentStates =
    new PlayerState[GameWorld.PlayerCount];

    // 当前帧的输入
    private PlayerCommand[] commands =
        new PlayerCommand[GameWorld.PlayerCount];

    // 逻辑帧累积时间
    private float accumulator;

    // 每帧逻辑耗时
    private const float TickInterval =
        1f / GameWorld.TickRate;

    private void Awake()
    {
        world = new GameWorld();

        CaptureStates();
        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            previousStates[i] = currentStates[i];
        }

        RenderWorld(0);
    }

    private void Update()
    {
        // 这里只负责驱动逻辑时钟
        accumulator += Time.deltaTime;

        Keyboard keyboard = Keyboard.current;

        
        // 每积累一个TickInterval，就推进一帧
        while (accumulator >= TickInterval)
        {
            // 保存上一帧状态
            for (int i = 0; i < GameWorld.PlayerCount; i++)
            {
                previousStates[i] = currentStates[i];
            }

            // 收集玩家输入
            commands[0] = ReadPlayer0(keyboard);
            commands[1] = ReadPlayer1(keyboard);

            // 推进逻辑世界
            world.Step(commands);

            CaptureStates();
            accumulator -= TickInterval;
        }
        float alpha = accumulator / TickInterval;
        // 根据逻辑状态更新Unity画面
        RenderWorld(alpha);
    }
    private void CaptureStates()
    {
        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            currentStates[i] = world.GetPlayerState(i);
        }
    }

    /// <summary>
    /// 玩家0使用WASD
    /// </summary>
    private static PlayerCommand ReadPlayer0(
        Keyboard keyboard)
    {
        if (keyboard == null)
            return new PlayerCommand(0, 0);

        int x =
            (keyboard.dKey.isPressed ? 1 : 0) -
            (keyboard.aKey.isPressed ? 1 : 0);

        int y =
            (keyboard.wKey.isPressed ? 1 : 0) -
            (keyboard.sKey.isPressed ? 1 : 0);

        return new PlayerCommand(x, y);
    }

    /// <summary>
    /// 玩家1使用方向键
    /// </summary>
    private static PlayerCommand ReadPlayer1(
        Keyboard keyboard)
    {
        if (keyboard == null)
            return new PlayerCommand(0, 0);

        int x =
            (keyboard.rightArrowKey.isPressed ? 1 : 0) -
            (keyboard.leftArrowKey.isPressed ? 1 : 0);

        int y =
            (keyboard.upArrowKey.isPressed ? 1 : 0) -
            (keyboard.downArrowKey.isPressed ? 1 : 0);

        return new PlayerCommand(x, y);
    }

    /// <summary>
    /// 将逻辑坐标转换为Unity坐标
    /// </summary>
    private void RenderWorld(float alpha)
    {
        alpha = Mathf.Clamp01(alpha);

        for (int i = 0; i < GameWorld.PlayerCount; i++)
        {
            Transform target = i == 0
                ? player0View
                : player1View;

            if (target == null)
                continue;

            PlayerState previous = previousStates[i];
            PlayerState current = currentStates[i];

            Vector3 previousPosition = new Vector3(
                previous.X / (float)GameWorld.PositionScale,
                previous.Y / (float)GameWorld.PositionScale,
                target.position.z
            );

            Vector3 currentPosition = new Vector3(
                current.X / (float)GameWorld.PositionScale,
                current.Y / (float)GameWorld.PositionScale,
                target.position.z
            );

            // 只对表现坐标进行插值
            target.position = Vector3.Lerp(
                previousPosition,
                currentPosition,
                alpha
            );
        }
    }

    // private static void SetPosition(
    //     Transform target,
    //     PlayerState state)
    // {
    //     if (target == null)
    //         return;

    //     target.position = new Vector3(
    //         state.X / (float)GameWorld.PositionScale,
    //         state.Y / (float)GameWorld.PositionScale,
    //         target.position.z
    //     );
    // }
}