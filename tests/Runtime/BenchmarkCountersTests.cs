using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The measurement plumbing the benchmark tiers report through.
//
// It is worth testing for a reason the code says out loud: a production run pays only a volatile boolean
// read for these, and everything else is meant to cost nothing while they are off. A counter that kept
// accumulating when disabled would be a permanent tax on every frame of a shipped game; one that failed
// to accumulate when enabled would make the whole benchmark quietly report zeros, which looks like
// "nothing is slow" rather than like a broken instrument.
public class BenchmarkCountersTests : TestClass
{
    public BenchmarkCountersTests(Node testScene) : base(testScene) { }

    // Counters are off in a production run, and off must mean recorded-nothing rather than
    // recorded-and-discarded.
    [Test]
    public void DisabledCountersRecordNothing()
    {
        RuntimeCounters.Disable();

        // Start hands back 0 while disabled, and Record treats that as "there was no measurement".
        Assert.Equal(0, RuntimeCounters.Start());
        RuntimeCounters.Record(RuntimeCounters.Counter.PlayerPhysics, RuntimeCounters.Start());
        RuntimeCounters.RecordTicks(RuntimeCounters.Counter.PlayerPhysics, 1000);

        RuntimeCounters.ResetAndEnable();
        try
        {
            Assert.Equal(0, RuntimeCounters.Read(RuntimeCounters.Counter.PlayerPhysics).Calls);
        }
        finally
        {
            RuntimeCounters.Disable();
        }
    }

    // Enabled, they accumulate calls, total and maximum. The maximum is the one that matters in a report:
    // a mean hides the single frame that stalled.
    [Test]
    public void EnabledCountersAccumulateCallsTotalAndMaximum()
    {
        RuntimeCounters.ResetAndEnable();
        try
        {
            RuntimeCounters.RecordTicks(RuntimeCounters.Counter.NetworkServer, 100);
            RuntimeCounters.RecordTicks(RuntimeCounters.Counter.NetworkServer, 900);
            RuntimeCounters.RecordTicks(RuntimeCounters.Counter.NetworkServer, 200);

            RuntimeCounters.Sample sample = RuntimeCounters.Read(RuntimeCounters.Counter.NetworkServer);

            Assert.Equal(3, sample.Calls);
            Assert.True(sample.TotalMs > 0d);
            Assert.True(sample.MaxMs >= sample.MeanMs, "the maximum came out below the mean");
            // 900 of 1200 ticks: the peak is three times the mean, and a report that lost it would say
            // the server never stalled.
            Assert.True(sample.MaxMs > sample.MeanMs * 2d,
                $"the peak was flattened: max {sample.MaxMs} vs mean {sample.MeanMs}");
        }
        finally
        {
            RuntimeCounters.Disable();
        }
    }

    // A measurement of zero or negative length is not a measurement. Stopwatch ticks can come back equal
    // across a fast call, and counting those would inflate the call count with samples of nothing.
    [Test]
    public void ZeroLengthMeasurementsAreNotCounted()
    {
        RuntimeCounters.ResetAndEnable();
        try
        {
            RuntimeCounters.RecordTicks(RuntimeCounters.Counter.ZombiesView, 0);
            RuntimeCounters.RecordTicks(RuntimeCounters.Counter.ZombiesView, -5);

            Assert.Equal(0, RuntimeCounters.Read(RuntimeCounters.Counter.ZombiesView).Calls);
        }
        finally
        {
            RuntimeCounters.Disable();
        }
    }

    // Re-enabling resets: a tier that warmed up and then sampled must not carry the warm-up frames into
    // the numbers it reports, which is the whole reason enabling and resetting are one call.
    [Test]
    public void EnablingResetsWhateverThePreviousRunLeftBehind()
    {
        RuntimeCounters.ResetAndEnable();
        RuntimeCounters.RecordTicks(RuntimeCounters.Counter.PlayerStep, 5000); // "warm-up"
        Assert.Equal(1, RuntimeCounters.Read(RuntimeCounters.Counter.PlayerStep).Calls);

        RuntimeCounters.ResetAndEnable(); // the sampling window opens
        try
        {
            Assert.Equal(0, RuntimeCounters.Read(RuntimeCounters.Counter.PlayerStep).Calls);
        }
        finally
        {
            RuntimeCounters.Disable();
        }
    }

    // Each counter is its own slot. They are indexed by enum, so an off-by-one here would report the
    // physics cost under the network's name and nobody would notice.
    [Test]
    public void CountersDoNotBleedIntoEachOther()
    {
        RuntimeCounters.ResetAndEnable();
        try
        {
            RuntimeCounters.RecordTicks(RuntimeCounters.Counter.NetworkClient, 700);

            Assert.Equal(1, RuntimeCounters.Read(RuntimeCounters.Counter.NetworkClient).Calls);
            foreach (RuntimeCounters.Counter other in new[]
                     {
                         RuntimeCounters.Counter.NetworkServer,
                         RuntimeCounters.Counter.PlayerPhysics,
                         RuntimeCounters.Counter.PlayerMoveAndSlide,
                         RuntimeCounters.Counter.PlayerStep,
                         RuntimeCounters.Counter.NavigationReconcile,
                         RuntimeCounters.Counter.ZombiesView,
                     })
                Assert.Equal(0, RuntimeCounters.Read(other).Calls);
        }
        finally
        {
            RuntimeCounters.Disable();
        }
    }

    // The Start/Record pair is what call sites actually use, so it gets its own pass over a real elapsed
    // interval rather than only the synthetic tick counts above.
    [Test]
    public void TheStartRecordPairMeasuresRealWork()
    {
        RuntimeCounters.ResetAndEnable();
        try
        {
            long started = RuntimeCounters.Start();
            Assert.NotEqual(0, started); // enabled: a real timestamp, not the disabled sentinel
            for (int i = 0; i < 200_000; i++)
                _ = i * 3;
            RuntimeCounters.Record(RuntimeCounters.Counter.NavigationReconcile, started);

            Assert.Equal(1, RuntimeCounters.Read(RuntimeCounters.Counter.NavigationReconcile).Calls);
        }
        finally
        {
            RuntimeCounters.Disable();
        }
    }

    // RSS is read from /proc/self/status where it exists and falls back to the runtime's own figure.
    // Deliberately asserted only as "a plausible positive number": pinning it tighter would be pinning
    // the allocator's behaviour, which is not what this reads.
    [Test]
    public void ResidentMemoryIsReported()
    {
        long rss = ProcessMemory.RssBytes();

        Assert.True(rss > 0, "no resident size was reported at all");
        Assert.True(rss > 1024 * 1024, $"{rss} bytes is too small to be a running engine");
    }

    // The memory trace is opt-in through an environment variable, because it samples every frame.
    [Test]
    public void TheMemoryTraceIsOptIn()
    {
        Assert.Null(MemoryTrace.Create()); // unset: nothing to run

        string previous = OS.GetEnvironment("UG_MEM_TRACE");
        OS.SetEnvironment("UG_MEM_TRACE", "1");
        try
        {
            MemoryTrace? trace = MemoryTrace.Create();
            Assert.NotNull(trace);
            trace!.Free();
        }
        finally
        {
            OS.SetEnvironment("UG_MEM_TRACE", previous);
        }
    }

    // A malformed interval is treated as "off" rather than as a default: a typo in a shell should not
    // silently switch on a per-frame sampler in a benchmark run.
    [Test]
    public void AnUnreadableTraceIntervalIsTreatedAsOff()
    {
        string previous = OS.GetEnvironment("UG_MEM_TRACE");
        try
        {
            foreach (string bad in new[] { "yes", "0", "-1", "nan" })
            {
                OS.SetEnvironment("UG_MEM_TRACE", bad);
                Assert.Null(MemoryTrace.Create());
            }
        }
        finally
        {
            OS.SetEnvironment("UG_MEM_TRACE", previous);
        }
    }

    // Enabled, it samples on its own frames rather than needing to be driven. The interval is asked for
    // in SECONDS, so this waits on the clock: counting frames would never reach one sample in headless,
    // where a frame is a fraction of a millisecond.
    [Test]
    public async Task TheMemoryTraceSamplesWhileItRuns()
    {
        string previous = OS.GetEnvironment("UG_MEM_TRACE");
        OS.SetEnvironment("UG_MEM_TRACE", "0.25"); // the floor Create clamps to
        try
        {
            MemoryTrace? trace = MemoryTrace.Create();
            Assert.NotNull(trace);
            TestScene.AddChild(trace!);

            ulong deadline = Time.GetTicksMsec() + 1500;
            while (Time.GetTicksMsec() < deadline)
                await NextFrame();

            trace!.QueueFree();
        }
        finally
        {
            OS.SetEnvironment("UG_MEM_TRACE", previous);
        }
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);
}
