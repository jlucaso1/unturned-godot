using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Waiting for foliage streaming to settle before a benchmark takes its numbers.
//
// It exists because residency counts taken mid-fill describe a state nobody plays in: a run that sampled
// while chunks were still arriving would report a resident set that is neither the cold one nor the
// steady one, and the number would move between runs for reasons that have nothing to do with the change
// under test.
//
// The rule it applies is "settled for three consecutive frames", not "settled once" — streaming can
// report settled between two admissions and then keep going. And a scene with no foliage at all is
// settled by definition rather than something to wait ten seconds for, which is what the benchmark tiers
// that build no world need.
public class FoliageBenchmarkSettlingTests : TestClass
{
    public FoliageBenchmarkSettlingTests(Node testScene) : base(testScene) { }

    // No foliage renderer in the tree is settled immediately. A tier that builds no world would otherwise
    // pay the full timeout before every measurement, and the warning it prints would be noise rather than
    // a signal.
    [Test]
    public async Task ASceneWithNoFoliageIsSettledAtOnce()
    {
        ulong before = Time.GetTicksMsec();

        bool settled = await Benchmark.FoliageBenchmarkSettling.WaitAsync(TestScene, TestScene.GetTree());

        ulong took = Time.GetTicksMsec() - before;
        Assert.True(settled, "an empty scene was not treated as settled");
        // Immediately, not after the ten-second ceiling: the point is that it does not wait for something
        // that is never coming.
        Assert.True(took < 2000, $"it waited {took} ms for foliage that does not exist");
    }
}
