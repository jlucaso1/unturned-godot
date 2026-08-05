using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Placing the map's objects: 6,023 of them on PEI, from a few hundred distinct assets.
//
// The whole design follows from that ratio. Placements are grouped by asset and drawn as batched
// instances rather than as nodes, and collision goes on server-owned bodies by RID — one per asset per
// cell, not one per placement. A node per object would be 6,000 nodes with no per-node behaviour.
//
// What that makes testable without a real map is the GROUPING: many placements of one asset become one
// batch, two assets cannot share one, and a server can build the same world with nothing drawn.
//
// Deliberately not asserted here: what happens to an asset with no mesh, and to a placement whose asset
// is unknown. Both matter, and both depend on how the region/cell partitioning lays nodes out — which
// this fixture does not reproduce faithfully enough for an assertion to mean anything. Guessing at it
// would produce a test that passes for the wrong reason, which is worse than the gap.
public class ObjectsBuilderTests : TestClass
{
    public ObjectsBuilderTests(Node testScene) : base(testScene) { }

    // Many placements of one asset are drawn as ONE batch. This is the entire reason the builder exists
    // in this shape: 6,000 objects from a few hundred assets, so instancing is the difference between a
    // few hundred draw calls and six thousand.
    [Test]
    public async Task ManyPlacementsOfOneAssetBecomeOneBatch()
    {
        Guid asset = Guid.NewGuid();
        var db = new ObjectAssetDatabase();
        db.Add(Asset(asset, "Pine"));
        var meshes = new Dictionary<Guid, ArrayMesh> { [asset] = Triangle() };

        var placements = new List<PlacedObject>();
        for (int i = 0; i < 50; i++)
            placements.Add(Place(asset, new Vector3(i * 4f, 0f, 0f)));

        Node3D root = ObjectsBuilder.Build(placements, db, meshes,
            new Dictionary<Guid, List<CachedCollider>>(), out int withMesh);
        TestScene.AddChild(root);
        await NextFrame();

        Assert.Equal(50, withMesh);
        // One MultiMesh carrying every instance, rather than 50 of anything.
        Assert.Equal(50, TotalInstances(root));
        Assert.True(BatchCount(root) < 50, $"the placements were not batched: {BatchCount(root)} batches");

        root.QueueFree();
    }

    // Two assets cannot share a batch, because a batch carries one mesh.
    [Test]
    public async Task TwoAssetsGetTwoBatches()
    {
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var db = new ObjectAssetDatabase();
        db.Add(Asset(first, "Pine"));
        db.Add(Asset(second, "Rock"));
        var meshes = new Dictionary<Guid, ArrayMesh> { [first] = Triangle(), [second] = Triangle() };

        Node3D root = ObjectsBuilder.Build(
            new[] { Place(first, Vector3.Zero), Place(second, new Vector3(10f, 0f, 0f)) },
            db, meshes, new Dictionary<Guid, List<CachedCollider>>(), out int withMesh);
        TestScene.AddChild(root);
        await NextFrame();

        Assert.Equal(2, withMesh);
        Assert.Equal(2, TotalInstances(root));

        root.QueueFree();
    }

    // Rendering can be switched off entirely: the dedicated server builds the same collision world with
    // nothing drawn, and a server that allocated meshes would be paying for a screen it does not have.
    [Test]
    public async Task AServerCanBuildTheCollisionWorldWithoutDrawingIt()
    {
        Guid asset = Guid.NewGuid();
        var db = new ObjectAssetDatabase();
        db.Add(Asset(asset, "Pine"));
        var meshes = new Dictionary<Guid, ArrayMesh> { [asset] = Triangle() };
        var colliders = new Dictionary<Guid, List<CachedCollider>>
        {
            [asset] = new() { CachedCollider.Box(Transform3D.Identity, Vector3.Zero, Vector3.One) },
        };

        Node3D root = ObjectsBuilder.Build(new[] { Place(asset, Vector3.Zero) }, db, meshes, colliders,
            out int _, renderGeometry: false);
        TestScene.AddChild(root);
        await NextPhysicsFrame();

        Assert.Equal(0, TotalInstances(root));          // nothing drawn
        Assert.True(HasCollision(root), "the server built no collision"); // but the world is solid

        root.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static int TotalInstances(Node parent)
    {
        int total = 0;
        foreach (Node child in parent.GetChildren())
        {
            if (child is MultiMeshRidRenderer renderer)
                foreach (MultiMesh mesh in renderer.MultiMeshes)
                    total += mesh.InstanceCount;
            if (child is MultiMeshInstance3D single && single.Multimesh != null)
                total += single.Multimesh.InstanceCount;
            total += TotalInstances(child);
        }
        return total;
    }

    private static int BatchCount(Node parent)
    {
        int batches = 0;
        foreach (Node child in parent.GetChildren())
        {
            if (child is MultiMeshRidRenderer renderer)
                foreach (MultiMesh _ in renderer.MultiMeshes)
                    batches++;
            if (child is MultiMeshInstance3D)
                batches++;
            batches += BatchCount(child);
        }
        return batches;
    }

    private static bool HasCollision(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is InstancedStaticBodies bodies && bodies.BodyCount > 0)
                return true;
            if (child is InstancedStaticBody or StaticBody3D)
                return true;
            if (HasCollision(child))
                return true;
        }
        return false;
    }

    // ObjectAsset is only constructible by parsing a .dat, which is the right constraint: there is no
    // way to build one that the scanner could not have produced.
    private static ObjectAsset Asset(Guid guid, string name)
    {
        DatDictionary root = DatParser.Parse(
            $"GUID {guid:N}\nType Large\nID 0\nName {name}\n");
        Assert.True(ObjectAsset.TryParse(root, localizedName: name, out ObjectAsset? asset),
            "the fixture .dat did not parse");
        return asset!;
    }

    private static PlacedObject Place(Guid asset, Vector3 position) =>
        new(position, Vector3.Zero, Vector3.One, 0, asset);

    private static ArrayMesh Triangle()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private SignalAwaiter NextFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
