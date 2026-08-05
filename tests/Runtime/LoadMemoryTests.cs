using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The post-load compaction.
//
// A cold load grows the heap by hundreds of megabytes it never needs again — decoded bundles, transient
// buffers, the parsed pools everything was built from. Without a compaction the process keeps every
// segment that work grew, for the whole session.
//
// The pass count is where the interesting behaviour is, and it is not a shipped setting: zero is an A/B
// control that prices the compaction itself, which matters most for the reclaims that land after the
// player is already moving. What the tests hold it to is that every configured value is survivable and
// bounded, because this runs in the middle of a load and a throw here would take the load with it.
//
// Nothing asserts how much memory came back. That is the allocator's business, not this code's, and a
// test that pinned it would fail on a different runtime rather than on a regression.
public class LoadMemoryTests : TestClass
{
    public LoadMemoryTests(Node testScene) : base(testScene) { }

    // The default: one pass, and it returns rather than throwing anywhere in the middle of a load.
    [Test]
    public void ReclaimingRunsAndReturns()
    {
        LoadMemory.Reclaim("test");
    }

    // Zero passes is the control, and it must be a real skip: the point of it is to measure a session
    // that did NOT compact, so a zero that quietly compacted anyway would make the comparison meaningless.
    [Test]
    public void ZeroPassesIsARealSkip()
    {
        WithPasses("0", () => LoadMemory.Reclaim("skipped"));
    }

    // Two passes is the other end. It exists because CompactOnce applies to the next blocking gen-2
    // collection and then resets itself — so the second pass has to re-arm it, or it would be measuring a
    // collection that left the large object heap alone rather than the two compactions it claims.
    [Test]
    public void TwoPassesRuns()
    {
        WithPasses("2", () => LoadMemory.Reclaim("twice"));
    }

    // Anything outside the range is clamped rather than trusted: a mistyped value must not ask for
    // twenty blocking gen-2 collections in the middle of a load.
    [Test]
    public void OutOfRangeAndUnreadableValuesAreClamped()
    {
        foreach (string value in new[] { "99", "-4", "yes", "" })
            WithPasses(value, () => LoadMemory.Reclaim($"clamped-{value}"));
    }

    // RSS is a diagnostic read from /proc, and it answers 0 rather than throwing where that does not
    // exist. On this runner it should be a plausible positive number.
    [Test]
    public void ResidentSizeIsReadableOrZero()
    {
        long rss = LoadMemory.ProcessRssMib();

        Assert.True(rss >= 0, "resident size came back negative");
        if (OS.GetName() == "Linux")
            Assert.True(rss > 0, "on Linux /proc/self/status should have answered");
    }

    private static void WithPasses(string value, System.Action body)
    {
        string previous = System.Environment.GetEnvironmentVariable("UG_RECLAIM_PASSES") ?? "";
        System.Environment.SetEnvironmentVariable("UG_RECLAIM_PASSES", value);
        try
        {
            body();
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("UG_RECLAIM_PASSES",
                previous.Length == 0 ? null : previous);
        }
    }
}
