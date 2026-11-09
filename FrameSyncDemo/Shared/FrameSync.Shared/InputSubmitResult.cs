namespace FrameSync.Shared
{
    public enum InputSubmitResult
    {
        /// <summary>
        /// 成功提交
        /// </summary>
        Accepted,

        /// <summary>
        /// 玩家ID无效
        /// </summary>
        InvalidPlayer,

        /// <summary>
        /// 帧编号无效
        /// </summary>
        InvalidFrame,

        /// <summary>
        /// 提交的帧已经完成
        /// </summary>
        TooLate,

        /// <summary>
        /// 同一玩家重复提交同一帧
        /// </summary>
        Duplicate,

        /// <summary>
        /// 提交的帧距离当前帧过远
        /// </summary>
        TooFarAhead
    }
}