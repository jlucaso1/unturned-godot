using System.Threading;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Leaving.
//
// The quit itself is still NOT exercised here: RequestQuit and QuitNow both end the process, so a test
// that called one would end the run and every test after it would be reported as neither passed nor
// failed. That part is unchanged.
//
// What HAS changed is the flag they raise on the way. It used to be irreversible — one process-wide
// CancellationTokenSource, created once — so the first test to raise it left IsShuttingDown true for
// every test after it. The reconciliation pass, the bundle decoders, the extraction workers and both
// guarded log channels all read that flag at their loop boundaries, so the rest of the suite would have
// quietly become no-ops that still reported success. Nothing could exercise "quit, then carry on".
//
// SignalForTests raises the flag without leaving and ResetForTests puts it back, which is what makes the
// guards observable at all. Every test here that raises it MUST reset it, and the fixture below is why
// they do so in a finally.
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

        // Two hundred registrations, none of them still running. The count is what the quit path walks,
        // so a set that accumulated would make every shutdown inspect a list of finished work — and the
        // name of this test was the only thing saying otherwise until now.
        Assert.Equal(0, AppShutdown.StillRunning());

        // And one that IS running is kept, or the pruning would be indiscriminate rather than correct.
        using var running = new System.Threading.CancellationTokenSource();
        _ = AppShutdown.Track(Task.Delay(Timeout.Infinite, running.Token));
        Assert.Equal(1, AppShutdown.StillRunning());

        running.Cancel();
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

    // The guards, from the other side. This is what a worker sees once teardown has started, and until
    // the flag could be put back it was unreachable from a test at all.
    [Test]
    public void OnceTeardownStartsTheGuardedChannelsFallSilent()
    {
        const string line = "[runtime-tests] a line from a worker that is on its way out";
        try
        {
            AppShutdown.SignalForTests();

            Assert.True(AppShutdown.IsShuttingDown);
            Assert.True(AppShutdown.Token.IsCancellationRequested);

            AppShutdown.PrintUnlessQuitting(line);
            AppShutdown.WarnUnlessQuitting(line);
        }
        finally
        {
            AppShutdown.ResetForTests();
        }

        // Log keeps a ring of what it has printed, which is the only way to see that a line did NOT go
        // out. A guard that let it through would reach a subsystem teardown has already taken away.
        Assert.DoesNotContain(Log.Tail(), printed => printed.Contains(line, System.StringComparison.Ordinal));
    }

    // ...and then the process carries on. This is the property the whole reset exists for: without it,
    // every test after the one above would be running against a shutting-down engine and passing anyway.
    [Test]
    public void TheFlagCanBePutBack()
    {
        AppShutdown.SignalForTests();
        Assert.True(AppShutdown.IsShuttingDown);

        AppShutdown.ResetForTests();

        Assert.False(AppShutdown.IsShuttingDown);
        Assert.False(AppShutdown.Token.IsCancellationRequested);

        // And the guards speak again, which is what the rest of the suite depends on.
        const string line = "[runtime-tests] a line from a worker that is running again";
        AppShutdown.PrintUnlessQuitting(line);
        Assert.Contains(Log.Tail(), printed => printed.Contains(line, System.StringComparison.Ordinal));
    }

    // Resetting drops the tracked work as well as the flag. A test that left a wedged task registered
    // would have every later quit path in the process wait its full grace period for it.
    [Test]
    public async Task ResettingAlsoDropsTheTrackedWork()
    {
        using var running = new CancellationTokenSource();
        _ = AppShutdown.Track(Task.Delay(Timeout.Infinite, running.Token));
        Assert.Equal(1, AppShutdown.StillRunning());

        AppShutdown.ResetForTests();

        Assert.Equal(0, AppShutdown.StillRunning());
        running.Cancel();
        await Task.CompletedTask;
    }
}
