using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace UnturnedGodot;

// Coordinates leaving the game. Three things make quitting from a loaded world unsafe without it.
//
// First, the quit is requested from inside a button's signal handler, so calling SceneTree.Quit there
// tears the tree down while the node that asked for it is still emitting. Deferring the call lets the
// current frame finish first.
//
// Second, the load leaves work on the thread pool — extracting audio, and on a cold load streaming
// meshes and textures out of the 1.4 GB bundle. Those tasks call into the engine (logging, and handing
// results back with Callable.CallDeferred), so once teardown starts they are touching objects that are
// being freed underneath them. That is what a "corrupted size vs. prev_size" abort at exit looks like.
//
// Third — and this is what raising a flag alone does NOT solve — deferring Quit only postpones it to the
// next idle frame, which arrives long before a worker sitting inside a multi-hundred-megabyte decode
// reaches its next cancellation checkpoint. So the flag is not enough: the tasks that do that work are
// registered here, and the quit waits for them to actually finish.
//
// Hence: raise the flag, wait for the registered work to observe it, then quit. Workers poll
// IsShuttingDown at their loop boundaries rather than being aborted, which keeps a half-written cache
// file from ever being left behind.
public static class AppShutdown
{
    // How long the quit will wait for background work before leaving regardless. A cold load's decode
    // checkpoints are per bundle node, so the wait is normally milliseconds; the cap only exists so a
    // worker wedged on slow IO cannot hold the window open forever. Leaving after it is still safe:
    // every engine call a worker makes is gated on IsShuttingDown, so a straggler writes cache files
    // (plain file IO) and nothing else.
    private static readonly System.TimeSpan Grace = System.TimeSpan.FromSeconds(10);

    private static readonly CancellationTokenSource Source = new();

    // The background work a quit has to wait for. Held weakly in the sense that finished tasks are
    // dropped on the next inspection; the set never grows past the handful of load-time tasks.
    private static readonly List<Task> Background = new();

    // True once the game has begun leaving. Background work checks this at loop boundaries and returns;
    // anything that talks to the engine from a worker must check it before doing so.
    public static bool IsShuttingDown => Source.IsCancellationRequested;

    public static CancellationToken Token => Source.Token;

    // Registers a task the quit must wait for: anything that decodes a bundle, writes the cache, or
    // hands results back to the main thread. Returns the task so call sites can stay one-liners.
    public static Task Track(Task task)
    {
        lock (Background)
        {
            Background.RemoveAll(t => t.IsCompleted);
            Background.Add(task);
        }
        return task;
    }

    // How many registered tasks are still running.
    private static int StillRunning()
    {
        lock (Background)
        {
            Background.RemoveAll(t => t.IsCompleted);
            return Background.Count;
        }
    }

    // Prints only while the engine is still alive. Worker threads log progress, and a log line arriving
    // during teardown reaches a subsystem that is already gone.
    public static void PrintUnlessQuitting(string message)
    {
        if (!IsShuttingDown)
            Log.Print(message);
    }

    // Same guard for the warning channel: PushWarning reaches further into the engine than a print does,
    // so a straggling extraction must not raise one once teardown has started.
    public static void WarnUnlessQuitting(string message)
    {
        if (!IsShuttingDown)
            Log.PushWarning(message);
    }

    // What the process will report. A failure can arrive after teardown has already started — Tier 3
    // calls RequestQuit(tree, 1) with no report written, while an expired QUIT_AFTER or the pause-menu
    // button may already have asked for 0 — and the early return below would drop it, leaving the run
    // looking successful to whatever reads the exit status. So the code lives here, outside the guard,
    // and the first failure wins over any later or earlier request to leave cleanly.
    private static int ExitCode;

    // Raised while a benchmark tier owns the process's exit status and has not written its report yet.
    //
    // Capturing a late failure is not enough on its own. When something else asks to leave mid-run — an
    // expired QUIT_AFTER, the pause-menu button — the deferred quit lands on the next idle frame, which
    // is sooner than the tier gets another frame to reach its finally, so there is no late failure to
    // capture: the process just exits 0 with no report. The tier cannot defend itself here, so the
    // invariant lives on this side. Leaving while a measurement is in flight *is* a failed measurement,
    // whoever asked to leave.
    private static bool BenchmarkInFlight;

    // Brackets a tier whose report decides the exit status. Tier 3 only: it is the tier that runs the
    // full interactive path, and so the only one reachable by a quit it did not ask for.
    public static void BeginBenchmark() => BenchmarkInFlight = true;

    public static void EndBenchmark() => BenchmarkInFlight = false;

    // The single way out of a loaded world. Safe to call more than once. exitCode is what the process
    // reports; a headless caller that failed passes nonzero so its wrapper script can tell.
    public static void RequestQuit(SceneTree tree, int exitCode = 0)
    {
        if (exitCode == 0 && BenchmarkInFlight)
            exitCode = 1;

        if (exitCode != 0 && ExitCode == 0)
            ExitCode = exitCode;

        if (IsShuttingDown)
            return;
        Source.Cancel();

        int running = StillRunning();
        if (running == 0)
        {
            Log.Print("[shutdown] leaving");
            // Deferred: never tear the tree down inside the signal handler that asked for it. The code is
            // read when the call runs, not when it is scheduled, so a failure raised in between still lands.
            Callable.From(() => Leave(tree)).CallDeferred();
            return;
        }

        Log.Print($"[shutdown] leaving; waiting for {running} background task(s) to stop");
        _ = WaitForBackgroundThenQuit(tree);
    }

    // Polls on the main thread rather than blocking it: the workers hand their last results back through
    // CallDeferred, which only runs while frames keep being processed. Blocking here would deadlock a
    // task that is waiting for the main thread to drain.
    private static async Task WaitForBackgroundThenQuit(SceneTree tree)
    {
        var waited = Stopwatch.StartNew();
        while (StillRunning() > 0 && waited.Elapsed < Grace)
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

        int left = StillRunning();
        Log.Print(left == 0
            ? $"[shutdown] background work stopped after {waited.ElapsedMilliseconds} ms"
            : $"[shutdown] {left} background task(s) still busy after {waited.ElapsedMilliseconds} ms; leaving anyway");

        Leave(tree);
    }

    // The one place the process actually ends.
    //
    // UG_COVERAGE=1 leaves through the RUNTIME rather than the engine, and that is the whole reason this
    // method exists. SceneTree.Quit tears the process down without ever returning to managed code, so the
    // module-unload hooks never run — and a coverage instrumenter records its hit counts in exactly those
    // hooks. Measured that way, a complete game run reports zero lines covered, which is worse than no
    // measurement: it reads as code nothing executes. GoDotTest carries its own flag for the same reason,
    // and this is its equivalent for a session rather than a suite.
    //
    // Nothing sets this in production, and the exit code is the same either way.
    private static void Leave(SceneTree tree)
    {
        if (EnvFlag.IsOn(OS.GetEnvironment("UG_COVERAGE"), whenUnset: false))
        {
            System.Environment.Exit(ExitCode);
            return;
        }

        tree.Quit(ExitCode);
    }
}
