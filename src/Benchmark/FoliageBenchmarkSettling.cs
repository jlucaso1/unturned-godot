using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot.Benchmark;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class FoliageBenchmarkSettling
{
    private const int RequiredStableFrames = 3;
    private const ulong TimeoutUsec = 10_000_000;

    public static async Task<bool> WaitAsync(Node context, SceneTree tree)
    {
        var tracker = new FoliageSettlingTracker(RequiredStableFrames);
        ulong deadline = Time.GetTicksUsec() + TimeoutUsec;
        while (Time.GetTicksUsec() < deadline)
        {
            bool found = false;
            bool settled = true;
            foreach (Node node in tree.GetNodesInGroup("foliage_streaming"))
                if (node is FoliageStreamingRenderer foliage)
                {
                    found = true;
                    settled &= foliage.IsSettled;
                }
            if (tracker.Observe(found, settled))
                return true;
            await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
        Log.PushWarning("[benchmark] foliage streaming did not remain settled for three frames in 10 s; "
            + "residency counts are reported under their *Unsettled keys and describe a mid-fill state, "
            + "not the steady resident set");
        return false;
    }
}
