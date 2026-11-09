using FrameSync.Shared;

using UnityEngine.InputSystem;

public sealed class InputCollector
{
    /// <summary>
    /// 根据玩家编号采集键盘输入
    /// </summary>
    public PlayerCommand Collect(int playerId)
    {
        Keyboard keyboard = Keyboard.current;

        if (keyboard == null)
        {
            return new PlayerCommand(0, 0);
        }

        int x = 0;
        int y = 0;

        if (playerId == 0)
        {
            x = (keyboard.dKey.isPressed ? 1 : 0) -
                (keyboard.aKey.isPressed ? 1 : 0);

            y = (keyboard.wKey.isPressed ? 1 : 0) -
                (keyboard.sKey.isPressed ? 1 : 0);

        }
        else if (playerId == 1)
        {
            x =
                (keyboard.rightArrowKey.isPressed ? 1 : 0) -
                (keyboard.leftArrowKey.isPressed ? 1 : 0);

            y =
                (keyboard.upArrowKey.isPressed ? 1 : 0) -
                (keyboard.downArrowKey.isPressed ? 1 : 0);
        }

        return new PlayerCommand(x, y);
    }
}