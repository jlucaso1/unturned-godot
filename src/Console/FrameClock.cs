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
    private static ulong PreviousPhysicsFrames;
    private static double MeasuredMs;
    private static int MeasuredSteps;

    // Called once per rendered frame from the console overlay, which is in the tree for the whole session.
    public static void Tick()
    {
        ulong now = Time.GetTicksUsec();
        ulong physicsFrames = Engine.GetPhysicsFrames();
        if (PreviousUsec != 0UL)
        {
            MeasuredMs = (now - PreviousUsec) / 1000d;
            MeasuredSteps = (int)(physicsFrames - PreviousPhysicsFrames);
        }
        PreviousUsec = now;
        PreviousPhysicsFrames = physicsFrames;
    }

    // 0 before the second tick — no overlay in the tree, or the very first frame after one appears.
    public static double LastFrameMs => MeasuredMs;

    // How many physics steps ran inside that frame, which is not always one and is 0 before the clock has
    // measured anything. Below the physics tick rate the engine runs several steps between two rendered
    // frames, and TimePhysicsProcess prices ONE of them — so the frame's physics bill is the step times
    // this, and counting it as a single step leaves the rest of the work unattributed.
    public static int LastPhysicsSteps => MeasuredSteps;
}
