using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TextureDependencyIndexTests
{
    [Fact]
    public void MissingTextureIds_FindsInterruptedStreamingTailForNeededMeshesOnly()
    {
        using var dir = new TempDir();
        string meshes = Directory.CreateDirectory(Path.Combine(dir.Path, "meshes")).FullName;
        string textures = Directory.CreateDirectory(Path.Combine(dir.Path, "textures")).FullName;
        string bundle = dir.Write("california2.masterbundle", new byte[] { 1 });
        long stamp = ExtractionIndex.StampFor(bundle);
        Guid needed = Guid.NewGuid(), unrelated = Guid.NewGuid();

        WriteMesh(meshes, needed, TextureKey.For("california2", 10),
            TextureKey.For("california2", 20), TextureKey.For("core", 30), "");
        WriteMesh(meshes, unrelated, TextureKey.For("california2", 40));
        WriteTexture(textures, TextureKey.For("california2", 10), bundle, stamp);

        HashSet<long> missing = TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "california2", new[] { needed }, bundle, stamp);

        Assert.Equal(new HashSet<long> { 20 }, missing);
    }

    [Fact]
    public void NeededTextureIds_DeduplicatesAndSkipsMissingStaleAndCorruptMeshes()
    {
        using var dir = new TempDir();
        Guid good = Guid.NewGuid(), missing = Guid.NewGuid(), stale = Guid.NewGuid(), corrupt = Guid.NewGuid();
        string key = TextureKey.For("mod", 99);
        WriteMesh(dir.Path, good, key, key);
        File.WriteAllBytes(Path.Combine(dir.Path, stale.ToString("N") + ".mesh"), new byte[] { 1, 2, 3, 4 });

        // Current magic followed by a truncated body: should not abort inspection of other entries.
        using (var valid = new MemoryStream())
        {
            MeshCache.Write(valid, Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(),
                Array.Empty<CachedSubmesh>());
            File.WriteAllBytes(Path.Combine(dir.Path, corrupt.ToString("N") + ".mesh"), valid.ToArray()[..5]);
        }

        Assert.Equal(new HashSet<long> { 99 }, TextureDependencyIndex.NeededTextureIds(
            dir.Path, "mod", new[] { missing, stale, good, corrupt }));
    }

    [Fact]
    public void MissingTextureIds_RecognizesSecondarySerializedFileTags()
    {
        using var dir = new TempDir();
        string meshes = Directory.CreateDirectory(Path.Combine(dir.Path, "meshes")).FullName;
        string textures = Directory.CreateDirectory(Path.Combine(dir.Path, "textures")).FullName;
        string bundle = dir.Write("mod.masterbundle", new byte[] { 1 });
        long stamp = ExtractionIndex.StampFor(bundle);
        Guid needed = Guid.NewGuid();

        // The second SerializedFile uses the bundle-2 namespace. A current base-tag file with the same
        // path ID must not make the secondary texture appear complete.
        WriteMesh(meshes, needed, TextureKey.For("mod-2", 42));
        WriteTexture(textures, TextureKey.For("mod", 42), bundle, stamp);
        Assert.Equal(new HashSet<long> { 42 }, TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }, bundle, stamp));

        WriteTexture(textures, TextureKey.For("mod-2", 42), bundle, stamp);
        Assert.Empty(TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }, bundle, stamp));

        // A bundle update can keep the same PathID and cache key. Its old pixels are still stale.
        Assert.Equal(new HashSet<long> { 42 }, TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }, bundle, stamp + 1));
        Assert.Equal(1, TextureDependencyIndex.RemoveStaleTextures(
            meshes, textures, "mod", new[] { needed }, bundle, stamp + 1));
        Assert.False(File.Exists(Path.Combine(textures, TextureKey.For("mod-2", 42) + ".tex")));
    }

    [Fact]
    public void TheAuthoredLowerLevelsOwnTexturesAreNeededAndPlannedToo()
    {
        using var dir = new TempDir();
        string meshes = Directory.CreateDirectory(Path.Combine(dir.Path, "meshes")).FullName;
        string textures = Directory.CreateDirectory(Path.Combine(dir.Path, "textures")).FullName;
        string bundle = dir.Write("mod.masterbundle", new byte[] { 1 });
        long stamp = ExtractionIndex.StampFor(bundle);
        Guid needed = Guid.NewGuid();

        // The lower level can bake several of the base level's materials into an atlas of its own, so its
        // texture is not necessarily one the base mesh references. Planning from the base alone would
        // leave that atlas undecoded and the level untextured everywhere past the switch distance.
        WriteMesh(meshes, needed, TextureKey.For("mod", 10));
        WriteLod1Mesh(meshes, needed, TextureKey.For("mod", 11));

        Assert.Equal(new HashSet<long> { 10, 11 },
            TextureDependencyIndex.NeededTextureIds(meshes, "mod", new[] { needed }));
        Assert.Equal(new HashSet<long> { 10, 11 }, TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }, bundle, stamp));

        WriteTexture(textures, TextureKey.For("mod", 10), bundle, stamp);
        WriteTexture(textures, TextureKey.For("mod", 11), bundle, stamp);
        Assert.Empty(TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }, bundle, stamp));

        // And a prefab that never shipped a lower level is unaffected: no file, nothing extra planned.
        Guid plain = Guid.NewGuid();
        WriteMesh(meshes, plain, TextureKey.For("mod", 12));
        Assert.Equal(new HashSet<long> { 12 },
            TextureDependencyIndex.NeededTextureIds(meshes, "mod", new[] { plain }));

        // A lower level truncated after its 4-byte header still passes the magic check, so the read has to
        // survive it. The base mesh beside it must still be planned.
        Guid halfWritten = Guid.NewGuid();
        WriteMesh(meshes, halfWritten, TextureKey.For("mod", 13));
        byte[] whole = File.ReadAllBytes(Path.Combine(meshes, halfWritten.ToString("N") + ".mesh"));
        File.WriteAllBytes(Path.Combine(meshes, halfWritten.ToString("N") + MeshCache.Lod1Suffix), whole[..5]);
        Assert.Equal(new HashSet<long> { 13 },
            TextureDependencyIndex.NeededTextureIds(meshes, "mod", new[] { halfWritten }));
    }

    private static void WriteMesh(string directory, Guid guid, params string[] keys) =>
        WriteMeshLevel(directory, guid, ".mesh", keys);

    private static void WriteLod1Mesh(string directory, Guid guid, params string[] keys) =>
        WriteMeshLevel(directory, guid, MeshCache.Lod1Suffix, keys);

    private static void WriteMeshLevel(string directory, Guid guid, string suffix, string[] keys)
    {
        var submeshes = new List<CachedSubmesh>();
        foreach (string key in keys)
            submeshes.Add(new CachedSubmesh(Array.Empty<int>(), Colors.White, key,
                UnityMaterial.Blend.Cutout));
        using FileStream stream = File.Create(Path.Combine(directory, guid.ToString("N") + suffix));
        MeshCache.Write(stream, Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(), submeshes);
    }

    private static void WriteTexture(string directory, string key, string bundlePath, long stamp)
    {
        string path = Path.Combine(directory, key + ".tex");
        using FileStream stream = File.Create(path);
        TextureCache.Write(stream, new CachedTexture(4, 1, 1, 1, new byte[] { 255, 255, 255, 255 }));
        stream.Dispose();
        TextureCache.RecordSource(path, bundlePath, stamp);
    }
}
