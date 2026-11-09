using System;
using FrameSync.Shared;

/// <summary>Diagnostics only. Times are monotonic milliseconds in each peer's own epoch.</summary>
public sealed class ClockSynchronizer
{
    private const double Alpha = 0.125;
    private double anchorClientTimeMs;
    private double anchorFrame;
    public bool Initialized { get; private set; }
    public double RttMs { get; private set; }
    public double SmoothedRttMs { get; private set; }
    public double JitterMs { get; private set; }
    public double ClockOffsetMs { get; private set; }
    public int LastSequence { get; private set; }
    public int ServerFrame { get; private set; } = -1;

    public bool AddSample(ServerMessage pong, double receiveTimeMs)
    {
        if (pong == null)
            return false;
        double t0 = pong.clientSendTimeMs;
        double t1 = pong.serverReceiveTimeMs;
        double t2 = pong.serverSendTimeMs;
        double t3 = receiveTimeMs;
        double rtt = (t3 - t0) - (t2 - t1);
        if (double.IsNaN(t3) || double.IsInfinity(t3) || pong.sequence <= LastSequence || pong.serverFrame < -1 || t0 < 0 || t1 < 0 || t2 < t1 || t3 < t0 || rtt < -1 || t3 - t0 > 10000)
            return false;
        rtt = Math.Max(0, rtt); // Integer timestamp quantization can contribute up to 1 ms.
        double offset = ((t1 - t0) + (t2 - t3)) / 2;
        if (!Initialized)
        {
            SmoothedRttMs = rtt;
            ClockOffsetMs = offset;
        }
        else
        {
            JitterMs += Alpha * (Math.Abs(rtt - RttMs) - JitterMs);
            SmoothedRttMs += Alpha * (rtt - SmoothedRttMs);
            ClockOffsetMs += Alpha * (offset - ClockOffsetMs);
        }
        RttMs = rtt;
        LastSequence = pong.sequence;
        ServerFrame = pong.serverFrame;
        anchorFrame = pong.serverFrame;
        anchorClientTimeMs = t2 - ClockOffsetMs;
        Initialized = true;
        return true;
    }

    // Nominal extrapolation; lockstep may stall while waiting for input.
    public double EstimateServerFrame(double clientTimeMs, int tickRate)
    {
        if (!Initialized || ServerFrame < 0)
            return -1;
        return anchorFrame + Math.Max(0, clientTimeMs - anchorClientTimeMs) * tickRate / 1000;
    }

    public void Reset()
    {
        Initialized = false;
        RttMs = SmoothedRttMs = JitterMs = ClockOffsetMs = 0;
        LastSequence = 0;
        ServerFrame = -1;
        anchorClientTimeMs = anchorFrame = 0;
    }
}
