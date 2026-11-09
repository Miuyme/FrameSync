using System;
using FrameSync.Shared;

/// <summary>Presentation-only timeline. Uses confirmed states and never extrapolates.</summary>
public sealed class RemoteInterpolationBuffer
{
    private readonly PlayerState[] states;
    private readonly int tickRate;
    private readonly int delayFrames;
    private int oldestFrame;
    private int newestFrame;
    private double renderFrame;
    private bool hasState;
    private bool playing;

    public RemoteInterpolationBuffer(int tickRate, int delayFrames = 2, int capacity = 64)
    {
        if (tickRate < 1 || delayFrames < 1 || capacity <= delayFrames + 1)
            throw new ArgumentOutOfRangeException(nameof(delayFrames));
        this.tickRate = tickRate;
        this.delayFrames = delayFrames;
        states = new PlayerState[capacity];
    }

    private int Index(int frame) => (frame % states.Length + states.Length) % states.Length;

    public bool Add(int frame, PlayerState state)
    {
        if (hasState && frame != newestFrame + 1)
            return false;
        if (!hasState)
        {
            oldestFrame = frame;
            renderFrame = frame;
            hasState = true;
        }
        states[Index(frame)] = state;
        newestFrame = frame;
        oldestFrame = Math.Max(oldestFrame, newestFrame - states.Length + 1);
        // Extremely long main-thread stalls can exhaust history; stay inside retained data.
        renderFrame = Math.Max(renderFrame, oldestFrame);
        return true;
    }

    public bool TrySample(double elapsedSeconds, out double x, out double y)
    {
        x = y = 0;
        if (!hasState || double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
            return false;
        if (!playing && newestFrame - oldestFrame >= delayFrames)
            playing = true;
        if (playing)
        {
            // Slowly drain excess jitter backlog without rewinding the render clock.
            double speed = newestFrame - renderFrame > delayFrames + 1 ? 1.1 : 1;
            renderFrame = Math.Min(newestFrame, renderFrame + elapsedSeconds * tickRate * speed);
        }
        int fromFrame = (int)Math.Floor(renderFrame);
        int toFrame = Math.Min(fromFrame + 1, newestFrame);
        double alpha = renderFrame - fromFrame;
        PlayerState from = states[Index(fromFrame)];
        PlayerState to = states[Index(toFrame)];
        x = from.X + ((double)to.X - from.X) * alpha;
        y = from.Y + ((double)to.Y - from.Y) * alpha;
        return true;
    }
}
