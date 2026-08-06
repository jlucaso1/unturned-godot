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

    // Whether a full interval has been observed yet. Callers need this to tell an unmeasured frame from a
    // measured one, because 0 is a legitimate answer for the step count below and a meaningless one for a
    // clock that has only ticked once.
    public static bool HasMeasured => PreviousUsec != 0UL && MeasuredMs > 0d;

    // 0 before the second tick — no overlay in the tree, or the very first frame after one appears.
    public static double LastFrameMs => MeasuredMs;

    // How many physics steps ran inside that frame, which is not always one. Below the physics tick rate
    // the engine runs several steps between two rendered frames while TimePhysicsProcess prices ONE of
    // them, so the frame's physics bill is the step times this. Above the tick rate it is legitimately
    // ZERO on most frames — those frames ran no physics at all, and charging them a step would eat real
    // GPU wait out of exactly the high-FPS frame where that reading is the whole question.
    public static int LastPhysicsSteps => MeasuredSteps;
}
