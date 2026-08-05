using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Turning the per-GUID mesh caches the extractor wrote into ArrayMeshes the scene can draw.
//
// It is engine-bound at the far end — ArrayMesh, StandardMaterial3D, ImageTexture — but everything
// interesting about it is what it does with a directory full of files that may or may not be sound. A map
// like PEI realises 416 meshes through this on every load, and the two ways it can go wrong are opposite:
// refusing a mesh that was fine (an object missing from the world) or accepting one that was not (a load
// that faults and drops the player back to the menu).
//
// The submesh/material grouping this hands to MaterialTable is covered separately; what is here is the
// reading, the sharing, and the failure handling.
public class ModelLibraryTests : TestClass
{
    public ModelLibraryTests(Node testScene) : base(testScene) { }

    // A cache directory that does not exist is an empty library rather than a throw: the first run of a
    // map reaches here before anything has been extracted.
    [Test]
    public void AMissingCacheDirectoryLoadsNothing()
    {
        using var dir = new TempDir();
        Assert.Empty(ModelLibrary.Load(Path.Combine(dir.Path, "not-created"), Registry(dir)));
    }

    // The ordinary path: every cached mesh becomes an ArrayMesh with its geometry intact.
    [Test]
    public void EveryCachedMeshIsRealised()
    {
        using var dir = new TempDir();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        dir.WriteMesh(first, "grass");
        dir.WriteMesh(second, "stone");

        Dictionary<Guid, ArrayMesh> library = ModelLibrary.Load(dir.Path, Registry(dir));

        Assert.Equal(2, library.Count);
        Assert.Equal(1, library[first].GetSurfaceCount());
        var vertices = (Vector3[])library[first].SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
        Assert.Equal(3, vertices.Length);
    }

    // Addressing the cache by name rather than scanning it: the cache is shared between every map, so a
    // load that read all of it would realise the meshes of maps nobody opened.
    [Test]
    public void AskingForSpecificGuidsSkipsEverythingElse()
    {
        using var dir = new TempDir();
        Guid wanted = Guid.NewGuid(), other = Guid.NewGuid();
        dir.WriteMesh(wanted, "grass");
        dir.WriteMesh(other, "grass");

        Dictionary<Guid, ArrayMesh> library =
            ModelLibrary.Load(dir.Path, Registry(dir), new HashSet<Guid> { wanted });

        Assert.Single(library);
        Assert.True(library.ContainsKey(wanted));
    }

    // Byte-identical meshes are realised ONCE and shared. Hundreds of placements of the same prefab
    // resolve to the same cache content, and a copy each would be hundreds of ArrayMeshes on the GPU.
    [Test]
    public void IdenticalMeshesAreRealisedOnceAndShared()
    {
        using var dir = new TempDir();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        byte[] payload = MeshBytes("grass");
        File.WriteAllBytes(Path.Combine(dir.Path, a.ToString("N") + ".mesh"), payload);
        File.WriteAllBytes(Path.Combine(dir.Path, b.ToString("N") + ".mesh"), payload);

        Dictionary<Guid, ArrayMesh> library = ModelLibrary.Load(dir.Path, Registry(dir));

        Assert.Equal(2, library.Count);
        Assert.Same(library[a], library[b]);
    }

    // The authored lower LOD lives under its own suffix, so the same call reads either level — which is
    // what lets a load build both libraries from one cache without a second scan.
    [Test]
    public void TheLowerLodLevelIsReadThroughItsOwnSuffix()
    {
        using var dir = new TempDir();
        Guid guid = Guid.NewGuid();
        dir.WriteMesh(guid, "grass");
        File.WriteAllBytes(Path.Combine(dir.Path, guid.ToString("N") + ".lod"), MeshBytes("grass"));

        Assert.Single(ModelLibrary.Load(dir.Path, Registry(dir), suffix: ".lod"));
        // And the base level is still its own library.
        Assert.Single(ModelLibrary.Load(dir.Path, Registry(dir)));
    }

    // A mesh with no geometry is not realised at all. An empty ArrayMesh in the library would be a draw
    // call that renders nothing, once per placement.
    [Test]
    public void AMeshWithNoGeometryIsNotRealised()
    {
        using var dir = new TempDir();
        Guid guid = Guid.NewGuid();
        using (var ms = new MemoryStream())
        {
            MeshCache.Write(ms, Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(),
                Array.Empty<CachedSubmesh>());
            File.WriteAllBytes(Path.Combine(dir.Path, guid.ToString("N") + ".mesh"), ms.ToArray());
        }

        Assert.Empty(ModelLibrary.Load(dir.Path, Registry(dir)));
    }

    // The failure that matters: a corrupt cache file must cost that one asset, not the load. Before the
    // meshes were guarded, an unreadable file faulted the whole build.
    [Test]
    public void ACorruptCacheFileCostsOneAssetAndNotTheLoad()
    {
        using var dir = new TempDir();
        Guid good = Guid.NewGuid(), corrupt = Guid.NewGuid();
        dir.WriteMesh(good, "grass");
        byte[] damaged = MeshBytes("grass");
        for (int i = 4; i < damaged.Length; i++)
            damaged[i] = 0xFF; // keeps the magic, destroys everything after it
        File.WriteAllBytes(Path.Combine(dir.Path, corrupt.ToString("N") + ".mesh"), damaged);

        Dictionary<Guid, ArrayMesh> library = ModelLibrary.Load(dir.Path, Registry(dir));

        Assert.True(library.ContainsKey(good), "one bad file took the good ones with it");
        Assert.False(library.ContainsKey(corrupt));
    }

    // Files that are not GUID-named are ignored rather than tripping the parse — the cache directory also
    // holds .collider and .tex entries beside the meshes.
    [Test]
    public void NonMeshEntriesInTheCacheAreIgnored()
    {
        using var dir = new TempDir();
        Guid guid = Guid.NewGuid();
        dir.WriteMesh(guid, "grass");
        File.WriteAllText(Path.Combine(dir.Path, "not-a-guid.mesh"), "junk");
        File.WriteAllText(Path.Combine(dir.Path, guid.ToString("N") + ".collider"), "not a mesh");

        Assert.Single(ModelLibrary.Load(dir.Path, Registry(dir)));
    }

    // Materials are shared across the whole load, and a caller can hand in its own table so two libraries
    // of one load (base and lower LOD) share materials instead of building two of each.
    [Test]
    public void MaterialsCanBeSharedAcrossTwoLibrariesOfOneLoad()
    {
        using var dir = new TempDir();
        Guid guid = Guid.NewGuid();
        dir.WriteMesh(guid, "grass");
        File.WriteAllBytes(Path.Combine(dir.Path, guid.ToString("N") + ".lod"), MeshBytes("grass"));
        TextureRegistry registry = Registry(dir);
        var shared = new MaterialTable();

        ModelLibrary.Load(dir.Path, registry, sharedMaterials: shared);
        int afterBase = shared.Count;
        ModelLibrary.Load(dir.Path, registry, suffix: ".lod", sharedMaterials: shared);

        Assert.True(afterBase > 0, "the base library realised no materials at all");
        Assert.Equal(afterBase, shared.Count); // the LOD found them already there
    }

    // A submesh with no texture key still gets a material — an untextured surface is a flat colour, not a
    // missing one.
    [Test]
    public void AnUntexturedSubmeshStillGetsAMaterial()
    {
        using var dir = new TempDir();
        Guid guid = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(dir.Path, guid.ToString("N") + ".mesh"), MeshBytes(""));

        Dictionary<Guid, ArrayMesh> library = ModelLibrary.Load(dir.Path, Registry(dir));

        Assert.Single(library);
        Assert.NotNull(library[guid].SurfaceGetMaterial(0));
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static TextureRegistry Registry(TempDir dir) => new(dir.Path);

    private static byte[] MeshBytes(string textureKey)
    {
        using var ms = new MemoryStream();
        MeshCache.Write(ms,
            new[] { Vector3.Zero, Vector3.Right, Vector3.Up },
            new[] { Vector3.Up, Vector3.Up, Vector3.Up },
            new[] { Vector2.Zero, Vector2.Right, Vector2.Up },
            new[]
            {
                new CachedSubmesh(new[] { 0, 1, 2 }, Colors.White, textureKey,
                    UnityMaterial.Blend.Opaque),
            });
        return ms.ToArray();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-models-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void WriteMesh(Guid guid, string textureKey) =>
            File.WriteAllBytes(System.IO.Path.Combine(Path, guid.ToString("N") + ".mesh"),
                MeshBytes(textureKey));

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
