using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Damage;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The two physics queries the punch host cannot make for itself, against the SERVER's own world.
//
// Everything about what a punch DOES — the damage table, the limb scaling, what counts as destroyed — is
// pure and tested in core/. What can only be tested here is the query: whether the ray reaches the right
// bodies, and whether the thing it hit can be named.
//
// Naming is the part that has actually broken. Object collision lives on server-owned bodies whose NAME
// carries the asset GUID, and there are two shapes of body because ObjectsBuilder builds either: normally
// one batched InstancedStaticBodies where the query's RID says which name was struck, and under
// UG_NODE_PHYSICS=1 a node per collider where the node's own name is the answer. Reading only the batched
// form left every breakable unpunchable in the other mode — the hit came back unnamed, so the damage
// ledger refused it as terrain.
public class PunchPhysicsTests : TestClass
{
    public PunchPhysicsTests(Node testScene) : base(testScene) { }

    // The mask is DamageTool.raycast's DAMAGE_SERVER reduced to this port's layers: the solid world plus
    // the medium furniture a swing can break, and deliberately NOT the player bit. A punch has to reach
    // past the thrower's own capsule, which sits a metre closer than anything it could hit.
    [Test]
    public void TheDamageMaskReachesTheWorldAndFurnitureButNotPlayers()
    {
        Assert.NotEqual(0u, PunchPhysics.DamageMask & CollisionLayers.World);
        Assert.NotEqual(0u, PunchPhysics.DamageMask & CollisionLayers.MediumFurniture);
        Assert.Equal(0u, PunchPhysics.DamageMask & CollisionLayers.Player);
    }

    [Test]
    public void AttachingToNothingIsRefused()
    {
        var harness = new Harness();
        Assert.Throws<ArgumentNullException>(() => PunchPhysics.Attach(null!, () => null));
        Assert.Throws<ArgumentNullException>(() => PunchPhysics.Attach(harness.Punches, null!));
    }

    // A session that rebuilt its world — a level reload — must not keep raycasting against the space it
    // left, which is why the world is resolved per swing rather than captured once. With nothing to
    // resolve, the query is a miss rather than a throw.
    [Test]
    public void WithNoWorldToResolveTheRayIsSimplyAMiss()
    {
        var harness = new Harness();
        PunchPhysics.Attach(harness.Punches, () => null);

        Assert.False(harness.Punches.WorldRaycast!(Vector3.Zero, Vector3.Forward, 4f,
            out _, out _, out _));
    }

    // The batched body: one InstancedStaticBodies owns every collider, and the query's RID is what says
    // which of its names was struck.
    [Test]
    public async Task ABatchedHitResolvesToItsAssetGuid()
    {
        var harness = new Harness();
        Guid asset = Guid.NewGuid();
        var bodies = new InstancedStaticBodies { Name = "ObjectCollision" };
        bodies.Add(ObjectCollisionNames.For(asset),
            new Shape3D[] { new BoxShape3D { Size = Vector3.One * 2f } },
            new[] { (0, new Transform3D(Basis.Identity, new Vector3(0f, 0f, -4f))) },
            CollisionLayers.World);
        TestScene.AddChild(bodies);
        await NextPhysicsFrame();

        PunchPhysics.Attach(harness.Punches, () => bodies.GetWorld3D());
        bool hit = harness.Punches.WorldRaycast!(Vector3.Zero, Vector3.Forward, 8f,
            out Vector3 point, out float distance, out Guid struck);

        Assert.True(hit, "the ray never reached the batched body");
        Assert.Equal(asset, struck);
        Assert.True(distance > 0f);
        Assert.True(point.Z < 0f, $"the hit was behind the swing: {point}");

        bodies.QueueFree();
    }

    // The node-per-collider mode (UG_NODE_PHYSICS=1), where the node's own name is the answer. This is
    // the case that was missing: reading only the batched form left every breakable unpunchable here.
    [Test]
    public async Task AHitOnANodePerColliderBodyResolvesToo()
    {
        var harness = new Harness();
        Guid asset = Guid.NewGuid();
        var body = new InstancedStaticBody
        {
            Name = ObjectCollisionNames.For(asset),
            Shapes = new Shape3D[] { new BoxShape3D { Size = Vector3.One * 2f } },
            Placements = new[] { (0, new Transform3D(Basis.Identity, new Vector3(0f, 0f, -4f))) },
            CollisionLayer = CollisionLayers.World,
        };
        TestScene.AddChild(body);
        await NextPhysicsFrame();

        PunchPhysics.Attach(harness.Punches, () => body.GetWorld3D());

        Assert.True(harness.Punches.WorldRaycast!(Vector3.Zero, Vector3.Forward, 8f,
            out _, out _, out Guid struck));
        Assert.Equal(asset, struck);

        body.QueueFree();
    }

    // Terrain and anything else nobody named resolve to Guid.Empty rather than to a wrong asset — the
    // punch has to damage the thing it hit, not whatever breakable happens to stand nearby.
    [Test]
    public async Task AnUnnamedBodyResolvesToNoAssetAtAll()
    {
        var harness = new Harness();
        var terrain = new StaticBody3D { Name = "Terrain", CollisionLayer = CollisionLayers.World };
        terrain.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One * 2f } });
        terrain.Position = new Vector3(0f, 0f, -4f);
        TestScene.AddChild(terrain);
        await NextPhysicsFrame();

        PunchPhysics.Attach(harness.Punches, () => terrain.GetWorld3D());

        Assert.True(harness.Punches.WorldRaycast!(Vector3.Zero, Vector3.Forward, 8f,
            out _, out _, out Guid struck));
        Assert.Equal(Guid.Empty, struck);

        terrain.QueueFree();
    }

    // Nothing in the way is a clean miss, and the out parameters come back neutral rather than carrying
    // the last swing's values.
    [Test]
    public async Task AnEmptySwingMissesCleanly()
    {
        var harness = new Harness();
        var far = new Node3D();
        TestScene.AddChild(far);
        await NextPhysicsFrame();

        PunchPhysics.Attach(harness.Punches, () => far.GetWorld3D());

        Assert.False(harness.Punches.WorldRaycast!(new Vector3(0f, 500f, 0f), Vector3.Up, 4f,
            out Vector3 point, out float distance, out Guid struck));
        Assert.Equal(Vector3.Zero, point);
        Assert.Equal(0f, distance);
        Assert.Equal(Guid.Empty, struck);

        far.QueueFree();
    }

    // LineBlocked is deliberately left unset. Where the cast works it already shortens the punch to
    // whatever solid thing it met first — so a zombie behind a wall is out of REACH rather than merely
    // occluded, and a second query would only be another chance to disagree with the first.
    [Test]
    public void TheOcclusionQueryIsDeliberatelyLeftUnset()
    {
        var harness = new Harness();
        PunchPhysics.Attach(harness.Punches, () => null);

        Assert.Null(harness.Punches.LineBlocked);
    }

    // PUNCH_LOG=1 is how a session says out loud that a fist connected. The damage model has no HUD in
    // front of it yet — no hit marker, no health bar, no ragdoll — so without it a swing that resolved and
    // a swing that did nothing look identical from outside the process.
    //
    // Driven through a real server tick rather than by raising the event, because the log is attached BY
    // Attach: a test that raised it itself would still pass if Attach had stopped subscribing.
    //
    // Only the miss branch is exercised here. Landing a punch needs the zombie simulation posed exactly
    // so, which core's PunchDamageHostTests already does at length against the resolver — duplicating
    // that setup here would be testing the damage model a second time rather than testing this log.
    [Test]
    public void TheSwingLogRunsOverAResolvedSwing()
    {
        var harness = new Harness();
        PunchPhysics.Attach(harness.Punches, () => null);

        string previous = OS.GetEnvironment("PUNCH_LOG");
        OS.SetEnvironment("PUNCH_LOG", "1");
        try
        {
            harness.Pump(4); // the client has to finish joining before an input frame is accepted
            harness.Swing(yaw: 0f);
            harness.Pump(4);
        }
        finally
        {
            OS.SetEnvironment("PUNCH_LOG", previous);
        }

        Assert.NotEmpty(harness.Results);
        Assert.Contains(harness.Results, r => !r.Hit.Exists);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A minimal loopback server with a zombie population, so a swing can be driven the way the game
    // drives one: an input frame carrying the swing flag, then ticks. Modelled on core's
    // PunchDamageHostTests, which is where this arrangement is already proven.
    private sealed class Harness
    {
        private static readonly Vector3 Spawn = new(0f, 10f, 0f);

        private readonly LoopbackServerTransport _transport = new();
        private uint _frame;
        private byte _swing;
        private double _now = 5000.0;

        public NetServer Server { get; }
        public NetClient Client { get; }
        public ZombieSystem Zombies { get; }
        public PunchDamageHost Punches { get; }
        public List<PunchResult> Results { get; } = new();

        public Harness()
        {
            Server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Spawn, "PEI");
            Zombies = new ZombieSystem(
                new[] { new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 } },
                new List<NavBound>
                {
                    new() { Center = new Vector3(0f, 140f, 0f), Size = new Vector3(400f, 300f, 400f) },
                },
                FlatGround);
            Punches = new PunchDamageHost(Server, Zombies);
            Punches.Resolved += Results.Add;
            Client = new NetClient(_transport.CreateClient(), "Puncher", "PEI");
        }

        public void PlantZombie(ushort id, Vector3 position)
        {
            var state = new ZombieSystemState();
            state.Zombies.Add(new ZombieInstance
            {
                Id = id,
                Bound = 0,
                Position = position,
                Yaw = 0f,
                Health = 100,
                MaxHealth = 100,
            });
            Zombies.RestoreState(state);
        }

        public void Swing(float yaw)
        {
            Client.SendInput(new InputCommand(++_frame, 0, 0, jump: false, sprint: false,
                NetAngles.QuantizeYaw(yaw),
                NetAngles.QuantizePitch(PunchAim.WirePitchOffset),
                EPlayerStance.Stand, Spawn, grounded: true, hasSwing: true, swingSequence: ++_swing,
                swingFist: EPlayerPunch.Left));
        }

        public void Pump(int rounds)
        {
            for (int i = 0; i < rounds; i++)
            {
                _now += ServerSimulation.TickRate;
                Server.Update(_now);
                Client.Update(_now);
            }
        }

        private static bool FlatGround(float x, float z, out float y)
        {
            y = 10f;
            return true;
        }
    }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
