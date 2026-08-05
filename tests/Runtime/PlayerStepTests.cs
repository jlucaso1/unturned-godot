using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Stepping over a kerb.
//
// Unity's CharacterController lifts the capsule over obstacles up to its step offset as part of the move;
// Godot's MoveAndSlide has no equivalent and simply stops at the ledge. This restores that, and it can
// only be tested against a real physics world — the whole method is TestMove against solid geometry.
//
// Which is exactly why it runs in a PhysicsSandbox. Building ground and a wall in the shared test scene
// changed the answer other tests' raycasts got: the first version of this file made two punch tests fail
// by giving their rays a floor to hit. Each test here owns its world instead.
//
// The decision the step makes is three-part, and each part is a way to break the world if it is wrong:
// headroom (or the body climbs into a ceiling), clear space once raised (or it phases through the riser),
// and something solid to land on within the step height (or it floats over a gap).
//
// What is covered here is the REFUSALS — no intent, a wall too tall, nothing in the way. The successful
// climb is not: reproducing it needs the capsule to end a slide genuinely stopped against the kerb, and
// the fixture I wrote settles somewhere the step pass correctly declines. That is a gap in the fixture
// rather than in the code, and asserting around it would prove nothing.
public class PlayerStepTests : TestClass
{
    public PlayerStepTests(Node testScene) : base(testScene) { }

    // No horizontal intent, no step — the common case on every frame a player stands or falls, which is
    // why it returns before crossing into the physics server at all.
    [Test]
    public async Task StandingStillNeverSteps()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        CharacterBody3D body = Capsule(sandbox);
        await sandbox.Settle();

        Assert.False(PlayerStep.TryStepUp(body, body.GlobalPosition, Vector3.Zero));
    }

    // A wall taller than the step height is not climbed. That bound is what stops the step pass from
    // becoming a way to walk up buildings.
    [Test]
    public async Task AWallTallerThanTheStepIsNotClimbed()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        CharacterBody3D body = Capsule(sandbox);
        float wall = PlayerConfig.StepOffset * 6f;
        sandbox.AddBox(new Vector3(0f, wall * 0.5f, -1.5f), new Vector3(4f, wall, 1f));
        await sandbox.Settle();

        Vector3 before = body.GlobalPosition;
        Vector3 wanted = Walk(body, new Vector3(0f, 0f, -1f));

        Assert.False(PlayerStep.TryStepUp(body, before, wanted));
        Assert.True(body.GlobalPosition.Y < before.Y + 0.05f,
            "the body rose against a wall it cannot climb");
    }

    // Nothing in the way is not a step either: with the motion fully delivered there is no shortfall to
    // complete, and stepping anyway would travel the same distance twice.
    [Test]
    public async Task WalkingOnFlatGroundNeverSteps()
    {
        using var sandbox = new PhysicsSandbox(TestScene);
        CharacterBody3D body = Capsule(sandbox);
        await sandbox.Settle();

        Vector3 before = body.GlobalPosition;
        Vector3 wanted = Walk(body, new Vector3(0f, 0f, -1f));

        Assert.False(PlayerStep.TryStepUp(body, before, wanted));
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A capsule standing on ground, which is the least world this can be asked about.
    private static CharacterBody3D Capsule(PhysicsSandbox sandbox)
    {
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));

        var body = new CharacterBody3D
        {
            Position = new Vector3(0f, 0.05f, 0f),
            FloorMaxAngle = Mathf.DegToRad(PlayerConfig.MaxWalkableSlopeDegrees),
        };
        body.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = PlayerConfig.Radius, Height = PlayerConfig.HeightStand },
            Position = Vector3.Up * (PlayerConfig.HeightStand * 0.5f),
        });
        sandbox.Root.AddChild(body);
        return body;
    }

    // One slide of the wanted horizontal motion, which is what the step pass is meant to be called after.
    private static Vector3 Walk(CharacterBody3D body, Vector3 wanted)
    {
        body.Velocity = wanted / (float)body.GetPhysicsProcessDeltaTime();
        body.MoveAndSlide();
        return wanted;
    }
}
