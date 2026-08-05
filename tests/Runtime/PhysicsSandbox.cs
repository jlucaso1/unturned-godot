using Godot;

namespace UnturnedGodot.RuntimeTests;

// A physics world of a test's own.
//
// Every test in this suite shares one scene, which is fine until one of them needs GEOMETRY. A test that
// puts ground and a wall in the shared world changes the answer every other physics query gets — and it
// does so silently, as a wrong result rather than an error. That really happened: adding a floor for a
// step-up test made two punch raycasts hit it instead of the bodies they were aimed at, and the punch
// tests failed in a file nobody had touched.
//
// A SubViewport owns its own World3D, so anything built inside one is invisible to everything outside it.
// That turns "does this test interfere with the others" from something to remember into something the
// type system arranges.
//
//     using var sandbox = new PhysicsSandbox(TestScene);
//     sandbox.AddBox(new Vector3(0, -0.5f, 0), new Vector3(40, 1, 40));
//     await sandbox.Settle();
//
// Disposing takes the whole world with it, so a test cannot leak geometry into the next one either.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class PhysicsSandbox : System.IDisposable
{
    private readonly Node _testScene;
    private readonly SubViewport _viewport;

    public PhysicsSandbox(Node testScene)
    {
        _testScene = testScene;
        _viewport = new SubViewport
        {
            // Its own World3D is the whole point; without this it would borrow the parent viewport's and
            // the isolation would be a comment rather than a fact.
            OwnWorld3D = true,
            // It is never displayed — nothing here renders — but a zero-sized viewport is not guaranteed
            // to run its physics, so give it something nominal.
            Size = new Vector2I(4, 4),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            PhysicsObjectPicking = false,
        };
        Root = new Node3D { Name = "SandboxWorld" };
        _viewport.AddChild(Root);
        testScene.AddChild(_viewport);
    }

    // Where a test builds. Everything added under this is inside the sandbox's own world.
    public Node3D Root { get; }

    public StaticBody3D AddBox(Vector3 centre, Vector3 size, uint layer = 1)
    {
        var solid = new StaticBody3D { Position = centre, CollisionLayer = layer };
        solid.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        Root.AddChild(solid);
        return solid;
    }

    // A physics step inside this world. Queries are only safe to make right after one, because the
    // project runs 3D physics on its own thread (project.godot: 3d/run_on_separate_thread).
    public SignalAwaiter Settle() =>
        _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

    public void Dispose()
    {
        _testScene.RemoveChild(_viewport);
        _viewport.Free(); // takes the world, its bodies and their RIDs with it
    }
}
