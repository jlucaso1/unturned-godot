using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class MeshCacheTests
{
    [Fact]
    public void RoundTrip_WithNormalsUvsAndSubmeshes()
    {
        var verts = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6), new Vector3(7, 8, 9) };
        var normals = new[] { new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1) };
        var uvs = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1) };
        var submeshes = new List<CachedSubmesh>
        {
            new(new[] { 0, 1, 2 }, new Color(0.5f, 0.4f, 0.3f, 1f), "abc", UnityMaterial.Blend.Opaque),
            new(new[] { 2, 1, 0 }, Colors.White, "", UnityMaterial.Blend.Alpha),
        };

        using var stream = new MemoryStream();
        MeshCache.Write(stream, verts, normals, uvs, submeshes);
        stream.Position = 0;
        var (v, n, u, sm) = MeshCache.Read(stream);

        Assert.Equal(verts, v);
        Assert.Equal(normals, n);
        Assert.Equal(uvs, u);
        Assert.Equal(2, sm.Count);
        Assert.Equal("abc", sm[0].TextureKey);
        Assert.Equal(new Color(0.5f, 0.4f, 0.3f, 1f), sm[0].Color);
        Assert.Equal(new[] { 0, 1, 2 }, sm[0].Indices);
        Assert.Equal(UnityMaterial.Blend.Opaque, sm[0].Blend);
        Assert.Equal("", sm[1].TextureKey);
        Assert.Equal(UnityMaterial.Blend.Alpha, sm[1].Blend);
    }

    [Fact]
    public void RoundTrip_WithoutNormalsOrUvs()
    {
        var verts = new[] { new Vector3(1, 1, 1) };
        var submeshes = new List<CachedSubmesh> { new(new[] { 0 }, Colors.Red, "", UnityMaterial.Blend.Opaque) };

        using var stream = new MemoryStream();
        MeshCache.Write(stream, verts, System.Array.Empty<Vector3>(), System.Array.Empty<Vector2>(), submeshes);
        stream.Position = 0;
        var (v, n, u, sm) = MeshCache.Read(stream);

        Assert.Single(v);
        Assert.Empty(n);
        Assert.Empty(u);
        Assert.Single(sm);
        Assert.Equal(Colors.Red, sm[0].Color);
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => MeshCache.Read(stream));
    }
}
