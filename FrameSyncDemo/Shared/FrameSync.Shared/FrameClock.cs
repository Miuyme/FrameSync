using System;

namespace FrameSync.Shared
{
    /// <summary>
    /// 固定逻辑帧时钟
    /// 只管理时间，不负责执行游戏逻辑
    /// </summary>
    public sealed class FrameClock
    {
        private readonly double tickInterval;

        private double accumulator;

        public double TickInterval => tickInterval;

        public double Accumulator => accumulator;

        /// <summary>
        /// 是否已经到达下一逻辑帧的时间
        /// </summary>
        public bool IsTickDue =>
            accumulator >= tickInterval;

        /// <summary>
        /// 当前插值比例
        /// </summary>
        public double InterpolationAlpha
        {
            get
            {
                double alpha = accumulator / tickInterval;

                if (alpha < 0)
                    return 0;

                if (alpha > 1)
                    return 1;

                return alpha;
            }
        }

        public FrameClock(int tickRate)
        {
            if (tickRate <= 0)
                throw new ArgumentOutOfRangeException(
                    nameof(tickRate));

            tickInterval = 1.0 / tickRate;

            accumulator = 0;
        }

        /// <summary>
        /// 累积经过的时间
        /// </summary>
        public void Accumulate(double elapsedSeconds)
        {
            if (double.IsNaN(elapsedSeconds) ||
                double.IsInfinity(elapsedSeconds) ||
                elapsedSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elapsedSeconds));
            }

            accumulator += elapsedSeconds;
        }

        /// <summary>
        /// 消耗一个逻辑帧的时间
        /// </summary>
        public void ConsumeTick()
        {
            if (!IsTickDue)
            {
                throw new InvalidOperationException(
                    "Tick is not due");
            }

            accumulator -= tickInterval;

            if (accumulator < 0)
                accumulator = 0;
        }

        public void Reset()
        {
            accumulator = 0;
        }
    }
}