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
        Guid needed = Guid.NewGuid(), unrelated = Guid.NewGuid();

        WriteMesh(meshes, needed, TextureKey.For("california2", 10),
            TextureKey.For("california2", 20), TextureKey.For("core", 30), "");
        WriteMesh(meshes, unrelated, TextureKey.For("california2", 40));
        WriteTexture(textures, TextureKey.For("california2", 10));

        HashSet<long> missing = TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "california2", new[] { needed });

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
        Guid needed = Guid.NewGuid();

        // The second SerializedFile uses the bundle-2 namespace. A current base-tag file with the same
        // path ID must not make the secondary texture appear complete.
        WriteMesh(meshes, needed, TextureKey.For("mod-2", 42));
        WriteTexture(textures, TextureKey.For("mod", 42));
        Assert.Equal(new HashSet<long> { 42 }, TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }));

        WriteTexture(textures, TextureKey.For("mod-2", 42));
        Assert.Empty(TextureDependencyIndex.MissingTextureIds(
            meshes, textures, "mod", new[] { needed }));
    }

    private static void WriteMesh(string directory, Guid guid, params string[] keys)
    {
        var submeshes = new List<CachedSubmesh>();
        foreach (string key in keys)
            submeshes.Add(new CachedSubmesh(Array.Empty<int>(), Colors.White, key,
                UnityMaterial.Blend.Cutout));
        using FileStream stream = File.Create(Path.Combine(directory, guid.ToString("N") + ".mesh"));
        MeshCache.Write(stream, Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector2>(), submeshes);
    }

    private static void WriteTexture(string directory, string key)
    {
        using FileStream stream = File.Create(Path.Combine(directory, key + ".tex"));
        TextureCache.Write(stream, new CachedTexture(4, 1, 1, 1, new byte[] { 255, 255, 255, 255 }));
    }
}
