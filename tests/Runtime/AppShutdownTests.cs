using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Leaving.
//
// The quit itself is deliberately NOT exercised here, and the reason is worth stating rather than
// leaving as a hole in the file. RequestQuit cancels one process-wide token and then tears the tree
// down — so a test that called it would end the run, and every test after it would be reported as
// neither passed nor failed. Worse, the cancellation is irreversible: the reconciliation pass, the bundle
// decoders and the extraction workers all check IsShuttingDown at their loop boundaries, so a single call
// would quietly turn the rest of the suite into no-ops that still report success.
//
// What is covered is everything around it: the tracking a quit waits on, and the guards that keep a
// worker from talking to an engine that is already gone.
public class AppShutdownTests : TestClass
{
    public AppShutdownTests(Node testScene) : base(testScene) { }

    // Nothing is shutting down during a test run, which is the precondition every other assertion here
    // rests on — and, if it ever failed, would explain a suite that passed while doing nothing.
    [Test]
    public void TheSuiteIsNotShuttingDown()
    {
        Assert.False(AppShutdown.IsShuttingDown);
        Assert.False(AppShutdown.Token.IsCancellationRequested);
    }

    // Tracking hands the task straight back, so call sites stay one-liners rather than growing a local
    // for something they only wanted to register.
    [Test]
    public void TrackingHandsTheTaskBack()
    {
        Task work = Task.CompletedTask;

        Assert.Same(work, AppShutdown.Track(work));
    }

    // The set does not grow. Finished work is dropped on the next registration, which is what keeps a
    // load that decodes hundreds of bundle nodes from handing the quit a list of hundreds of completed
    // tasks to walk.
    [Test]
    public async Task FinishedWorkIsDroppedRatherThanAccumulated()
    {
        for (int i = 0; i < 200; i++)
            _ = AppShutdown.Track(Task.CompletedTask);

        // Nothing observable to read back — the set is private, deliberately — so what this covers is
        // that registering repeatedly stays cheap and cannot throw. A leak here would show up as a quit
        // that walks an ever-longer list.
        await Task.CompletedTask;
    }

    // Tracking work that FAILED does not raise the failure here. Registration happens where the task is
    // started, long before anyone awaits it, and a throw at that point would take down the load for a
    // cache write that could not land.
    [Test]
    public void TrackingWorkThatFailedIsQuiet()
    {
        Task failed = Task.FromException(new System.IO.IOException("a cache write that could not land"));

        AppShutdown.Track(failed);

        Assert.True(failed.IsFaulted);
        _ = failed.Exception; // observed, so the finalizer has nothing to escalate
    }

    // The guarded channels print while the engine is alive. Their whole purpose is the other case — a
    // worker logging progress into a subsystem that teardown has already taken away — which cannot be
    // reached from a suite that must not shut down.
    [Test]
    public void TheGuardedChannelsSpeakWhileTheEngineIsAlive()
    {
        AppShutdown.PrintUnlessQuitting("[runtime-tests] a line from a worker that is still running");
        AppShutdown.WarnUnlessQuitting("[runtime-tests] a warning from a worker that is still running");
    }

    // A benchmark in flight owns the exit status: leaving mid-measurement IS a failed measurement,
    // whoever asked to leave. The bracket has to be balanced, or every later clean quit in the process
    // would be turned into a failure — which is exactly what this test would do to the suite's own exit
    // status if it forgot to end what it began.
    [Test]
    public void TheBenchmarkBracketIsBalanced()
    {
        AppShutdown.BeginBenchmark();
        AppShutdown.EndBenchmark();

        Assert.False(AppShutdown.IsShuttingDown);
    }
}
