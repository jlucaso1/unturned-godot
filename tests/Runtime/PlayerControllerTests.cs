using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The thing the player IS: a capsule, a camera, and a 12.5 Hz tick.
//
// Almost everything it does comes in through the keyboard and mouse, which is why so little of it had
// been pinned. But the input handler is a method — a mouse motion or a key press can be handed to it
// directly — and physics is a world, which a sandbox can supply. Between those two the controller is
// drivable without a person at the machine.
//
// Each of these builds its own world. A CharacterBody3D that falls through the shared test scene would
// change what every other physics query in the suite answers, and it would do it silently.
//
// One production seam exists for this file. Whether the player owns the controls is read from mouse
// capture, and a HEADLESS run cannot capture the mouse — the engine refuses without a window and leaves
// the mode Visible however it is set. Read straight, that gate makes the look, the stance keys, the
// perspective toggle, the attack latch and the whole movement path unreachable from the suite. The
// controller therefore asks an overridable question instead, and only a test answers it.
public class PlayerControllerTests : TestClass
{
    public PlayerControllerTests(Node testScene) : base(testScene) { }

    // A ready controller has the parts everything else assumes: a camera to render from, a capsule the
    // world can hit, and the player's own collision bit.
    [Test]
    public async Task AReadyControllerHasACameraACapsuleAndItsOwnCollisionBit()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        Assert.NotNull(world.Player.Camera);
        Assert.Equal(CollisionLayers.Player, world.Player.CollisionLayer);

        var capsule = Assert.IsType<CapsuleShape3D>(FirstChild<CollisionShape3D>(world.Player).Shape);
        Assert.Equal(PlayerConfig.Radius, capsule.Radius);
        Assert.Equal(PlayerConfig.HeightStand, capsule.Height);
    }

    // The player's own bit is not the one the world uses. It used to be, and the zombies' alert raycast —
    // which masks the vision-blocking bit — ended inside the player's own capsule at close range and
    // reported that it could not see them.
    [Test]
    public async Task ThePlayerIsNotGeometry()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        Assert.Equal(0u, world.Player.CollisionLayer & CollisionLayers.VisionBlocker);
    }

    // Gravity, and the floor. A player dropped above the ground lands on it and stays — which is also the
    // proof that the capsule, the mask and the world agree with each other.
    [Test]
    public async Task APlayerDroppedAboveTheGroundLandsOnIt()
    {
        using var world = new PlayerWorld(TestScene, spawn: new Vector3(0f, 3f, 0f));
        world.Player.MarkWorldReady();

        await world.Run(180);

        Assert.True(world.Player.IsOnFloor(), $"never landed; resting at {world.Player.Position}");
        Assert.True(Math.Abs(world.Player.Position.Y) < 0.2f,
            $"landed somewhere other than the floor: {world.Player.Position}");
    }

    // --- looking -------------------------------------------------------------------------------------

    // The mouse yaws the whole body and pitches only the head. Yawing the head instead would leave the
    // capsule — and everything the server is told about the player's facing — pointing the old way.
    [Test]
    public async Task TheMouseYawsTheBodyAndPitchesTheHead()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        world.Look(new Vector2(40f, 20f));

        Assert.NotEqual(0f, world.Player.Rotation.Y);
        Assert.NotEqual(0f, world.Head.RotationDegrees.X);
        // The body itself never tips: a leaning capsule would catch on geometry it should walk past.
        Assert.Equal(0d, world.Player.Rotation.X, 4);
    }

    // Look is clamped rather than free. Without the clamp the camera rolls past vertical and the view
    // comes back upside down — and the pitch is a byte on the wire, so an unclamped one wraps.
    [Test]
    public async Task LookIsClampedAtBothEnds()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        world.Look(new Vector2(0f, -100000f));   // straight up, and then some
        float up = world.Head.RotationDegrees.X;
        world.Look(new Vector2(0f, 200000f));    // and all the way back down
        float down = world.Head.RotationDegrees.X;

        (float limitDown, float limitUp) = PlayerConfig.PitchLimitsFor(EPlayerStance.Stand);
        Assert.Equal(limitUp, up, 3);
        Assert.Equal(limitDown, down, 3);
    }

    // Nothing looks around while a menu owns the mouse. The pause menu releases capture, and a controller
    // that kept reading motion would spin the view under the cursor the player is trying to click with.
    [Test]
    public async Task NothingLooksAroundWhileAMenuOwnsTheMouse()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        world.MenuTakesTheControls();
        world.Player._Input(Motion(new Vector2(40f, 20f)));

        Assert.Equal(0f, world.Player.Rotation.Y);
        Assert.Equal(0f, world.Head.RotationDegrees.X);
    }

    // --- what is drawn -------------------------------------------------------------------------------

    // First person draws no body. It briefly did, on the theory that the camera sits inside the head and
    // the skull's faces would cull — but the camera is at a fixed 1.75 m rather than at the rig's actual
    // skull, so what a player saw was their own chest filling the screen.
    [Test]
    public async Task FirstPersonDrawsNoBody()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        Assert.False(world.Body.Visible);
    }

    // Third person draws it, which is the same switch read the other way — and the mode a screenshot of
    // the character has to boot into, since there is no keyboard behind one.
    [Test]
    public async Task ThirdPersonDrawsTheBody()
    {
        using var world = new PlayerWorld(TestScene, thirdPerson: true);
        await world.Settle();

        Assert.True(world.Body.Visible);
    }

    // And the key swaps between them.
    [Test]
    public async Task ThePerspectiveKeySwapsWhatIsDrawn()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        world.Press(Key.F5);
        Assert.True(world.Body.Visible);

        world.Press(Key.F5);
        Assert.False(world.Body.Visible);
    }

    // --- stance --------------------------------------------------------------------------------------

    // Crouching lowers the eye. This is the one stance effect a player sees before they see anything
    // else, and it is applied on the tick rather than on the key so it can be refused under a ceiling.
    [Test]
    public async Task CrouchingLowersTheEye()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        float standing = world.Head.Position.Y;

        world.Press(PlayerSettings.Default.Crouch);
        await world.Run(30);

        Assert.True(world.Head.Position.Y < standing,
            $"the eye stayed at {world.Head.Position.Y} after crouching");
    }

    // Prone lowers it further still, and the two intents are exclusive: pressing prone while crouched
    // must not leave both set, or standing back up would take two presses.
    [Test]
    public async Task ProneIsLowerStillAndCancelsCrouch()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();

        world.Press(PlayerSettings.Default.Crouch);
        await world.Run(30);
        float crouching = world.Head.Position.Y;

        world.Press(PlayerSettings.Default.Prone);
        await world.Run(40);
        float prone = world.Head.Position.Y;

        Assert.True(prone < crouching, $"prone ({prone}) was not below crouched ({crouching})");

        // One press to stand back up, because the crouch intent was cleared rather than stacked.
        world.Press(PlayerSettings.Default.Prone);
        await world.Run(40);
        Assert.True(world.Head.Position.Y > crouching, "standing up from prone took more than one press");
    }

    // A controller can boot straight into a stance. There is no keyboard behind a screenshot, and crouch
    // and prone are exactly where the first-person rig's framing is most visibly wrong.
    [Test]
    public async Task AControllerCanBootStraightIntoAStance()
    {
        using var world = new PlayerWorld(TestScene, stance: EPlayerStance.Prone);
        await world.Land();
        await world.Run(40);

        Assert.True(world.Head.Position.Y < PlayerConfig.EyeHeightCrouch,
            $"booted into prone but the eye is at {world.Head.Position.Y}");
    }

    // Keys do nothing while a menu owns the mouse, for the same reason the look does: the player is
    // typing at something else.
    [Test]
    public async Task StanceKeysDoNothingWhileAMenuOwnsTheMouse()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        float standing = world.Head.Position.Y;

        world.MenuTakesTheControls();
        world.Player._Input(KeyPress(PlayerSettings.Default.Crouch));
        await world.Run(30);

        Assert.Equal(standing, world.Head.Position.Y, 3);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static InputEventMouseMotion Motion(Vector2 relative) => new() { Relative = relative };

    private static T FirstChild<T>(Node parent) where T : Node
    {
        foreach (Node child in parent.GetChildren())
            if (child is T match)
                return match;
        throw new InvalidOperationException($"no {typeof(T).Name} under {parent.Name}");
    }

    private static InputEventKey KeyPress(Key keycode) =>
        new() { Keycode = keycode, Pressed = true, Echo = false };

    // A controller standing on ground, in a world of its own.
    //
    // Mouse capture is global state the controller takes in _Ready — it is how it tells "the player is
    // playing" from "a menu is open" — so the harness restores whatever the rest of the suite had.
    private sealed class PlayerWorld : IDisposable
    {
        private readonly Node _testScene;
        private readonly PhysicsSandbox _sandbox;
        private bool _playerOwnsTheControls = true;

        public PlayerController Player { get; }

        public PlayerWorld(Node testScene, Vector3? spawn = null, bool thirdPerson = false,
            EPlayerStance stance = EPlayerStance.Stand)
        {
            _testScene = testScene;
            _sandbox = new PhysicsSandbox(testScene);
            PlayerController.OverrideInputOwnershipForTests(() => _playerOwnsTheControls);
            _sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));

            Player = new PlayerController
            {
                Name = "Player",
                // Half a metre up rather than exactly on the floor, and it matters. A body placed at the
                // surface never integrates — an idle grounded player deliberately skips collision — so it
                // stays EXACTLY on it, and the standing-headroom query, whose capsule starts at the
                // body's feet, then touches the ground and reports no room to stand. Landing under
                // gravity leaves the engine's own safe gap, which is where a real player always is.
                Position = spawn ?? new Vector3(0f, 0.5f, 0f),
                StartThirdPerson = thirdPerson,
                StartStance = stance,
            };
            _sandbox.Root.AddChild(Player);
        }

        // The head, which is the camera's parent: the body carries the yaw and the head the pitch.
        public Node3D Head => Player.Camera.GetParent<Node3D>();

        // The body model, which _Ready adds as a child of the controller. It is a placeholder figure
        // here — no character is imported in the suite — but the visibility switch is the real one.
        public Node3D Body
        {
            get
            {
                // _Ready adds the collider, then the model, then the head. The model is the first Node3D
                // that is not the collider — a placeholder figure here, since no character is imported in
                // the suite, but the visibility switch on it is the real one.
                foreach (Node child in Player.GetChildren())
                    if (child is Node3D node and not CollisionShape3D)
                        return node;
                throw new InvalidOperationException("the controller built no body model");
            }
        }

        public SignalAwaiter Settle() =>
            _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

        public async Task Run(int frames)
        {
            for (int i = 0; i < frames; i++)
                await Settle();
        }

        // Let the player fall onto the floor and come to rest there, as every session starts.
        public async Task Land()
        {
            Player.MarkWorldReady();
            for (int i = 0; i < 120 && !Player.IsOnFloor(); i++)
                await Settle();
            await Run(5);
        }

        public void Look(Vector2 relative) => Player._Input(Motion(relative));

        public void Press(Key keycode) => Player._Input(KeyPress(keycode));

        // What the pause menu does: takes the controls away without the controller knowing it exists.
        public void MenuTakesTheControls() => _playerOwnsTheControls = false;

        public void Dispose()
        {
            _sandbox.Dispose();
            // Left set, the next test in the process would run with this test's answer.
            PlayerController.OverrideInputOwnershipForTests(null);
        }
    }
}
