using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
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

    // --- moving ---------------------------------------------------------------------------------------

    // A held key moves the player. Movement is read as HELD state rather than latched as an edge — a
    // player walking forward is not sending events, they are simply still holding the key.
    [Test]
    public async Task AHeldKeyWalksThePlayerForward()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        Vector3 start = world.Player.Position;

        world.Hold(PlayerSettings.Default.Forward);
        await world.Run(40);
        world.Release(PlayerSettings.Default.Forward);

        // FORWARD, not merely somewhere. At the default yaw the controller's forward is -Z, so a key
        // wired to strafe or to back would satisfy a distance check while sending the player the wrong
        // way — and this is the test that owns that binding.
        Vector3 moved = world.Player.Position - start;
        Assert.True(moved.Z < -0.5f,
            $"holding forward moved the player by {moved}, which is not forward");
        Assert.True(MathF.Abs(moved.X) < MathF.Abs(moved.Z),
            $"holding forward moved the player mostly sideways: {moved}");
    }

    // Letting go stops them. A controller that kept the last direction would have players sliding away
    // from the keyboard.
    [Test]
    public async Task LettingGoStops()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();

        world.Hold(PlayerSettings.Default.Forward);
        await world.Run(20);
        world.Release(PlayerSettings.Default.Forward);
        await world.Run(20);

        Vector3 resting = world.Player.Position;
        await world.Run(20);

        Assert.True(world.Player.Position.DistanceTo(resting) < 0.1f,
            $"the player kept moving after the key came up: {resting} -> {world.Player.Position}");
    }

    // Nothing moves while a menu owns the controls, however held the key is. The pause menu does not
    // release the keyboard — it takes the input — so a controller that polled it anyway would walk the
    // player around behind the menu.
    [Test]
    public async Task NothingMovesWhileAMenuOwnsTheControls()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        Vector3 start = world.Player.Position;

        world.Hold(PlayerSettings.Default.Forward);
        world.MenuTakesTheControls();
        await world.Run(40);
        world.Release(PlayerSettings.Default.Forward);

        Assert.True(world.Player.Position.DistanceTo(start) < 0.1f,
            $"the player walked while a menu was open: {start} -> {world.Player.Position}");
    }

    // Sprinting is faster than walking, and it is a stance rather than a modifier — which is why the
    // camera widens with it and why it can be refused the same way crouching can.
    [Test]
    public async Task SprintingCoversMoreGroundThanWalking()
    {
        float walked = await Distance(sprinting: false);
        float sprinted = await Distance(sprinting: true);

        Assert.True(sprinted > walked, $"sprinting covered {sprinted} against walking's {walked}");
    }

    private async Task<float> Distance(bool sprinting)
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        Vector3 start = world.Player.Position;

        world.Hold(PlayerSettings.Default.Forward);
        if (sprinting)
            world.Hold(PlayerSettings.Default.Sprint);
        await world.Run(40);
        world.Release(PlayerSettings.Default.Forward);
        if (sprinting)
            world.Release(PlayerSettings.Default.Sprint);

        return world.Player.Position.DistanceTo(start);
    }

    // --- the hands ------------------------------------------------------------------------------------

    // A click with no session behind it is spent harmlessly.
    //
    // This is the whole claim, and it is smaller than it looks: a player clicking during the loading
    // screen, or after a host has gone away, has a controller whose Net is null — and the swing goes out
    // on an input datagram there is nothing to send. That the LATCH works is covered by
    // InASessionASwingIsAnnounced, which can see the wire; asserting the local body's floor state here
    // proved nothing, because Land() already made it true before the click.
    [Test]
    public async Task AClickWithNoSessionIsSpentHarmlessly()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();

        Assert.Null(world.Player.Net);
        world.Click();
        await world.Run(40);

        // Still standing, still simulating: the tick that consumed the latch did not take the controller
        // with it. Anything stronger needs a session, and that test exists.
        Assert.True(world.Player.IsOnFloor());
        Assert.True(world.Player.IsInsideTree());
    }

    // A click while a menu owns the controls is not a punch. The pause menu is full of things to click
    // on, and every one of them would otherwise also swing the player's fist.
    //
    // Observed on the WIRE, like the swing above. Without a session there is nothing that can tell a
    // click that was correctly ignored from one that was latched and thrown.
    [Test]
    public async Task AClickOnAMenuIsNotAPunch()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        using var session = new Loopback();
        world.Player.Net = session.Client;

        for (int i = 0; i < 40 && !session.Client.Joined; i++)
        {
            session.Pump();
            await world.Run(1);
        }

        Assert.True(session.Client.Joined, "the loopback session never admitted the player");
        int before = session.Swings;

        world.MenuTakesTheControls();
        world.Click();
        for (int i = 0; i < 60; i++)
        {
            session.Pump();
            await world.Run(1);
        }

        Assert.Equal(before, session.Swings);
    }

    // In a session, a swing goes out on the wire. It is announced on more than one input frame, because
    // input datagrams are unreliable and a single dropped one would eat the swing for everyone else while
    // the thrower saw their own — the one desync a locally-predicted action can produce.
    [Test]
    public async Task InASessionASwingIsAnnounced()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Land();
        using var session = new Loopback();
        world.Player.Net = session.Client;

        // Let the handshake finish before swinging: an input frame sent before admission is not a swing
        // anyone refused, it is a swing nobody was listening for.
        for (int i = 0; i < 40 && !session.Client.Joined; i++)
        {
            session.Pump();
            await world.Run(1);
        }

        Assert.True(session.Client.Joined, "the loopback session never admitted the player");
        int before = session.Swings;

        world.Click();
        for (int i = 0; i < 60 && session.Swings == before; i++)
        {
            session.Pump();
            await world.Run(1);
        }

        // The SERVER saw it. Asserting the local body's floor state proved nothing about the wire — it
        // was already true before the session was attached — and a controller that latched the click and
        // never announced it is exactly the desync this path exists to prevent.
        Assert.True(session.Swings > before,
            "the click never reached the server as a swing");
    }

    // A swing thrown inside a safezone that forbids weapons never happens. PlayerEquipment has had the
    // gate since the safezone volumes were parsed, and nothing reached it: the controller built its
    // HandState out of the stance alone, so `isSafe` was false everywhere and the fist swung in every
    // town on the map.
    //
    // Observed on the WIRE, like the menu case above: without a session there is nothing that tells a
    // click correctly refused from one latched and thrown.
    [Test]
    public async Task NoPunchInsideASafezoneThatForbidsWeapons() =>
        Assert.Equal(0, await SwingsInSafezone(noWeapons: true));

    // And the same volume with weapons allowed is not a gate at all — the flag is the rule, not the
    // zone. A controller that refused every safezone would be just as wrong.
    [Test]
    public async Task ASafezoneThatAllowsWeaponsDoesNotStopThePunch() =>
        Assert.True(await SwingsInSafezone(noWeapons: false) > 0,
            "a safezone that allows weapons swallowed the swing");

    private async Task<int> SwingsInSafezone(bool noWeapons)
    {
        var nodes = new LevelNodeSet();
        // Centred on the spawn, so the player is standing in it. The radius is the file's own 0..1
        // slider and zero is already a 16-metre sphere, which is the ordinary safezone shape; isHeight
        // would make it the paintball box instead.
        nodes.Safezones.Add(new SafezoneNode(Vector3.Zero, 0f, isHeight: false, noWeapons,
            noBuildables: false));

        using var world = new PlayerWorld(TestScene, nodes: nodes);
        await world.Land();
        using var session = new Loopback();
        world.Player.Net = session.Client;

        for (int i = 0; i < 40 && !session.Client.Joined; i++)
        {
            session.Pump();
            await world.Run(1);
        }

        Assert.True(session.Client.Joined, "the loopback session never admitted the player");

        world.Click();
        // Accumulated rather than read at the end: the server's gesture list is refilled by every step,
        // so it holds the swings of THAT tick alone and is empty again on the next one.
        int swings = 0;
        for (int i = 0; i < 60; i++)
        {
            session.Pump();
            swings += session.Swings;
            await world.Run(1);
        }

        return swings;
    }

    // The map's own Max_Walkable_Slope reaches the body that has to obey it. Every consumer used to read
    // the 59-degree constant, so a map asking for anything else was ignored — and this is the seam the
    // resolved value arrives through.
    [Test]
    public async Task TheMapsWalkableSlopeReachesTheBody()
    {
        using var world = new PlayerWorld(TestScene, maxWalkableSlopeDegrees: 40f);
        await world.Settle();

        Assert.Equal(Mathf.DegToRad(40f), world.Player.FloorMaxAngle, 4);
    }

    // A controller told nothing about the map keeps the game's own default, which is what a map that
    // leaves Max_Walkable_Slope at its -1 sentinel resolves to.
    [Test]
    public async Task WithNoMapConfigTheDefaultSlopeStands()
    {
        using var world = new PlayerWorld(TestScene);
        await world.Settle();

        Assert.Equal(Mathf.DegToRad(PlayerConfig.MaxWalkableSlopeDegrees),
            world.Player.FloorMaxAngle, 4);
        Assert.Equal(PlayerConfig.MaxWalkableSlopeDegrees,
            PlayerConfig.ResolveMaxWalkableSlope(LevelConfigData.Default.MaxWalkableSlope));
    }

    // --- ice ------------------------------------------------------------------------------------------
    //
    // The one shipped surface that declares Character_Friction_Mode Custom. Half the deceleration and
    // 1.2x the max speed, resolved here as a constant rather than off the install: what is being tested
    // is that the CONTROLLER consults the surface at all, not that Ice.asset says what it says (which
    // tests/Player/GroundFrictionTests.cs asserts against the real file).
    private static readonly UnturnedGodot.Assets.CharacterFrictionProperties Ice =
        new(UnturnedGodot.Assets.EPhysicsMaterialCharacterFrictionMode.Custom, 1f, 0.5f, 1.2f);

    // Ice ramps up instead of snapping to walking speed, so the first quarter-second of a walk covers
    // less ground than it does on concrete.
    [Test]
    public async Task IceAcceleratesRatherThanSnappingToSpeed()
    {
        float onConcrete = await WalkedIn(15, friction: null);
        float onIce = await WalkedIn(15, friction: _ => Ice);

        Assert.True(onIce < onConcrete * 0.75f,
            $"ice covered {onIce} m against concrete's {onConcrete} m, which is not a ramp");
    }

    // ...and it keeps the velocity after the key comes up, which is the whole of what sliding IS. The
    // grounded body is otherwise allowed to skip collision integration entirely on an idle tick — true
    // of every surface that responds immediately, and the one thing that would freeze a slide dead.
    [Test]
    public async Task IceKeepsSlidingAfterTheKeyComesUp()
    {
        using var world = new PlayerWorld(TestScene, friction: _ => Ice);
        await world.Land();

        world.Hold(PlayerSettings.Default.Forward);
        await world.Run(60);
        world.Release(PlayerSettings.Default.Forward);
        await world.Run(2); // the release has to be seen before the distance is measured

        Vector3 released = world.Player.Position;
        await world.Run(20);

        Assert.True(world.Player.Position.DistanceTo(released) > 0.5f,
            $"the slide stopped dead at the key: {released} -> {world.Player.Position}");
        // And it does stop, rather than sliding for ever: the deceleration is floored at the desired
        // speed, so the velocity reaches exactly zero.
        await world.Run(400);
        Vector3 rested = world.Player.Position;
        await world.Run(20);
        Assert.True(world.Player.Position.DistanceTo(rested) < 0.01f,
            $"the slide never came to rest: {rested} -> {world.Player.Position}");
    }

    private async Task<float> WalkedIn(int frames,
        Func<Vector3, UnturnedGodot.Assets.CharacterFrictionProperties>? friction)
    {
        using var world = new PlayerWorld(TestScene, friction: friction);
        await world.Land();
        Vector3 start = world.Player.Position;

        world.Hold(PlayerSettings.Default.Forward);
        await world.Run(frames);
        world.Release(PlayerSettings.Default.Forward);

        return world.Player.Position.DistanceTo(start);
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

    // A loopback session for the controller to announce into.
    private sealed class Loopback : IDisposable
    {
        private readonly LoopbackServerTransport _transport = new();
        private readonly NetServer _server;
        private double _now = 1000.0;

        public NetClient Client { get; }

        public Loopback()
        {
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(Ground)), Vector3.Zero, "PEI");
            Client = new NetClient(_transport.CreateClient(), "Local", "PEI");
        }

        public void Pump()
        {
            _now += ServerSimulation.TickRate;
            _server.Update(_now);
            Client.Update(_now);
        }

        // Swings the SERVER accepted. The controller announces one on several input frames because
        // datagrams are unreliable, and the server recognises the repeats as the same swing — so this
        // counts events rather than packets, which is what a second player would see.
        public int Swings => _server.Gestures.Count;

        private static bool Ground(float x, float z, out float y)
        {
            y = 0f;
            return true;
        }

        public void Dispose()
        {
        }
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
            EPlayerStance stance = EPlayerStance.Stand, LevelNodeSet? nodes = null,
            float? maxWalkableSlopeDegrees = null,
            Func<Vector3, UnturnedGodot.Assets.CharacterFrictionProperties>? friction = null)
        {
            _testScene = testScene;
            _sandbox = new PhysicsSandbox(testScene);
            PlayerController.OverrideInputOwnershipForTests(() => _playerOwnsTheControls);
            _sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));

            Player = new PlayerController
            {
                Name = "Player",
                // The map's own data, which a session reads off the level and a test states outright:
                // the safezone volumes the hands obey, and the slope limit the body stands by.
                Nodes = nodes,
                MaxWalkableSlopeDegrees = maxWalkableSlopeDegrees ?? PlayerConfig.MaxWalkableSlopeDegrees,
                SurfaceFriction = friction,
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

        public void Click() => Player._Input(new InputEventMouseButton
        {
            ButtonIndex = PlayerSettings.Default.AttackPrimary,
            Pressed = true,
        });

        // Movement is polled as held state rather than latched from events, so a test has to make the
        // key genuinely held: Input.ParseInputEvent is what puts it into the engine's own key state,
        // which is where the controller reads it from.
        public void Hold(Key keycode)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = keycode, PhysicalKeycode = keycode, Pressed = true });
            _held.Add(keycode);
        }

        public void Release(Key keycode)
        {
            Input.ParseInputEvent(new InputEventKey { Keycode = keycode, PhysicalKeycode = keycode, Pressed = false });
            _held.Remove(keycode);
        }

        private readonly System.Collections.Generic.HashSet<Key> _held = new();

        // What the pause menu does: takes the controls away without the controller knowing it exists.
        public void MenuTakesTheControls() => _playerOwnsTheControls = false;

        public void Dispose()
        {
            // A key left down would still be down for the next test in the process.
            foreach (Key keycode in new System.Collections.Generic.List<Key>(_held))
                Release(keycode);
            _sandbox.Dispose();
            // Left set, the next test in the process would run with this test's answer.
            PlayerController.OverrideInputOwnershipForTests(null);
        }
    }
}
