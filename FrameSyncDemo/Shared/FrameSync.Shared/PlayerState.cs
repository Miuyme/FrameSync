namespace FrameSync.Shared
{
    /// <summary>
    /// 玩家当前逻辑状态1000逻辑单位等于Unity中1单位 1000=1
    /// </summary>
    public struct PlayerState
    {
        public int X;
        public int Y;
        public PlayerState(int x,int y)
        {
            X = x;
            Y = y;
        }
    }
}