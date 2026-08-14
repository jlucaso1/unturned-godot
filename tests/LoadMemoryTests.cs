using System;
using System.Runtime.InteropServices;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// The post-load compaction.
//
// A cold load grows the heap by hundreds of megabytes it never needs again — decoded bundles, transient
// buffers, the parsed pools everything was built from. Without a compaction the process keeps every
// segment that work grew, for the whole session.
//
// The pass count is where the interesting behaviour is, and it is not a shipped setting: zero is an A/B
// control that prices the compaction itself, which matters most for the reclaims that land after the
// player is already moving. What these hold it to is that every configured value is survivable and
// bounded, because this runs in the middle of a load and a throw here would take the load with it.
//
// Nothing asserts how much memory came back. That is the allocator's business, not this code's, and a
// test that pinned it would fail on a different runtime rather than on a regression.
[Collection(ProcessStateCollection.Name)]
public class LoadMemoryTests
{
    // The default: one pass, and it reports what it did rather than compacting silently.
    [Fact]
    public void ReclaimingRunsAndSaysWhatItDid()
    {
        var log = WithPasses(null, () => LoadMemory.Reclaim("test"));

        // The line only appears where /proc/self/status does; elsewhere RSS is unavailable and the
        // reclaim deliberately says nothing rather than printing a zero it did not measure.
        if (LoadMemory.ProcessRssMib() > 0)
            Assert.Contains(log.Prints, line => line.Contains("test reclaim", StringComparison.Ordinal));
    }

    // Zero passes is the control, and it must be a real skip: the point of it is to measure a session that
    // did NOT compact, so a zero that quietly compacted anyway would make the comparison meaningless. The
    // skip says so on its way out, because a control that looked like a normal run would be unreadable.
    [Fact]
    public void ZeroPassesIsARealSkipAndSaysSo()
    {
        var log = WithPasses("0", () => LoadMemory.Reclaim("skipped"));

        Assert.Contains(log.Prints, line => line.Contains("skipped (UG_RECLAIM_PASSES=0)", StringComparison.Ordinal));
    }

    // Two passes is the other end. It exists because CompactOnce applies to the next blocking gen-2
    // collection and then resets itself — so the second pass has to re-arm it, or it would be measuring a
    // collection that left the large object heap alone rather than the two compactions it claims.
    [Fact]
    public void TwoPassesRunsAndReportsTwo()
    {
        var log = WithPasses("2", () => LoadMemory.Reclaim("twice"));

        if (LoadMemory.ProcessRssMib() > 0)
            Assert.Contains(log.Prints, line => line.Contains("2 pass(es)", StringComparison.Ordinal));
    }

    // A number outside the range is clamped to an end of it rather than trusted: a mistyped value must
    // not ask for twenty blocking gen-2 collections in the middle of a load. Note which end -4 lands on —
    // the low one is the skip, so a negative asks for no compaction at all. That is the clamp doing what
    // it says, and it is worth pinning because "UG_RECLAIM_PASSES=-1" reads like "default" and is not.
    [Theory]
    [InlineData("99", 2)]
    [InlineData("3", 2)]
    [InlineData("-4", 0)]
    public void ANumberOutsideTheRangeIsClampedToAnEndOfIt(string value, int expected)
    {
        var log = WithPasses(value, () => LoadMemory.Reclaim($"clamped-{value}"));

        AssertRanWith(log, expected);
    }

    // Something that is not a number at all falls back to the default rather than to either end: a typo
    // must not silently turn the compaction off, which is exactly what the low clamp would do.
    [Theory]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("two")]
    public void AnUnreadableValueFallsBackToOnePass(string value)
    {
        var log = WithPasses(value, () => LoadMemory.Reclaim($"unreadable-{value}"));

        AssertRanWith(log, 1);
    }

    private static void AssertRanWith(RecordingHostLog log, int passes)
    {
        if (passes == 0)
        {
            Assert.Contains(log.Prints,
                line => line.Contains("skipped (UG_RECLAIM_PASSES=0)", StringComparison.Ordinal));
            return;
        }

        Assert.DoesNotContain(log.Prints,
            line => line.Contains("UG_RECLAIM_PASSES=0", StringComparison.Ordinal));
        if (LoadMemory.ProcessRssMib() > 0)
            Assert.Contains(log.Prints, line => line.Contains($"{passes} pass(es)", StringComparison.Ordinal));
    }

    // RSS is a diagnostic read from /proc, and it answers 0 rather than throwing where that does not
    // exist. On Linux it should be a plausible positive number.
    [Fact]
    public void ResidentSizeIsReadableOrZero()
    {
        long rss = LoadMemory.ProcessRssMib();

        Assert.True(rss >= 0, "resident size came back negative");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Assert.True(rss > 0, "on Linux /proc/self/status should have answered");
    }

    private static RecordingHostLog WithPasses(string? value, Action body)
    {
        var log = new RecordingHostLog();
        string? previousValue = Environment.GetEnvironmentVariable("UG_RECLAIM_PASSES");
        IHostLog previousSink = HostLog.Sink;
        Environment.SetEnvironmentVariable("UG_RECLAIM_PASSES", value);
        HostLog.Sink = log;
        try
        {
            body();
        }
        finally
        {
            HostLog.Sink = previousSink;
            Environment.SetEnvironmentVariable("UG_RECLAIM_PASSES", previousValue);
        }

        return log;
    }
}
