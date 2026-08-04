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
            new(new[] { 0, 1, 2 }, new Color(0.5f, 0.4f, 0.3f, 1f), "abc", UnityMaterial.Blend.Opaque,
                metallic: 0.8f, smoothness: 0.6f, cull: EShaderCull.TwoSided),
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
        Assert.Equal(0.8f, sm[0].Metallic);
        Assert.Equal(0.6f, sm[0].Smoothness);
        Assert.Equal(EShaderCull.TwoSided, sm[0].Cull);
        Assert.Equal("", sm[1].TextureKey);
        Assert.Equal(UnityMaterial.Blend.Alpha, sm[1].Blend);
        Assert.Equal(0f, sm[1].Metallic);   // defaults: fully matte
        Assert.Equal(0f, sm[1].Smoothness);
        Assert.Equal(EShaderCull.Back, sm[1].Cull); // default: the engine's back-face culling
    }

    // Write bulk-reinterprets the blittable blocks instead of emitting one float at a time. That is only
    // a speed change if the BYTES are unchanged — a different layout would silently invalidate every
    // mesh cache already on disk, and IsCurrent only checks the magic, so a stale file would be read as
    // current and parsed as garbage. This builds the old per-component encoding by hand and demands the
    // two match exactly.
    [Fact]
    public void Write_ProducesTheSameBytesAsThePerComponentEncoding()
    {
        var verts = new[] { new Vector3(1, 2, 3), new Vector3(-4.5f, 5.25f, 6) };
        var normals = new[] { new Vector3(0, 1, 0), new Vector3(1, 0, 0) };
        var uvs = new[] { new Vector2(0.125f, 0), new Vector2(1, 0.75f) };
        var submeshes = new List<CachedSubmesh>
        {
            new(new[] { 0, 1, 2, 3 }, new Color(0.5f, 0.4f, 0.3f, 1f), "abc", UnityMaterial.Blend.Opaque,
                metallic: 0.8f, smoothness: 0.6f, cull: EShaderCull.TwoSided),
        };

        using var actual = new MemoryStream();
        MeshCache.Write(actual, verts, normals, uvs, submeshes);

        using var expected = new MemoryStream();
        using (var w = new BinaryWriter(expected, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0x454D4755u); // MeshCache.Magic
            w.Write(verts.Length);
            foreach (Vector3 v in verts) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(true);
            foreach (Vector3 n in normals) { w.Write(n.X); w.Write(n.Y); w.Write(n.Z); }
            w.Write(true);
            foreach (Vector2 t in uvs) { w.Write(t.X); w.Write(t.Y); }
            w.Write(submeshes.Count);
            foreach (CachedSubmesh sm in submeshes)
            {
                w.Write(sm.TextureKey);
                w.Write(sm.Color.R); w.Write(sm.Color.G); w.Write(sm.Color.B); w.Write(sm.Color.A);
                w.Write((byte)sm.Blend);
                w.Write(sm.Metallic);
                w.Write(sm.Smoothness);
                w.Write((byte)sm.Cull);
                w.Write(sm.Indices.Length);
                foreach (int i in sm.Indices) w.Write(i);
            }
        }

        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    public void IsCurrent_TrueForFreshCache_FalseForStaleOrMissing()
    {
        string dir = Directory.CreateTempSubdirectory("meshcache").FullName;
        try
        {
            string fresh = Path.Combine(dir, "fresh.mesh");
            using (FileStream f = File.Create(fresh))
                MeshCache.Write(f, new[] { new Vector3(1, 1, 1) }, System.Array.Empty<Vector3>(),
                    System.Array.Empty<Vector2>(),
                    new List<CachedSubmesh> { new(new[] { 0 }, Colors.Red, "", UnityMaterial.Blend.Opaque) });
            Assert.True(MeshCache.IsCurrent(fresh));

            string stale = Path.Combine(dir, "stale.mesh");
            File.WriteAllBytes(stale, new byte[] { 0x55, 0x47, 0x4D, 0x32, 0, 0 }); // "UGM2" (old format)
            Assert.False(MeshCache.IsCurrent(stale));

            string shortFile = Path.Combine(dir, "short.mesh");
            File.WriteAllBytes(shortFile, new byte[] { 0x55 });
            Assert.False(MeshCache.IsCurrent(shortFile));

            Assert.False(MeshCache.IsCurrent(Path.Combine(dir, "missing.mesh")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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
    public void IsCurrent_OverBytes_MatchesTheFileCheck()
    {
        // The span overload exists so a caller that already read the file does not open it twice.
        using var ms = new MemoryStream();
        MeshCache.Write(ms, System.Array.Empty<Vector3>(), System.Array.Empty<Vector3>(),
            System.Array.Empty<Vector2>(), System.Array.Empty<CachedSubmesh>());

        Assert.True(MeshCache.IsCurrent(ms.ToArray()));
        Assert.False(MeshCache.IsCurrent(new byte[] { 1, 2, 3 }));          // shorter than the magic
        Assert.False(MeshCache.IsCurrent(new byte[] { 9, 9, 9, 9, 0, 0 })); // an older format version
    }

    [Fact]
    public void Read_BadMagic_Throws()
    {
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<InvalidDataException>(() => MeshCache.Read(stream));
    }

    [Fact]
    public void RoundTrip_LongTextureKey_MultiByteLengthPrefix()
    {
        // A >127-char key forces the 7-bit-encoded string length to span two bytes on read.
        string key = new string('k', 200);
        var verts = new[] { new Vector3(1, 1, 1) };
        var submeshes = new List<CachedSubmesh> { new(new[] { 0 }, Colors.Red, key, UnityMaterial.Blend.Opaque) };

        using var stream = new MemoryStream();
        MeshCache.Write(stream, verts, System.Array.Empty<Vector3>(), System.Array.Empty<Vector2>(), submeshes);
        stream.Position = 0;
        var (_, _, _, sm) = MeshCache.Read(stream);

        Assert.Equal(key, sm[0].TextureKey);
    }
}
