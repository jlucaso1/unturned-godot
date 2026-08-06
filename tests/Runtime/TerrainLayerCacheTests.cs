using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The terrain layer texture cache, and the batched physics bodies that share one lifecycle node.
//
// The cache is really a staleness question, and staleness is the interesting half: a cached texture that
// outlives the bundle it came from is a map drawn with another map's ground. So every entry carries a
// stamp of its SOURCE — path, length, last-write — and anything that does not match is treated as absent
// rather than as usable. A game update rewrites the bundle and every layer re-extracts.
//
// It lives here rather than in the xUnit suite because CachedTexture is decoded through Godot's image
// types and the default directory resolves through ProjectSettings.
public class TerrainLayerCacheTests : TestClass
{
    public TerrainLayerCacheTests(Node testScene) : base(testScene) { }

    // --- staleness -----------------------------------------------------------------------------------

    // Nothing cached: everything is missing. This is the first run of every map.
    [Test]
    public void WithAnEmptyCacheEverythingIsMissing()
    {
        using var dir = new TempDir();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        string bundle = dir.WriteBundle("core.masterbundle");

        HashSet<Guid> missing = TerrainLayerCache.Missing(
            new[] { a, b },
            new Dictionary<Guid, string> { [a] = bundle, [b] = bundle },
            dir.Path);

        Assert.Equal(2, missing.Count);
    }

    // A written entry stops being missing — the round trip the whole cache exists for.
    [Test]
    public void AWrittenEntryIsFoundAndReadsBack()
    {
        using var dir = new TempDir();
        Guid material = Guid.NewGuid();
        string bundle = dir.WriteBundle("core.masterbundle");
        var texture = new CachedTexture(4, 1, 1, 1, new byte[] { 12, 34, 56, 255 });

        TerrainLayerCache.Write(material, texture, bundle, dir.Path);

        Assert.Empty(TerrainLayerCache.Missing(new[] { material },
            new Dictionary<Guid, string> { [material] = bundle }, dir.Path));
    }

    // The point of the stamp. A bundle that changed — a game update, a workshop re-upload — invalidates
    // everything extracted from it, because a cached texture that outlives its source is a map drawn with
    // another map's ground.
    [Test]
    public async Task ABundleThatChangedInvalidatesWhatCameOutOfIt()
    {
        using var dir = new TempDir();
        Guid material = Guid.NewGuid();
        string bundle = dir.WriteBundle("core.masterbundle");
        TerrainLayerCache.Write(material, new CachedTexture(4, 1, 1, 1, new byte[] { 1, 2, 3, 4 }),
            bundle, dir.Path);
        var byGuid = new Dictionary<Guid, string> { [material] = bundle };
        Assert.Empty(TerrainLayerCache.Missing(new[] { material }, byGuid, dir.Path));

        // Rewrite it at a different length, which is what a real update looks like from here. The
        // last-write timestamp alone can be too coarse to notice a change made in the same instant.
        await Task.Delay(10);
        File.WriteAllBytes(bundle, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9, 9 });

        Assert.Contains(material, TerrainLayerCache.Missing(new[] { material }, byGuid, dir.Path));
    }

    // A GUID nobody can say which bundle it came from cannot be validated, so it is missing rather than
    // trusted. Without this a stale entry with no source would survive every update forever.
    [Test]
    public void AMaterialWithNoKnownBundleIsMissing()
    {
        using var dir = new TempDir();
        Guid material = Guid.NewGuid();
        string bundle = dir.WriteBundle("core.masterbundle");
        TerrainLayerCache.Write(material, new CachedTexture(4, 1, 1, 1, new byte[] { 1, 2, 3, 4 }),
            bundle, dir.Path);

        Assert.Contains(material, TerrainLayerCache.Missing(
            new[] { material }, new Dictionary<Guid, string>(), dir.Path));
    }

    // A bundle that is not there at all produces no stamp, and no stamp means nothing validates against
    // it — the map re-extracts rather than drawing from a cache it cannot vouch for.
    [Test]
    public void AMissingBundleValidatesNothing()
    {
        using var dir = new TempDir();
        Guid material = Guid.NewGuid();
        string gone = Path.Combine(dir.Path, "not-here.masterbundle");

        Assert.Contains(material, TerrainLayerCache.Missing(
            new[] { material }, new Dictionary<Guid, string> { [material] = gone }, dir.Path));
    }

    // A write that cannot land is best-effort: the next boot extracts the texture again, which is a cost
    // rather than a failure. A throw here would take down a load over a full disk.
    [Test]
    public void AWriteThatCannotLandIsNotAFailure()
    {
        using var dir = new TempDir();
        string bundle = dir.WriteBundle("core.masterbundle");

        // A path that cannot be created: the "directory" is an existing file.
        string blocked = Path.Combine(dir.Path, "core.masterbundle", "nested");
        TerrainLayerCache.Write(Guid.NewGuid(), new CachedTexture(4, 1, 1, 1, new byte[] { 1, 2, 3, 4 }),
            bundle, blocked);
    }

    // The default cache directory resolves through the engine's user:// path, which is what puts it
    // beside the mesh and texture caches instead of somewhere relative to the working directory.
    [Test]
    public void TheDefaultDirectoryIsUnderTheUserData()
    {
        Assert.NotEmpty(TerrainLayerCache.Directory);
        Assert.False(TerrainLayerCache.Directory.StartsWith("user://", StringComparison.Ordinal),
            "the path was never globalized, so File I/O against it would fail");
    }

    // --- the batched bodies --------------------------------------------------------------------------

    // Every body keeps its own name behind its RID, which is what preserves collision diagnostics when
    // many bodies share one node: the hit reports a body RID, and the name is the way back to the asset.
    [Test]
    public async Task EachBatchedBodyKeepsItsOwnNameBehindItsRid()
    {
        var bodies = new InstancedStaticBodies { Name = "ObjectCollision" };
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        var pool = new Shape3D[] { new BoxShape3D { Size = Vector3.One * 2f } };
        bodies.Add(ObjectCollisionNames.For(first), pool,
            new[] { new CollisionPlacement(0, new Transform3D(Basis.Identity, new Vector3(0f, 0f, -4f)), 0) },
            CollisionLayers.World);
        bodies.Add(ObjectCollisionNames.For(second), pool,
            new[] { new CollisionPlacement(0, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 4f)), 0) },
            CollisionLayers.World);
        Assert.Equal(2, bodies.BodyCount); // counted from the pending definitions

        TestScene.AddChild(bodies);
        await NextPhysicsFrame();
        Assert.Equal(2, bodies.BodyCount); // and from the built bodies

        // Query each side and check the names come back distinct and correct.
        Assert.Equal(ObjectCollisionNames.For(first), NameAt(bodies, new Vector3(0f, 0f, -4f)));
        Assert.Equal(ObjectCollisionNames.For(second), NameAt(bodies, new Vector3(0f, 0f, 4f)));

        bodies.QueueFree();
    }

    // An unknown RID falls back to the node's own name rather than to nothing, so a diagnostic always has
    // something to print — "?" in a collision report is what this avoids.
    [Test]
    public async Task AnUnknownRidFallsBackToTheNodesName()
    {
        var bodies = new InstancedStaticBodies { Name = "ObjectCollision" };
        bodies.Add("Col_whatever", new Shape3D[] { new BoxShape3D() },
            new[] { new CollisionPlacement(0, Transform3D.Identity, 0) }, CollisionLayers.World);
        TestScene.AddChild(bodies);
        await NextPhysicsFrame();

        // Against the node's ACTUAL name: QueueFree is deferred, so an earlier test's node with the same
        // name may still be in the tree and Godot will have uniquified this one.
        Assert.Equal(bodies.Name.ToString(), bodies.NameFor(default));

        // And the static reader over a raw hit dictionary: no collider at all is "?" rather than a throw.
        Assert.Equal("?", InstancedStaticBodies.ColliderName(new Godot.Collections.Dictionary()));

        bodies.QueueFree();
    }

    // A hit on something that is not one of these reports that node's own name — terrain, a ladder, a
    // body some other system owns.
    [Test]
    public void AHitOnAnotherKindOfBodyReportsItsOwnName()
    {
        var wall = new StaticBody3D { Name = "Terrain" };
        TestScene.AddChild(wall);

        Assert.Equal(wall.Name.ToString(), InstancedStaticBodies.ColliderName(
            new Godot.Collections.Dictionary { ["collider"] = wall }));

        wall.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static string NameAt(InstancedStaticBodies bodies, Vector3 where)
    {
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new BoxShape3D { Size = Vector3.One * 0.5f },
            Transform = new Transform3D(Basis.Identity, where),
            CollisionMask = CollisionLayers.World,
        };
        foreach (Godot.Collections.Dictionary hit in
                 bodies.GetWorld3D().DirectSpaceState.IntersectShape(query))
        {
            if (hit["collider"].As<Node>() == bodies)
                return InstancedStaticBodies.ColliderName(hit);
        }
        throw new InvalidOperationException($"nothing of ours was at {where}");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-layers-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        // A stand-in for the bundle a layer was extracted from. Only its path, length and write time are
        // read, so its contents do not have to be a real bundle.
        public string WriteBundle(string name)
        {
            string path = System.IO.Path.Combine(Path, name);
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }

    private SignalAwaiter NextPhysicsFrame() =>
        TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.PhysicsFrame);
}
