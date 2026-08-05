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

    // Breadth. A map is not fifty copies of one prefab: it is hundreds of assets, spread over kilometres,
    // some with collision and some without, at every scale and rotation the editor allows. The builder
    // partitions that into cells and batches, and a fixture with one asset at the origin exercises
    // neither.
    [Test]
    public async Task AWholeMapsWorthOfPlacementsBuilds()
    {
        var db = new ObjectAssetDatabase();
        var meshes = new Dictionary<Guid, ArrayMesh>();
        var colliders = new Dictionary<Guid, List<CachedCollider>>();
        var placements = new List<PlacedObject>();
        var rng = new RandomNumberGenerator();
        rng.Seed = 12345; // fixed, so a failure is reproducible

        // Forty assets, half of them with collision — the split a real catalogue has.
        var assets = new List<Guid>();
        for (int i = 0; i < 40; i++)
        {
            Guid guid = Guid.NewGuid();
            assets.Add(guid);
            db.Add(Asset(guid, $"Prop{i}"));
            meshes[guid] = Triangle();
            if (i % 2 == 0)
            {
                colliders[guid] = new List<CachedCollider>
                {
                    CachedCollider.Box(Transform3D.Identity, Vector3.Zero, Vector3.One * (1f + i * 0.1f)),
                };
            }
        }

        // Six hundred placements spread over a kilometre, at varied scales and rotations, so the cell
        // partitioning and the per-instance transforms both run over something other than identity.
        for (int i = 0; i < 600; i++)
        {
            Guid guid = assets[i % assets.Count];
            placements.Add(new PlacedObject(
                new Vector3(rng.RandfRange(-500f, 500f), rng.RandfRange(0f, 20f), rng.RandfRange(-500f, 500f)),
                new Vector3(0f, rng.RandfRange(0f, 360f), 0f),
                Vector3.One * rng.RandfRange(0.5f, 2f),
                0, guid));
        }

        Node3D root = ObjectsBuilder.Build(placements, db, meshes, colliders, out int withMesh);
        TestScene.AddChild(root);
        await NextPhysicsFrame();

        Assert.Equal(600, withMesh);
        Assert.Equal(600, TotalInstances(root));

        // Batched, not one draw call per placement — the entire reason this exists. Forty assets over a
        // partitioned kilometre gives more batches than assets, and far fewer than placements.
        int batches = BatchCount(root);
        Assert.True(batches < 600, $"the placements were not batched: {batches} batches");
        Assert.True(batches >= assets.Count / 2,
            $"{batches} batches for 40 assets over a kilometre — the cell partitioning did not run");

        root.QueueFree();
    }

    // The same spread with a lower LOD level available. An authored LOD is a second batch per asset that
    // takes over at distance, so this is where the visibility ranges and the second library are used.
    [Test]
    public async Task ALowerLodLevelAddsItsOwnBatches()
    {
        var db = new ObjectAssetDatabase();
        var meshes = new Dictionary<Guid, ArrayMesh>();
        var lods = new Dictionary<Guid, ArrayMesh>();
        var placements = new List<PlacedObject>();

        for (int i = 0; i < 8; i++)
        {
            Guid guid = Guid.NewGuid();
            db.Add(Asset(guid, $"Tree{i}"));
            meshes[guid] = Triangle();
            lods[guid] = Triangle();
            for (int p = 0; p < 20; p++)
                placements.Add(new PlacedObject(new Vector3(p * 30f, 0f, i * 30f), Vector3.Zero,
                    Vector3.One, 0, guid));
        }

        Node3D withoutLod = ObjectsBuilder.Build(placements, db, meshes,
            new Dictionary<Guid, List<CachedCollider>>(), out int _);
        Node3D withLod = ObjectsBuilder.Build(placements, db, meshes,
            new Dictionary<Guid, List<CachedCollider>>(), out int _, lod1Library: lods);
        TestScene.AddChild(withoutLod);
        TestScene.AddChild(withLod);
        await NextFrame();

        Assert.True(TotalInstances(withLod) > TotalInstances(withoutLod),
            "the authored LOD level produced no instances of its own");

        withoutLod.QueueFree();
        withLod.QueueFree();
    }

    // What the dev console switches off is the geometry this builder DRAWS — its batches and its
    // placeholder boxes — and deliberately not the root they hang off. Both build paths attach the NPC
    // root to that same root, and NPCs have a switch of their own; grouping the root would make
    // `objects.enabled 0` take them with it and fold their cost into the objects measurement.
    [Test]
    public async Task TheConsoleGroupCoversWhatIsDrawnRatherThanTheRootTheNpcsShare()
    {
        Guid asset = Guid.NewGuid();
        var db = new ObjectAssetDatabase();
        db.Add(Asset(asset, "Pine"));
        var meshes = new Dictionary<Guid, ArrayMesh> { [asset] = Triangle() };

        Node3D root = ObjectsBuilder.Build(new List<PlacedObject> { Place(asset, Vector3.Zero) }, db,
            meshes, new Dictionary<Guid, List<CachedCollider>>(), out _);
        // Stands in for the NPC root, which both build paths attach here.
        var npcs = new Node3D { Name = "Npcs" };
        root.AddChild(npcs);
        TestScene.AddChild(root);
        await NextFrame();

        Assert.False(root.IsInGroup(SceneGroups.Objects));
        Assert.False(npcs.IsInGroup(SceneGroups.Objects));
        Node grouped = Assert.Single(TestScene.GetTree().GetNodesInGroup(SceneGroups.Objects));
        Assert.IsType<MultiMeshRidRenderer>(grouped);
        // And the same node is what the per-category and shadow switches drive.
        Assert.True(grouped.IsInGroup(SceneGroups.ObjectBatches));

        root.QueueFree();
        await NextFrame();
    }

    // ...and the node-wrapper mode is grouped too. UG_NODE_MULTIMESH=1 is an A/B control for owning the
    // RIDs directly, so its batches are nodes hanging off the root — which is exactly the root the group
    // deliberately skips. Ungrouped, `objects.enabled 0` would take the placeholders away and leave every
    // real batch drawing, in the one mode whose whole purpose is measurement.
    [Test]
    public async Task NodeWrapperBatchesJoinTheGroupTheRidRendererWouldHave()
    {
        Guid asset = Guid.NewGuid();
        var db = new ObjectAssetDatabase();
        db.Add(Asset(asset, "Pine"));
        var meshes = new Dictionary<Guid, ArrayMesh> { [asset] = Triangle() };

        // The flag is process-wide and read only while Build runs, so it is restored before this test
        // yields a frame. Holding it across the await would leave any other builder that ran in the
        // meantime silently in the A/B mode — which is the mode this test exists to keep honest.
        Node3D root;
        string previous = OS.GetEnvironment("UG_NODE_MULTIMESH");
        OS.SetEnvironment("UG_NODE_MULTIMESH", "1");
        try
        {
            root = ObjectsBuilder.Build(new List<PlacedObject> { Place(asset, Vector3.Zero) }, db,
                meshes, new Dictionary<Guid, List<CachedCollider>>(), out _);
        }
        finally
        {
            OS.SetEnvironment("UG_NODE_MULTIMESH", previous);
        }

        TestScene.AddChild(root);
        await NextFrame();

        Node grouped = Assert.Single(TestScene.GetTree().GetNodesInGroup(SceneGroups.Objects));
        Assert.IsType<MultiMeshInstance3D>(grouped);
        Assert.False(root.IsInGroup(SceneGroups.Objects));

        root.QueueFree();
        await NextFrame();
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
