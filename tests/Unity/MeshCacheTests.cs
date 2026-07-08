using System.IO;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class MeshCacheTests
{
    [Fact]
    public void RoundTrip_WithNormals()
    {
        var verts = new[] { new Vector3(1, 2, 3), new Vector3(4, 5, 6) };
        var normals = new[] { new Vector3(0, 1, 0), new Vector3(1, 0, 0) };
        var indices = new[] { 0, 1, 0 };

        using var stream = new MemoryStream();
        MeshCache.Write(stream, verts, normals, indices);
        stream.Position = 0;
        var (v, n, idx) = MeshCache.Read(stream);

        Assert.Equal(verts, v);
        Assert.Equal(normals, n);
        Assert.Equal(indices, idx);
    }

    [Fact]
    public void RoundTrip_WithoutNormals()
    {
        var verts = new[] { new Vector3(1, 1, 1) };
        using var stream = new MemoryStream();
        MeshCache.Write(stream, verts, System.Array.Empty<Vector3>(), new[] { 0 });
        stream.Position = 0;
        var (v, n, idx) = MeshCache.Read(stream);

        Assert.Single(v);
        Assert.Empty(n);
        Assert.Equal(new[] { 0 }, idx);
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => MeshCache.Read(stream));
    }
}
