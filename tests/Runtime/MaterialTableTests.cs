using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The materials realised for one load, and the pass that merges the duplicates a cold load could not
// avoid making.
//
// The problem it solves is an ordering one. Materials are shared by complete state, and part of that
// state is the texture's IDENTITY — the hash of its .tex file. On a cold load none of those files exist
// yet: materials are built between the mesh phase and the texture phase of the same decode pass, so every
// key stands in for its own identity and byte-identical textures build a material each.
//
// So the surfaces are recorded, and once the textures have settled the grouping is re-run with the real
// identities and the aliases are pointed at one material — which is what a warm load had all along. The
// swap is invisible because the merged materials carry the same map, filter and shader; what changes is
// the draw-call count.
public class MaterialTableTests : TestClass
{
    public MaterialTableTests(Node testScene) : base(testScene) { }

    // Nothing tracked is nothing to merge. A warm load resolves every identity up front, so this is its
    // ordinary path, and it must not walk a surface list it never filled.
    [Test]
    public void AWarmLoadHasNothingToRededuplicate()
    {
        using var dir = new TempDir();
        var table = new MaterialTable();
        var registry = new TextureRegistry(dir.Path);

        (int repointed, int materials) = table.Rededuplicate(registry);

        Assert.Equal(0, repointed);
        Assert.Equal(0, materials);
    }

    // Tracking is opt-in for a reason: the record holds a reference to every realised mesh for the rest
    // of the load, and a warm load has nothing to gain from it.
    [Test]
    public void SurfacesAreOnlyRecordedWhenTrackingIsAskedFor()
    {
        using var dir = new TempDir();
        var untracked = new MaterialTable();
        untracked.Track(TriangleMesh(), 0, Submesh("core_1"));

        Assert.Equal(0, untracked.Rededuplicate(new TextureRegistry(dir.Path)).Repointed);
    }

    // The cold load's whole point. Two surfaces whose textures turn out to be byte-identical end up
    // sharing one material, and the loser's surface is repointed at the winner's.
    [Test]
    public void TwoSurfacesOverIdenticalTexturesEndUpSharingOneMaterial()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "core_1", 200, 30, 30);
        WriteTexture(dir.Path, "mod_2", 200, 30, 30); // the same bytes under another key
        var registry = new TextureRegistry(dir.Path);
        registry.MaterialIdentity("core_1");
        registry.MaterialIdentity("mod_2");

        var table = new MaterialTable();
        table.TrackSurfaces();
        ArrayMesh first = TriangleMesh(), second = TriangleMesh();
        var winner = new StandardMaterial3D();
        var loser = new StandardMaterial3D();
        first.SurfaceSetMaterial(0, winner);
        second.SurfaceSetMaterial(0, loser);
        table.Track(first, 0, Submesh("core_1"));
        table.Track(second, 0, Submesh("mod_2"));

        (int repointed, _) = table.Rededuplicate(registry);

        Assert.Equal(1, repointed);
        Assert.Same(winner, second.SurfaceGetMaterial(0));
    }

    // Textures that are genuinely different keep their own materials — the merge must be driven by the
    // content hash and not by anything looser, or two surfaces would end up wearing one texture.
    [Test]
    public void SurfacesOverDifferentTexturesKeepTheirOwnMaterials()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "core_1", 200, 30, 30);
        WriteTexture(dir.Path, "core_2", 30, 200, 30); // different pixels
        var registry = new TextureRegistry(dir.Path);
        registry.MaterialIdentity("core_1");
        registry.MaterialIdentity("core_2");

        var table = new MaterialTable();
        table.TrackSurfaces();
        ArrayMesh first = TriangleMesh(), second = TriangleMesh();
        var a = new StandardMaterial3D();
        var b = new StandardMaterial3D();
        first.SurfaceSetMaterial(0, a);
        second.SurfaceSetMaterial(0, b);
        table.Track(first, 0, Submesh("core_1"));
        table.Track(second, 0, Submesh("core_2"));

        Assert.Equal(0, table.Rededuplicate(registry).Repointed);
        Assert.Same(a, first.SurfaceGetMaterial(0));
        Assert.Same(b, second.SurfaceGetMaterial(0));
    }

    // Same texture, different material state — a different colour tint — is a different material. The
    // texture identity is only part of what a material IS.
    [Test]
    public void TheSameTextureUnderADifferentTintIsADifferentMaterial()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "core_1", 200, 30, 30);
        WriteTexture(dir.Path, "mod_2", 200, 30, 30);
        var registry = new TextureRegistry(dir.Path);
        registry.MaterialIdentity("core_1");
        registry.MaterialIdentity("mod_2");

        var table = new MaterialTable();
        table.TrackSurfaces();
        ArrayMesh first = TriangleMesh(), second = TriangleMesh();
        first.SurfaceSetMaterial(0, new StandardMaterial3D());
        second.SurfaceSetMaterial(0, new StandardMaterial3D());
        table.Track(first, 0, Submesh("core_1", Colors.White));
        table.Track(second, 0, Submesh("mod_2", Colors.Red)); // same pixels, painted differently

        Assert.Equal(0, table.Rededuplicate(registry).Repointed);
    }

    // An untextured submesh has no key to resolve, so it can neither gain nor lose an alias — and it must
    // not be recorded at all, or the merge would try to group surfaces by an empty identity.
    [Test]
    public void AnUntexturedSurfaceIsNotTracked()
    {
        using var dir = new TempDir();
        var table = new MaterialTable();
        table.TrackSurfaces();
        table.Track(TriangleMesh(), 0, Submesh(""));

        Assert.Equal(0, table.Rededuplicate(new TextureRegistry(dir.Path)).Repointed);
    }

    // A merged surface stays on the loser key's pending list. A texture that never applies — an
    // unsupported format — would otherwise leave that key pending forever, and a late apply would reach
    // nothing: the surface would keep an untextured material for the session.
    [Test]
    public void AMergedSurfaceStaysReachableFromTheKeyItGaveUp()
    {
        using var dir = new TempDir();
        WriteTexture(dir.Path, "core_1", 44, 44, 44);
        WriteTexture(dir.Path, "mod_2", 44, 44, 44);
        var registry = new TextureRegistry(dir.Path);
        var winner = new StandardMaterial3D();
        registry.Register("core_1", winner);
        registry.Register("mod_2", new StandardMaterial3D());
        registry.MaterialIdentity("core_1");
        registry.MaterialIdentity("mod_2");

        var table = new MaterialTable();
        table.TrackSurfaces();
        ArrayMesh first = TriangleMesh(), second = TriangleMesh();
        first.SurfaceSetMaterial(0, winner);
        second.SurfaceSetMaterial(0, new StandardMaterial3D());
        table.Track(first, 0, Submesh("core_1"));
        table.Track(second, 0, Submesh("mod_2"));

        Assert.Equal(1, table.Rededuplicate(registry).Repointed);

        // The surviving material is now on the loser key's queue too, so applying that key still lands.
        Assert.True(registry.Apply("mod_2"));
        Assert.NotNull(winner.AlbedoTexture);
    }

    // The share-by-state table itself: a surface asks for its exact material state and either gets the
    // one already realised or contributes a new one. This is what stops a map with 6000 objects from
    // building 6000 materials.
    [Test]
    public void MaterialsAreSharedByCompleteState()
    {
        var table = new MaterialTable();
        var key = new MaterialTable.Key("identity-a", Colors.White, UnityMaterial.Blend.Opaque,
            0f, 0f, EShaderCull.Back);
        var other = key with { TextureIdentity = "identity-b" };

        Assert.False(table.TryGet(key, out _));
        Assert.Equal(0, table.Count);

        var material = new StandardMaterial3D();
        table.Add(key, material);

        Assert.True(table.TryGet(key, out Material found));
        Assert.Same(material, found);
        Assert.Equal(1, table.Count);
        Assert.False(table.TryGet(other, out _)); // a different identity is a different material
    }

    // The load index is dropped once there is nothing left to realise. It holds a reference to every
    // realised mesh and every material; the scene owns both from here, and a table asked for a material
    // after this would build a second one — which is the behaviour, not a bug, so it is pinned.
    [Test]
    public void ReleasingDropsTheLoadIndex()
    {
        using var dir = new TempDir();
        var table = new MaterialTable();
        table.TrackSurfaces();
        var key = new MaterialTable.Key("identity-a", Colors.White, UnityMaterial.Blend.Opaque,
            0f, 0f, EShaderCull.Back);
        table.Add(key, new StandardMaterial3D());
        table.Track(TriangleMesh(), 0, Submesh("core_1"));
        Assert.Equal(1, table.Count);

        table.Release();

        Assert.Equal(0, table.Count);
        Assert.False(table.TryGet(key, out _));
        // Tracking is off again too, so a table reused after a release does not start recording meshes.
        table.Track(TriangleMesh(), 0, Submesh("core_1"));
        Assert.Equal(0, table.Rededuplicate(new TextureRegistry(dir.Path)).Repointed);
    }

    // Untextured materials are counted in the surviving total even though the merge never sees them: an
    // untextured submesh has no key to resolve, so the report would understate what the load realised.
    [Test]
    public void UntexturedMaterialsStillCountTowardsTheSurvivingTotal()
    {
        using var dir = new TempDir();
        var table = new MaterialTable();
        table.TrackSurfaces();
        table.Add(new MaterialTable.Key("", Colors.Gray, UnityMaterial.Blend.Opaque, 0f, 0f,
            EShaderCull.Back), new StandardMaterial3D());
        WriteTexture(dir.Path, "core_1", 7, 7, 7);
        var registry = new TextureRegistry(dir.Path);
        registry.MaterialIdentity("core_1");
        ArrayMesh mesh = TriangleMesh();
        mesh.SurfaceSetMaterial(0, new StandardMaterial3D());
        table.Track(mesh, 0, Submesh("core_1"));

        (_, int materials) = table.Rededuplicate(registry);

        Assert.Equal(2, materials); // one textured group plus the untextured one
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static ArrayMesh TriangleMesh()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static CachedSubmesh Submesh(string textureKey, Color? color = null) =>
        new(new[] { 0, 1, 2 }, color ?? Colors.White, textureKey, UnityMaterial.Blend.Opaque);

    private static void WriteTexture(string dir, string key, byte r, byte g, byte b)
    {
        using var ms = new MemoryStream();
        TextureCache.Write(ms, new CachedTexture(4, 1, 1, 1, new byte[] { r, g, b, 255 }));
        File.WriteAllBytes(Path.Combine(dir, key + ".tex"), ms.ToArray());
    }

    private sealed class TempDir : System.IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-materials-" + System.Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

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
}
