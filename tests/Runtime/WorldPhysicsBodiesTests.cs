using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The world's collision, which is built through PhysicsServer3D directly rather than as nodes.
//
// That choice is the reason these need a live engine and the reason they are worth testing. A
// StaticBody3D per placement would explode the node count on a map with 6000 objects, so one body holds
// every placement of one asset and the shapes go on by RID. What that buys in nodes it costs in things
// the engine would otherwise do for you — and each of those is a real bug that has happened:
//
//   - a server-owned body is anonymous unless its instance id is attached, so every query result comes
//     back as "?" with no way home to the GUID that produced it;
//   - the name is the ONLY identity a physics query can recover, so the writer and the reader of that
//     name must not drift, or a punch simply stops damaging anything and looks like a targeting bug;
//   - a ladder needs its own frame back, which a batched body cannot give.
public class WorldPhysicsBodiesTests : TestClass
{
    public WorldPhysicsBodiesTests(Node testScene) : base(testScene) { }

    // --- the name that is the only identity a hit can recover ----------------------------------------

    [Test]
    public void ACollisionBodyNameRoundTripsToItsAsset()
    {
        Guid guid = Guid.NewGuid();

        Assert.True(ObjectCollisionNames.TryParseGuid(ObjectCollisionNames.For(guid), out Guid plain));
        Assert.Equal(guid, plain);

        // Cell-suffixed bodies parse back to the same asset: the suffix says which part of the map this
        // body covers, not which asset it holds.
        Assert.True(ObjectCollisionNames.TryParseGuid(ObjectCollisionNames.For(guid, 3, -7), out Guid celled));
        Assert.Equal(guid, celled);
    }

    // Everything that is not one of ours parses to nothing rather than to a wrong GUID — terrain, ladder
    // volumes, a body some other system named. A false positive here would attribute a hit on the ground
    // to whatever asset the mangled parse happened to produce.
    [Test]
    public void ABodyThatIsNotOursIsNotMistakenForAnAsset()
    {
        foreach (string name in new[] { "Terrain", "", "Col_", "Col_not-a-guid", "Ladders", "Col" })
            Assert.False(ObjectCollisionNames.TryParseGuid(name, out _), $"'{name}' parsed as an asset");

        Assert.False(ObjectCollisionNames.TryParseGuid(null!, out _));
    }

    // --- the batched static body ---------------------------------------------------------------------

    // The body is created in _Ready — in the tree, so a space exists to join — and it points back at the
    // node. Without that attachment every query result on the object world is anonymous, which is exactly
    // what made the collision diagnostics report "?" as the owner.
    [Test]
    public async Task ABatchedBodyIsQueryableAndKnowsWhoItIs()
    {
        var shape = new BoxShape3D { Size = Vector3.One };
        var body = new InstancedStaticBody
        {
            Name = ObjectCollisionNames.For(Guid.NewGuid()),
            Shapes = new Shape3D[] { shape },
            Placements = new[] { (0, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 0f))) },
        };
        TestScene.AddChild(body);
        await NextPhysicsFrame();

        PhysicsDirectSpaceState3D space = body.GetWorld3D().DirectSpaceState;
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = Vector3.One * 0.5f },
            Transform = Transform3D.Identity,
            CollisionMask = body.CollisionLayer,
        };
        Godot.Collections.Array<Godot.Collections.Dictionary> hits = space.IntersectShape(query);

        Assert.NotEmpty(hits);
        // The hit resolves to the node, not to a bare RID — which is what makes the GUID recoverable.
        Assert.Same(body, hits[0]["collider"].As<Node>());

        body.QueueFree();
    }

    // The managed placement list is dropped once the server has copied it. Thousands of tuples per body
    // serve no later query, and holding them inflated steady-state RAM for the whole session.
    [Test]
    public async Task ThePlacementListIsDroppedOnceTheServerHasIt()
    {
        var body = new InstancedStaticBody
        {
            Shapes = new Shape3D[] { new BoxShape3D() },
            Placements = new[] { (0, Transform3D.Identity), (0, Transform3D.Identity) },
        };
        TestScene.AddChild(body);
        await NextFrame();

        Assert.Empty(body.Placements);
        body.QueueFree();
    }

    // …unless asked to keep them, which is the diagnostic path.
    [Test]
    public async Task ThePlacementListCanBeKeptForDiagnostics()
    {
        string previous = OS.GetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS");
        OS.SetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS", "1");
        try
        {
            var body = new InstancedStaticBody
            {
                Shapes = new Shape3D[] { new BoxShape3D() },
                Placements = new[] { (0, Transform3D.Identity) },
            };
            TestScene.AddChild(body);
            await NextFrame();

            Assert.Single(body.Placements);
            body.QueueFree();
        }
        finally
        {
            OS.SetEnvironment("UG_KEEP_PHYSICS_PLACEMENTS", previous);
        }
    }

    // Leaving the tree frees the server body. A raw RID is not owned by the node, so nothing else would
    // ever release it — the leak would be for the life of the process, on every map change.
    [Test]
    public async Task LeavingTheTreeFreesTheServerBody()
    {
        var body = new InstancedStaticBody
        {
            Name = "Col_leak_check",
            Shapes = new Shape3D[] { new BoxShape3D { Size = Vector3.One } },
            Placements = new[] { (0, Transform3D.Identity) },
        };
        TestScene.AddChild(body);
        await NextPhysicsFrame();
        World3D world = body.GetWorld3D();

        TestScene.RemoveChild(body);
        body.Free();
        await NextPhysicsFrame();

        // Nothing of ours is left in the space to be hit.
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = Vector3.One * 0.5f },
            Transform = Transform3D.Identity,
            CollisionMask = 1,
        };
        foreach (Godot.Collections.Dictionary hit in world.DirectSpaceState.IntersectShape(query))
            Assert.True(hit["collider"].As<Node>() is not InstancedStaticBody,
                "the freed body was still in the physics space");
    }

    // --- ladders -------------------------------------------------------------------------------------

    // A body per ladder rather than one batched body, because the climb rules need the volume's own
    // frame back: Unturned reads the collider's transform for the axis the ladder faces along. A batched
    // body would hand back only the shape that was hit.
    [Test]
    public async Task EachLadderKeepsItsOwnFrame()
    {
        var ladders = new LadderVolumes { Name = "Ladders" };
        var frame = new Transform3D(Basis.Identity, new Vector3(4f, 0f, 9f));
        ladders.Add(new BoxShape3D { Size = new Vector3(1f, 4f, 0.2f) },
            new Transform3D(Basis.Identity, new Vector3(4f, 2f, 9f)), frame);
        Assert.Equal(1, ladders.Count); // counted before it is in the tree, from the pending list

        TestScene.AddChild(ladders);
        await NextFrame();

        Assert.Equal(1, ladders.Count); // and after, from the built bodies
        ladders.QueueFree();
    }

    // A ray that met something which is not a ladder resolves to nothing — and that is the answer that
    // matters, because the FIRST thing the probe meets decides: a wall in front of a ladder means there
    // is no ladder to climb.
    [Test]
    public void ARayThatMissedALadderResolvesToNothing()
    {
        Assert.False(LadderVolumes.TryResolve(new Godot.Collections.Dictionary(), out _));

        // A hit on something real, but not a ladder.
        var wall = new StaticBody3D();
        TestScene.AddChild(wall);
        var onAWall = new Godot.Collections.Dictionary
        {
            ["collider"] = wall,
            ["rid"] = default(Rid),
            ["position"] = Vector3.Zero,
            ["normal"] = Vector3.Up,
        };
        Assert.False(LadderVolumes.TryResolve(onAWall, out _));

        wall.QueueFree();
    }

    // A hit on a ladder body that this LadderVolumes does not know is also nothing: the rid is the only
    // thing that says WHICH ladder, and an unknown one cannot produce a frame to climb along.
    [Test]
    public async Task AHitOnAnUnknownLadderBodyResolvesToNothing()
    {
        var ladders = new LadderVolumes { Name = "Ladders" };
        ladders.Add(new BoxShape3D(), Transform3D.Identity, Transform3D.Identity);
        TestScene.AddChild(ladders);
        await NextFrame();

        var strangeRid = new Godot.Collections.Dictionary
        {
            ["collider"] = ladders,
            ["rid"] = default(Rid), // never one of this node's bodies
            ["position"] = Vector3.Zero,
            ["normal"] = Vector3.Up,
        };

        Assert.False(LadderVolumes.TryResolve(strangeRid, out _));
        ladders.QueueFree();
    }

    // The frame is what the climb rules read: its origin is the ladder's position and its Y axis the
    // direction it faces, which is what a player is aligned to when they mount.
    [Test]
    public async Task AHitOnAKnownLadderCarriesItsFrameBack()
    {
        var ladders = new LadderVolumes { Name = "Ladders" };
        // Turned a quarter turn, so "the frame came back" cannot pass by accident on an identity basis.
        var frame = new Transform3D(new Basis(Vector3.Up, Mathf.Pi * 0.5f), new Vector3(11f, 0f, -3f));
        ladders.Add(new BoxShape3D { Size = new Vector3(1f, 4f, 0.2f) }, frame, frame);
        TestScene.AddChild(ladders);
        await NextPhysicsFrame();

        Rid body = FirstLadderBody(ladders);
        var hit = new Godot.Collections.Dictionary
        {
            ["collider"] = ladders,
            ["rid"] = body,
            ["position"] = new Vector3(11f, 1.5f, -3f),
            ["normal"] = Vector3.Forward,
        };

        Assert.True(LadderVolumes.TryResolve(hit, out LadderContact contact));
        Assert.Equal(new Vector3(11f, 1.5f, -3f), contact.Point);
        Assert.Equal(frame.Origin, contact.Origin);
        Assert.True(contact.Up.IsEqualApprox(frame.Basis.Y.Normalized()));

        ladders.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // The RID of the one body this node built, found by asking the space what is there rather than by
    // reaching into the node's private state.
    private static Rid FirstLadderBody(LadderVolumes ladders)
    {
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = Vector3.One * 8f },
            Transform = new Transform3D(Basis.Identity, new Vector3(11f, 0f, -3f)),
            CollisionMask = CollisionLayers.Ladder,
        };
        foreach (Godot.Collections.Dictionary hit in
                 ladders.GetWorld3D().DirectSpaceState.IntersectShape(query))
        {
            if (hit["collider"].As<Node>() == ladders)
                return hit["rid"].As<Rid>();
        }
        throw new InvalidOperationException("the ladder body was not in the physics space");
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

    // Queries have to be made against a settled physics step. This project runs 3D physics on its own
    // thread (project.godot: 3d/run_on_separate_thread), so DirectSpaceState is only safe to touch right
    // after a physics frame — reached any other way it comes back null and every query NREs.
    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
