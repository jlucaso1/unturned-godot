using System.Reflection;
using Chickensoft.GoDotTest;
using Godot;

namespace UnturnedGodot.RuntimeTests;

// Entry point for the tests that need a live engine.
//
// The xUnit suite under tests/ references core/ alone — no Godot — which is what keeps it fast, parallel
// and runnable on a machine that has never seen the engine. That boundary has a cost: nothing in it can
// reach a Node. Node lifecycle is not incidental detail, it is behaviour with rules of its own, and it has
// already produced a shipped bug that no pure test could have caught (see CharacterSkeletonLifecycleTests).
// These tests close exactly that gap and nothing else: if a test does not need a real engine, it belongs in
// the xUnit suite, where it runs in milliseconds instead of a process launch.
//
// Run them with ./scripts/run-godot-tests.sh. This scene is not the project's main scene, and is only
// reached by naming it on the command line, so the game never carries the runner. The flags are parsed by
// GoDotTest out of OS.GetCmdlineArgs(), which is why they are passed BEFORE the engine's `--` separator,
// unlike the game's own --benchmark (OS.GetCmdlineUserArgs()).
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class RuntimeTestRunner : Node
{
    private ITestEnvironment _environment = default!;

    public override void _Ready()
    {
        _environment = TestEnvironment.From(OS.GetCmdlineArgs());
        if (!_environment.ShouldRunTests)
        {
            // Opening the scene without the flag would otherwise sit there doing nothing at all, which
            // reads as a hung test run rather than a missing argument.
            Log.PrintErr("[runtime-tests] This scene runs the Godot-runtime suite and needs --run-tests. "
                + "Use ./scripts/run-godot-tests.sh.");
            GetTree().Quit(1);
            return;
        }

        // Deferred so the run starts on a fully entered tree: the tests add nodes under this one, and a
        // node added during _Ready would be posting its own notifications inside this one's.
        CallDeferred(nameof(Run));
    }

    private void Run() => _ = GoTest.RunTests(Assembly.GetExecutingAssembly(), this, _environment);
}
