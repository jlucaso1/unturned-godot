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
    private static double AccumulatedPhysicsMs;
    private static double MeasuredPhysicsMs;
    private static FrameClockTicker? Ticker;

    // Puts the per-frame tick in the tree, once per process, wherever the console is speaking from.
    //
    // It carries its own node rather than borrowing the overlay's _Process because the overlay is not the
    // only host: a benchmark tier sets RenderConsole.Host to its own context node and has no pane at all,
    // and that is the session where `perf` matters most — UG_CONSOLE plus a report is how this project
    // measures. Hanging the tick off the overlay left exactly those runs reading an averaged frame under a
    // line that says "last completed", which is the failure this whole clock exists to remove.
    //
    // Parented to the scene root, not the host: the host is torn down and rebuilt on every map load, and a
    // process-scoped clock that restarted on each load would go unmeasured for the first frame of every
    // world — including the frame right after the load a measurement is comparing.
    public static void EnsureTicking(Node anchor)
    {
        if (Ticker != null && GodotObject.IsInstanceValid(Ticker))
            return;
        if (!anchor.IsInsideTree())
            return;
        var ticker = new FrameClockTicker { Name = "UgFrameClock" };
        Ticker = ticker;
        // Deferred: a host typically sets itself during _Ready, and the tree is busy building at that
        // point. AddChild would complain rather than land.
        anchor.GetTree().Root.CallDeferred(Node.MethodName.AddChild, ticker);
    }

    public static void Tick()
    {
        ulong now = Time.GetTicksUsec();
        ulong physicsFrames = Engine.GetPhysicsFrames();
        if (PreviousUsec != 0UL)
        {
            MeasuredMs = (now - PreviousUsec) / 1000d;
            MeasuredSteps = (int)(physicsFrames - PreviousPhysicsFrames);
            MeasuredPhysicsMs = AccumulatedPhysicsMs;
        }
        AccumulatedPhysicsMs = 0d;
        PreviousUsec = now;
        PreviousPhysicsFrames = physicsFrames;
    }

    // Called once per physics step, and the reason the frame's physics bill is a SUM rather than an
    // estimate. TimePhysicsProcess prices one step, so multiplying the latest sample by the step count
    // assumes every step in the frame cost the same — and the frames with several steps are exactly the
    // ones where that is least true, since what makes a step expensive (an AI pass, a collision burst)
    // arrives in bursts. Reading the monitor at each step and adding it up drops the assumption.
    //
    // The reading lags by one step: inside a step, the monitor still describes the one before it. Over a
    // frame that shifts which steps are counted, not how many, which is what the subtraction needs.
    public static void TickPhysics() =>
        AccumulatedPhysicsMs += Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000d;

    // Whether a full interval has been observed yet. Callers need this to tell an unmeasured frame from a
    // measured one, because 0 is a legitimate answer for the step count below and a meaningless one for a
    // clock that has only ticked once.
    public static bool HasMeasured => PreviousUsec != 0UL && MeasuredMs > 0d;

    // 0 before the second tick: nothing has hosted the console yet, or the ticker has only seen one frame.
    public static double LastFrameMs => MeasuredMs;

    // How many physics steps ran inside that frame, which is not always one. Below the physics tick rate
    // the engine runs several steps between two rendered frames while TimePhysicsProcess prices ONE of
    // them, so the frame's physics bill is the step times this. Above the tick rate it is legitimately
    // ZERO on most frames — those frames ran no physics at all, and charging them a step would eat real
    // GPU wait out of exactly the high-FPS frame where that reading is the whole question.
    public static int LastPhysicsSteps => MeasuredSteps;

    // Every physics step in that frame, summed as it happened rather than estimated from one sample.
    public static double LastPhysicsMs => MeasuredPhysicsMs;
}

// The tick, as a node, because only a node gets called once per rendered frame — and once per physics
// step, which is a different rate and the whole reason both hooks are here. It holds no state of its own:
// everything measured lives in the static above, which is what lets it survive a map load.
internal sealed partial class FrameClockTicker : Node
{
    public override void _Process(double delta) => FrameClock.Tick();

    public override void _PhysicsProcess(double delta) => FrameClock.TickPhysics();
}
