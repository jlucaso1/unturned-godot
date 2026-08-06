using Godot;

namespace UnturnedGodot.DevConsole;

// The wall-clock length of the last rendered frame, measured rather than inferred.
//
// Nothing Godot exposes answers this question. TimeFps is the last SECOND averaged and refreshes once a
// second. GetProcessDeltaTime is the SIMULATION delta, which is a different quantity wearing the same
// units: --fixed-fps pins it, Engine.TimeScale scales it, delta smoothing shapes it, and the
// max-physics-steps clamp caps it exactly when frames run longest — so the one frame whose duration you
// most want to read is the one it stops reporting truthfully.
//
// Time.GetTicksUsec is the monotonic clock none of those touch, so the interval is taken from it directly
// and the per-frame tick below is the only place that has to be right.
internal static class FrameClock
{
    private static ulong PreviousUsec;
    private static double MeasuredMs;

    // Called once per rendered frame from the console overlay, which is in the tree for the whole session.
    public static void Tick()
    {
        ulong now = Time.GetTicksUsec();
        if (PreviousUsec != 0UL)
            MeasuredMs = (now - PreviousUsec) / 1000d;
        PreviousUsec = now;
    }

    // 0 before the second tick — no overlay in the tree, or the very first frame after one appears.
    public static double LastFrameMs => MeasuredMs;
}
