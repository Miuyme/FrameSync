namespace FrameSync.Shared
{
    /// <summary>
    /// 不同游戏世界的状态比较器
    /// </summary>
    public static class StateChecker
    {
        public static bool TryCompare(
            GameWorld a,
            GameWorld b,
            out string reason)
        {
            reason = string.Empty;

            if (a == null || b == null)
            {
                reason = "World is null";
                return false;
            }

            // 检查帧编号
            if (a.CurrentFrame != b.CurrentFrame)
            {
                reason =
                    $"Frame mismatch: " +
                    $"{a.CurrentFrame} != {b.CurrentFrame}";

                return false;
            }

            // 检查每个玩家的坐标
            for (int i = 0; i < GameWorld.PlayerCount; i++)
            {
                PlayerState stateA = a.GetPlayerState(i);
                PlayerState stateB = b.GetPlayerState(i);

                if (stateA.X != stateB.X)
                {
                    reason =
                        $"Player {i} X mismatch: " +
                        $"{stateA.X} != {stateB.X}";

                    return false;
                }

                if (stateA.Y != stateB.Y)
                {
                    reason =
                        $"Player {i} Y mismatch: " +
                        $"{stateA.Y} != {stateB.Y}";

                    return false;
                }
            }

            // 最后比较Hash
            if (a.CalculateHash() != b.CalculateHash())
            {
                reason = "Hash mismatch";

                return false;
            }

            return true;
        }
    }
}
