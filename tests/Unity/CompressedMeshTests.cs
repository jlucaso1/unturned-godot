using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class CompressedMeshTests
{
    private const int Bits = 16;
    private const float Max = (1 << Bits) - 1;

    // Quantizes floats into a PackedBitVector node the way Unity's importer does.
    private static Dictionary<string, object> Floats(float start, float range, params float[] values)
    {
        var quantized = new uint[values.Length];
        for (int i = 0; i < values.Length; i++)
            quantized[i] = range > 0f ? (uint)Mathf.Round((values[i] - start) / range * Max) : 0u;

        return UInts(Bits, quantized, range, start);
    }

    private static Dictionary<string, object> UInts(int bitSize, uint[] values, float range = 0f,
        float start = 0f)
    {
        var data = new byte[((values.Length * bitSize) + 7) / 8];
        int bitPos = 0;
        foreach (uint value in values)
            for (int bit = 0; bit < bitSize; bit++, bitPos++)
                if (((value >> bit) & 1) != 0)
                    data[bitPos / 8] |= (byte)(1 << (bitPos % 8));

        return new Dictionary<string, object>
        {
            ["m_NumItems"] = values.Length,
            ["m_Range"] = range,
            ["m_Start"] = start,
            ["m_Data"] = data,
            ["m_BitSize"] = bitSize,
        };
    }

    // A unit cube corner triangle: three vertices, three indices, one UV channel.
    private static Dictionary<string, object> Mesh(uint uvInfo, int uvComponents = 2)
    {
        var uvs = new List<float>();
        for (int i = 0; i < 3; i++)
        {
            uvs.Add(i * 0.5f);      // u
            uvs.Add(1f - (i * 0.5f)); // v
            for (int extra = 2; extra < uvComponents; extra++)
                uvs.Add(0.25f);      // a third/fourth component the reader must skip over
        }

        return new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 4f, 0, 0, 0, 4, 0, 0, 0, 4, 0),
            ["m_UV"] = Floats(0f, 1f, uvs.ToArray()),
            ["m_UVInfo"] = uvInfo,
            ["m_Normals"] = Floats(-1f, 2f, 0, 0, 0, 0, 0, 0),           // x=y=0 -> z = +-1
            ["m_NormalSigns"] = UInts(1, new uint[] { 1, 0, 1 }),
            ["m_Triangles"] = UInts(8, new uint[] { 0, 1, 2 }),
        };
    }

    [Fact]
    public void Read_RebuildsVerticesTrianglesAndUnitNormals()
    {
        CompressedMesh mesh = CompressedMesh.Read(Mesh(uvInfo: 0));

        Assert.True(mesh.HasGeometry);
        Assert.Equal(3, mesh.Vertices.Length);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Triangles);

        Assert.Equal(0f, mesh.Vertices[0].X, 3);
        Assert.Equal(4f, mesh.Vertices[1].X, 3);
        Assert.Equal(4f, mesh.Vertices[2].Y, 3);

        // The sign vector flips Z; every normal must come back on the unit sphere.
        Assert.Equal(3, mesh.Normals.Length);
        Assert.Equal(1f, mesh.Normals[0].Z, 3);
        Assert.Equal(-1f, mesh.Normals[1].Z, 3);
        foreach (Vector3 normal in mesh.Normals)
            Assert.Equal(1f, normal.Length(), 3);
    }

    [Fact]
    public void Read_WithoutAChannelTable_TakesTheVectorAsUv0()
    {
        CompressedMesh mesh = CompressedMesh.Read(Mesh(uvInfo: 0));

        Assert.Equal(3, mesh.Uvs.Length);
        Assert.Equal(0f, mesh.Uvs[0].X, 3);
        Assert.Equal(1f, mesh.Uvs[0].Y, 3);
        Assert.Equal(1f, mesh.Uvs[2].X, 3);
    }

    [Fact]
    public void Read_ChannelTable_DrivesTheComponentCount()
    {
        // Channel 0 present with three components (bits: exists | dimension-1).
        const uint threeComponentUv0 = 4u | 2u;
        CompressedMesh mesh = CompressedMesh.Read(Mesh(threeComponentUv0, uvComponents: 3));

        Assert.Equal(3, mesh.Uvs.Length);
        Assert.Equal(0f, mesh.Uvs[0].X, 3);
        Assert.Equal(1f, mesh.Uvs[0].Y, 3);
        Assert.Equal(0.5f, mesh.Uvs[1].X, 3); // the skipped third component did not shift the read
        Assert.Equal(1f, mesh.Uvs[2].X, 3);
    }

    [Fact]
    public void Read_ChannelTable_SkipsAbsentChannels()
    {
        // Nothing on channel 0; channel 1 exists with two components, so it is the one to sample.
        const uint uv1Only = (4u | 1u) << 4;
        CompressedMesh mesh = CompressedMesh.Read(Mesh(uv1Only));

        Assert.Equal(3, mesh.Uvs.Length);
        Assert.Equal(0f, mesh.Uvs[0].X, 3);
    }

    [Fact]
    public void Read_OffSphereNormals_AreRenormalized()
    {
        // Quantization can leave x²+y² slightly over 1; the reader must not take a negative square root.
        var mesh = new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0),
            ["m_Normals"] = Floats(0f, 1f, 1f, 1f),
            ["m_NormalSigns"] = UInts(1, new uint[] { 1 }),
            ["m_Triangles"] = UInts(8, new uint[] { 0, 1, 2 }),
        };

        CompressedMesh result = CompressedMesh.Read(mesh);

        Vector3 normal = Assert.Single(result.Normals);
        Assert.Equal(1f, normal.Length(), 3);
        Assert.Equal(0f, normal.Z, 3);
    }

    [Fact]
    public void Read_TrianglesNotAMultipleOfThree_DropsTheRemainder()
    {
        var mesh = new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 1f, 0, 0, 0, 1, 0, 0, 0, 1, 0),
            ["m_Triangles"] = UInts(8, new uint[] { 0, 1, 2, 0 }),
        };

        Assert.Equal(new[] { 0, 1, 2 }, CompressedMesh.Read(mesh).Triangles);
    }

    [Fact]
    public void Read_WithoutVerticesOrNode_HasNoGeometry()
    {
        Assert.False(CompressedMesh.Read(null).HasGeometry);
        Assert.False(CompressedMesh.Read("not a node").HasGeometry);
        Assert.False(CompressedMesh.Read(new Dictionary<string, object>()).HasGeometry);

        // Vertices but no triangles is not renderable either.
        var noTriangles = new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 1f, 0, 0, 0),
        };
        CompressedMesh mesh = CompressedMesh.Read(noTriangles);
        Assert.False(mesh.HasGeometry);
        Assert.Single(mesh.Vertices);
        Assert.Empty(mesh.Normals);
        Assert.Empty(mesh.Uvs);
    }

    // A Unity Mesh whose geometry lives in m_CompressedMesh, as UnityMesh.Read receives it.
    private static Dictionary<string, object> UnityCompressedMesh(int indexFormat,
        params (int FirstByte, int IndexCount, int Topology)[] submeshes)
    {
        var table = new List<object>();
        foreach ((int firstByte, int indexCount, int topology) in submeshes)
            table.Add(new Dictionary<string, object>
            {
                ["firstByte"] = firstByte,
                ["indexCount"] = indexCount,
                ["topology"] = topology,
            });

        return new Dictionary<string, object>
        {
            ["m_Name"] = "Pipe_Cap",
            ["m_MeshCompression"] = 1,
            ["m_IndexFormat"] = indexFormat,
            ["m_SubMeshes"] = table,
            ["m_CompressedMesh"] = SixTriangleMesh(),
        };
    }

    // Two triangles over six vertices, so a submesh table has something to slice.
    private static Dictionary<string, object> SixTriangleMesh() => new()
    {
        ["m_Vertices"] = Floats(0f, 6f, 0, 0, 0, 1, 0, 0, 0, 1, 0, 2, 0, 0, 3, 0, 0, 3, 1, 0),
        ["m_Triangles"] = UInts(8, new uint[] { 0, 1, 2, 3, 4, 5 }),
    };

    [Fact]
    public void UnityMesh_ReadsACompressedMesh_AndSlicesItsSubmeshes()
    {
        // 16-bit indices: firstByte 6 is index 3, where the second triangle starts.
        UnityMesh mesh = UnityMesh.Read(UnityCompressedMesh(indexFormat: 0, (0, 3, 0), (6, 3, 0)));

        Assert.True(mesh.Usable);
        Assert.Equal("Pipe_Cap", mesh.Name);
        Assert.Equal(6, mesh.Vertices.Length);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, mesh.Indices);
        Assert.Equal(2, mesh.Submeshes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, mesh.Submeshes[0]);
        Assert.Equal(new[] { 3, 4, 5 }, mesh.Submeshes[1]);
        Assert.Empty(mesh.BindPoses); // no m_BindPose on a static mesh
    }

    [Fact]
    public void UnityMesh_CompressedWith32BitIndices_UsesTheDeclaredElementSize()
    {
        // The same second triangle, addressed with 4-byte indices.
        UnityMesh mesh = UnityMesh.Read(UnityCompressedMesh(indexFormat: 1, (12, 3, 0)));

        int[] only = Assert.Single(mesh.Submeshes);
        Assert.Equal(new[] { 3, 4, 5 }, only);
    }

    [Fact]
    public void UnityMesh_CompressedSubmeshes_DropUnusableEntriesButKeepAlignment()
    {
        // A line-topology submesh, one that runs past the triangle list, and one that is empty: each
        // stays in place so the material list still lines up.
        UnityMesh mesh = UnityMesh.Read(UnityCompressedMesh(indexFormat: 0,
            (0, 3, 5), (0, 99, 0), (0, 0, 0), (6, 3, 0)));

        Assert.Equal(4, mesh.Submeshes.Count);
        Assert.Empty(mesh.Submeshes[0]);
        Assert.Empty(mesh.Submeshes[1]);
        Assert.Empty(mesh.Submeshes[2]);
        Assert.Equal(new[] { 3, 4, 5 }, mesh.Submeshes[3]);
    }

    [Fact]
    public void UnityMesh_CompressedWithoutASubmeshTable_RendersAsOneSurface()
    {
        UnityMesh mesh = UnityMesh.Read(UnityCompressedMesh(indexFormat: 0));

        int[] only = Assert.Single(mesh.Submeshes);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, only);
    }

    [Fact]
    public void UnityMesh_CompressedButEmpty_IsNotUsable()
    {
        var mesh = new Dictionary<string, object>
        {
            ["m_Name"] = "Empty",
            ["m_MeshCompression"] = 2,
            ["m_IndexFormat"] = 0,
            ["m_SubMeshes"] = new List<object>(),
            ["m_CompressedMesh"] = new Dictionary<string, object>(),
        };

        UnityMesh result = UnityMesh.Read(mesh);

        Assert.False(result.Usable);
        Assert.Empty(result.Vertices);
    }

    [Fact]
    public void Read_FewerSignsThanNormals_LeavesTheRestPositive()
    {
        var mesh = new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 1f, 0, 0, 0, 1, 0, 0),
            ["m_Normals"] = Floats(-1f, 2f, 0, 0, 0, 0),
            ["m_NormalSigns"] = UInts(1, new uint[] { 0 }), // only the first normal has a sign
            ["m_Triangles"] = UInts(8, new uint[] { 0, 1, 0 }),
        };

        CompressedMesh result = CompressedMesh.Read(mesh);

        Assert.Equal(2, result.Normals.Length);
        Assert.Equal(-1f, result.Normals[0].Z, 3);
        Assert.Equal(1f, result.Normals[1].Z, 3);
    }

    [Fact]
    public void Read_WithoutAUvVector_HasNoUvs()
    {
        var mesh = new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 1f, 0, 0, 0),
            ["m_Triangles"] = UInts(8, new uint[] { 0, 0, 0 }),
        };

        Assert.Empty(CompressedMesh.Read(mesh).Uvs);
    }

    [Fact]
    public void Read_UvVectorWithoutAnInfoField_IsTreatedAsUv0()
    {
        // Meshes written before Unity 5 have no channel table at all, not even a zeroed one.
        var mesh = new Dictionary<string, object>
        {
            ["m_Vertices"] = Floats(0f, 1f, 0, 0, 0, 1, 0, 0),
            ["m_UV"] = Floats(0f, 1f, 0f, 1f, 1f, 0f),
            ["m_Triangles"] = UInts(8, new uint[] { 0, 1, 0 }),
        };

        CompressedMesh result = CompressedMesh.Read(mesh);

        Assert.Equal(2, result.Uvs.Length);
        Assert.Equal(0f, result.Uvs[0].X, 3);
        Assert.Equal(1f, result.Uvs[0].Y, 3);
        Assert.Equal(1f, result.Uvs[1].X, 3);
    }

    [Fact]
    public void Read_ChannelTableWithNoChannels_HasNoUvs()
    {
        // A non-zero info word whose channels are all marked absent.
        Assert.Empty(CompressedMesh.Read(Mesh(uvInfo: 1u)).Uvs);
    }

    [Fact]
    public void Read_OneComponentChannel_IsNotUsableAsAUv()
    {
        // Channel 0 exists with a single component: there is no V to sample, and the reader moves on
        // (there is no other channel here, so nothing comes back).
        Assert.Empty(CompressedMesh.Read(Mesh(uvInfo: 4u)).Uvs);
    }

    [Fact]
    public void Read_ChannelPastTheEndOfTheVector_FallsThroughToNothing()
    {
        // Channel 0 claims four components, twice what the vector holds, so the read cannot be served.
        Assert.Empty(CompressedMesh.Read(Mesh(uvInfo: 4u | 3u)).Uvs);
    }
}
