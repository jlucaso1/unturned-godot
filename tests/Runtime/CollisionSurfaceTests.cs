using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Getting from a raycast back to the surface it struck — PhysicsTool.GetColliderSharedPhysicsMaterialName,
// which is what decides the sound and the mark an impact makes.
//
// The shape index a query reports is the whole mechanism, and it is the part that can only be checked
// here: the surface table is built at upload in BodyAddShape order, and if that order and the query's
// index ever disagreed, a punch on wood would sound like metal — a mismatch no unit test can see, because
// the physics server is what assigns the index.
public class CollisionSurfaceTests : TestClass
{
    public CollisionSurfaceTests(Node testScene) : base(testScene) { }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

    private static Shape3D[] OneBox() => new Shape3D[] { new BoxShape3D { Size = Vector3.One * 2f } };

    private static Transform3D At(float z) => new(Basis.Identity, new Vector3(0f, 0f, z));

    // Casts down the -Z axis and returns the (rid, shape) pair the hit names.
    private static (Rid Body, int Shape) Cast(Node3D owner, float distance = 16f)
    {
        using var ray = new PhysicsRayQueryParameters3D
        {
            From = Vector3.Zero,
            To = Vector3.Forward * distance,
            CollisionMask = CollisionLayers.World,
        };
        using Godot.Collections.Dictionary hit = owner.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        Assert.True(hit.Count > 0, "the ray reached nothing");
        return (hit["rid"].As<Rid>(), (int)hit["shape"]);
    }

    // Two shapes on one body, made of different things, and the ray picks the near one. The whole point:
    // the answer has to follow the SHAPE, not the body.
    [Test]
    public async Task ABatchedHitNamesTheSurfaceOfTheShapeItStruck()
    {
        var table = new SurfaceTable();
        byte wood = table.IndexOf("Wood_Static");
        byte metal = table.IndexOf("Metal_Static");

        var bodies = new InstancedStaticBodies { Name = "Surfaces" };
        bodies.Add("near", OneBox(), new[] { new CollisionPlacement(0, At(-4f), wood) },
            CollisionLayers.World, table.Names);
        bodies.Add("far", OneBox(), new[] { new CollisionPlacement(0, At(-12f), metal) },
            CollisionLayers.World, table.Names);
        TestScene.AddChild(bodies);
        await NextPhysicsFrame();

        (Rid body, int shape) = Cast(bodies);

        Assert.Equal("Wood_Static", bodies.MaterialFor(body, shape));

        bodies.QueueFree();
    }

    // Several shapes on ONE body, each a different surface. This is the real layout — a building is one
    // body with every one of its colliders on it — and it is where an off-by-one in the upload order
    // would show up as the wrong material rather than as no material.
    [Test]
    public async Task WithinOneBodyEachShapeKeepsItsOwnSurface()
    {
        var table = new SurfaceTable();
        var bodies = new InstancedStaticBodies { Name = "Mixed" };
        bodies.Add("mixed", OneBox(), new[]
        {
            new CollisionPlacement(0, At(-16f), table.IndexOf("Concrete_Static")),
            new CollisionPlacement(0, At(-4f), table.IndexOf("Wood_Static")),
            new CollisionPlacement(0, At(-10f), table.IndexOf("Metal_Static")),
        }, CollisionLayers.World, table.Names);
        TestScene.AddChild(bodies);
        await NextPhysicsFrame();

        (Rid body, int shape) = Cast(bodies);

        // The nearest is the wooden one, and it is the SECOND shape uploaded — so a reader that assumed
        // shape order followed distance, or that took the body's first surface, would say concrete.
        Assert.Equal(1, shape);
        Assert.Equal("Wood_Static", bodies.MaterialFor(body, shape));
        Assert.Equal("Concrete_Static", bodies.MaterialFor(body, 0));
        Assert.Equal("Metal_Static", bodies.MaterialFor(body, 2));

        bodies.QueueFree();
    }

    // A collider with no PhysicMaterial. Unity's sharedMaterial is null there and PlayerEquipment.punch
    // skips the impact entirely, so this has to read as empty rather than as some default surface.
    [Test]
    public async Task AShapeWithNoMaterialNamesNoSurface()
    {
        var bodies = new InstancedStaticBodies { Name = "Bare" };
        bodies.Add("bare", OneBox(), new[] { new CollisionPlacement(0, At(-4f), 0) },
            CollisionLayers.World);
        TestScene.AddChild(bodies);
        await NextPhysicsFrame();

        (Rid body, int shape) = Cast(bodies);

        Assert.Equal(string.Empty, bodies.MaterialFor(body, shape));

        bodies.QueueFree();
    }

    // An index nobody uploaded, and a body nobody knows. Both are empty rather than a throw: a query can
    // legitimately land on terrain or on a body from another builder, and a punch that finds no surface is
    // a punch that makes no sound — not a crashed tick.
    [Test]
    public async Task AnUnknownBodyOrShapeIndexNamesNoSurface()
    {
        var table = new SurfaceTable();
        var bodies = new InstancedStaticBodies { Name = "Known" };
        bodies.Add("known", OneBox(),
            new[] { new CollisionPlacement(0, At(-4f), table.IndexOf("Wood_Static")) },
            CollisionLayers.World, table.Names);
        TestScene.AddChild(bodies);
        await NextPhysicsFrame();

        (Rid body, _) = Cast(bodies);

        Assert.Equal(string.Empty, bodies.MaterialFor(body, 7));
        Assert.Equal(string.Empty, bodies.MaterialFor(body, -1));
        Assert.Equal(string.Empty, bodies.MaterialFor(default, 0));

        bodies.QueueFree();
    }

    // The node-per-collider mode (UG_NODE_PHYSICS=1) has to answer identically — the two body flavours
    // disagreeing is exactly how the asset-naming path broke before.
    [Test]
    public async Task ANodePerColliderBodyNamesItsSurfaceToo()
    {
        var table = new SurfaceTable();
        var body = new InstancedStaticBody
        {
            Name = "node",
            Shapes = OneBox(),
            Placements = new[]
            {
                new CollisionPlacement(0, At(-12f), table.IndexOf("Metal_Static")),
                new CollisionPlacement(0, At(-4f), table.IndexOf("Wood_Static")),
            },
            SurfaceNames = table.Names,
            CollisionLayer = CollisionLayers.World,
        };
        TestScene.AddChild(body);
        await NextPhysicsFrame();

        (_, int shape) = Cast(body);

        Assert.Equal(1, shape);
        Assert.Equal("Wood_Static", body.MaterialFor(shape));
        Assert.Equal("Metal_Static", body.MaterialFor(0));
        Assert.Equal(string.Empty, body.MaterialFor(9));

        body.QueueFree();
    }
}
