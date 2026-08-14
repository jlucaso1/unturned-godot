using System;
using System.Collections.Generic;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityMeshFormatTests
{
    // Builds a one-stream mesh with a single position channel of the given format + optional normal.
    private static Dictionary<string, object> Mesh(int format, byte[] vertexData, int vertexCount,
        byte[] indexBuffer, int normalDimension = 0, byte compression = 0, string streamPath = "")
    {
        var channels = new List<object>();
        int posSize = new[] { 4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4 }[format];
        channels.Add(Channel(0, 0, format, 3));
        channels.Add(Channel(0, 3 * posSize, 0, normalDimension)); // normal (Float32)
        for (int i = 2; i < 8; i++)
            channels.Add(Channel(0, 0, 0, 0));

        return new Dictionary<string, object>
        {
            ["m_Name"] = "T",
            ["m_MeshCompression"] = compression,
            ["m_StreamData"] = new Dictionary<string, object> { ["path"] = streamPath, ["offset"] = 0UL, ["size"] = 0u },
            ["m_VertexData"] = new Dictionary<string, object>
            {
                ["m_VertexCount"] = (uint)vertexCount,
                ["m_Channels"] = channels,
                ["m_DataSize"] = vertexData,
            },
            ["m_IndexBuffer"] = indexBuffer,
            ["m_IndexFormat"] = 0,
            ["m_SubMeshes"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["firstByte"] = 0u, ["indexCount"] = (uint)(indexBuffer.Length / 2),
                    ["topology"] = 0, ["firstVertex"] = 0u, ["vertexCount"] = (uint)vertexCount,
                },
            },
        };
    }

    private static Dictionary<string, object> Channel(int stream, int offset, int format, int dim) =>
        new() { ["stream"] = stream, ["offset"] = offset, ["format"] = format, ["dimension"] = dim };

    private static byte[] Indices(params ushort[] idx)
    {
        var b = new byte[idx.Length * 2];
        Buffer.BlockCopy(idx, 0, b, 0, b.Length);
        return b;
    }

    [Fact]
    public void Float32_Positions()
    {
        byte[] vd = new byte[12];
        Buffer.BlockCopy(new[] { 1.5f, 2.5f, 3.5f }, 0, vd, 0, 12);
        UnityMesh m = UnityMesh.Read(Mesh(0, vd, 1, Indices(0, 0, 0)));
        Assert.True(m.Usable);
        Assert.Equal(new Godot.Vector3(1.5f, 2.5f, 3.5f), m.Vertices[0]);
        Assert.Empty(m.Normals); // normal dimension 0
    }

    [Fact]
    public void Float16_Positions()
    {
        var vd = new byte[6];
        BitConverter.GetBytes((Half)1f).CopyTo(vd, 0);
        BitConverter.GetBytes((Half)2f).CopyTo(vd, 2);
        BitConverter.GetBytes((Half)3f).CopyTo(vd, 4);
        UnityMesh m = UnityMesh.Read(Mesh(1, vd, 1, Indices(0, 0, 0)));
        Assert.Equal(1f, m.Vertices[0].X, 3);
        Assert.Equal(3f, m.Vertices[0].Z, 3);
    }

    [Fact]
    public void UNorm8_Positions_AndNormals()
    {
        // position UNorm8 (dim3) + normal Float32 (dim3). stride = 3 + 12 = 15.
        var vd = new byte[15];
        vd[0] = 255; vd[1] = 128; vd[2] = 0;
        Buffer.BlockCopy(new[] { 0f, 1f, 0f }, 0, vd, 3, 12);
        UnityMesh m = UnityMesh.Read(Mesh(2, vd, 1, Indices(0, 0, 0), normalDimension: 3));
        Assert.Equal(1f, m.Vertices[0].X, 3);
        Assert.Equal(0f, m.Vertices[0].Z, 3);
        Assert.Equal(new Godot.Vector3(0, 1, 0), m.Normals[0]);
    }

    [Fact]
    public void SNorm8_Positions()
    {
        // Signed values divide by 127, and -128 clamps to -1 rather than reading below it.
        var vd = new byte[3];
        vd[0] = unchecked((byte)127); vd[1] = unchecked((byte)-64); vd[2] = unchecked((byte)-128);
        UnityMesh m = UnityMesh.Read(Mesh(3, vd, 1, Indices(0, 0, 0)));
        Assert.Equal(1f, m.Vertices[0].X, 3);
        Assert.Equal(-64f / 127f, m.Vertices[0].Y, 3);
        Assert.Equal(-1f, m.Vertices[0].Z, 3);
    }

    [Fact]
    public void UNorm16_Positions()
    {
        var vd = new byte[6];
        BitConverter.GetBytes((ushort)65535).CopyTo(vd, 0);
        BitConverter.GetBytes((ushort)32768).CopyTo(vd, 2);
        BitConverter.GetBytes((ushort)0).CopyTo(vd, 4);
        UnityMesh m = UnityMesh.Read(Mesh(4, vd, 1, Indices(0, 0, 0)));
        Assert.Equal(1f, m.Vertices[0].X, 4);
        Assert.Equal(32768f / 65535f, m.Vertices[0].Y, 4);
        Assert.Equal(0f, m.Vertices[0].Z, 4);
    }

    [Fact]
    public void SNorm16_Positions()
    {
        var vd = new byte[6];
        BitConverter.GetBytes((short)32767).CopyTo(vd, 0);
        BitConverter.GetBytes((short)-16384).CopyTo(vd, 2);
        BitConverter.GetBytes((short)-32768).CopyTo(vd, 4);
        UnityMesh m = UnityMesh.Read(Mesh(5, vd, 1, Indices(0, 0, 0)));
        Assert.Equal(1f, m.Vertices[0].X, 4);
        Assert.Equal(-16384f / 32767f, m.Vertices[0].Y, 4);
        Assert.Equal(-1f, m.Vertices[0].Z, 4); // clamped, not -1.00003
    }

    [Theory]
    [InlineData(6)]  // UInt8
    [InlineData(8)]  // UInt16
    [InlineData(10)] // UInt32
    public void IntegerFormatOnThePositionChannel_MakesTheMeshUnusable(int format)
    {
        // These formats used to fall through to a 4-byte read-as-float, which decoded at the right stride
        // and so produced a mesh that looked fine and was scrambled. A position channel that is not a
        // float format now reads as absent, which is what Usable is for.
        byte[] vd = new byte[12];
        Buffer.BlockCopy(new[] { 4f, 5f, 6f }, 0, vd, 0, 12);

        UnityMesh m = UnityMesh.Read(Mesh(format, vd, 1, Indices(0, 0, 0)));

        Assert.Empty(m.Vertices);
        Assert.False(m.Usable);
    }

    [Fact]
    public void UnknownVertexFormat_MakesTheMeshUnusableRatherThanThrowing()
    {
        // Format 12 is past the end of Unity's VertexFormat enum; sizing it indexed FormatSize out of
        // bounds, which faulted the whole bundle rather than the single mesh that named it.
        var mesh = Mesh(0, new byte[12], 1, Indices(0, 0, 0));
        ((Dictionary<string, object>)((List<object>)((Dictionary<string, object>)
            mesh["m_VertexData"])["m_Channels"])[0])["format"] = 12;

        UnityMesh m = UnityMesh.Read(mesh);

        Assert.False(m.Usable);
        Assert.Empty(m.Vertices);
    }

    [Fact]
    public void MultipleStreams_AlignsSecondStream()
    {
        // Position in stream 0, normal in stream 1. Stream 1 starts 16-byte aligned after stream 0.
        var vd = new byte[28];
        Buffer.BlockCopy(new[] { 1f, 2f, 3f }, 0, vd, 0, 12);   // stream 0
        Buffer.BlockCopy(new[] { 4f, 5f, 6f }, 0, vd, 16, 12);  // stream 1 (offset aligned to 16)

        var channels = new List<object> { Channel(0, 0, 0, 3), Channel(1, 0, 0, 3) };
        for (int i = 2; i < 8; i++)
            channels.Add(Channel(0, 0, 0, 0));

        var mesh = new Dictionary<string, object>
        {
            ["m_Name"] = "T",
            ["m_MeshCompression"] = (byte)0,
            ["m_StreamData"] = new Dictionary<string, object> { ["path"] = "", ["offset"] = 0UL, ["size"] = 0u },
            ["m_VertexData"] = new Dictionary<string, object>
            {
                ["m_VertexCount"] = 1u,
                ["m_Channels"] = channels,
                ["m_DataSize"] = vd,
            },
            ["m_IndexBuffer"] = Indices(0, 0, 0),
            ["m_IndexFormat"] = 0,
            ["m_SubMeshes"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["firstByte"] = 0u, ["indexCount"] = 3u, ["topology"] = 0,
                    ["firstVertex"] = 0u, ["vertexCount"] = 1u,
                },
            },
        };

        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Equal(new Godot.Vector3(1, 2, 3), m.Vertices[0]);
        Assert.Equal(new Godot.Vector3(4, 5, 6), m.Normals[0]);
    }

    [Fact]
    public void ReadsUvChannel()
    {
        // position (ch0) + UV0 (ch4). stride = 12 + 8 = 20.
        var vd = new byte[20];
        Buffer.BlockCopy(new[] { 0f, 0f, 0f }, 0, vd, 0, 12);
        Buffer.BlockCopy(new[] { 0.25f, 0.75f }, 0, vd, 12, 8);

        var channels = new List<object> { Channel(0, 0, 0, 3), Channel(0, 0, 0, 0), Channel(0, 0, 0, 0),
            Channel(0, 0, 0, 0), Channel(0, 12, 0, 2) };
        for (int i = 5; i < 8; i++)
            channels.Add(Channel(0, 0, 0, 0));

        var mesh = Mesh(0, vd, 1, Indices(0, 0, 0));
        ((Dictionary<string, object>)mesh["m_VertexData"])["m_Channels"] = channels;
        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Equal(new Godot.Vector2(0.25f, 0.75f), m.Uvs[0]);
    }

    [Fact]
    public void KeepsSubmeshesSeparate()
    {
        var mesh = Mesh(0, MakeVerts(4), 4, Array.Empty<byte>());
        mesh["m_IndexBuffer"] = Indices(0, 1, 2, 1, 2, 3);
        mesh["m_SubMeshes"] = new List<object>
        {
            new Dictionary<string, object> { ["firstByte"] = 0u, ["indexCount"] = 3u, ["topology"] = 0, ["firstVertex"] = 0u, ["vertexCount"] = 3u },
            new Dictionary<string, object> { ["firstByte"] = 6u, ["indexCount"] = 3u, ["topology"] = 0, ["firstVertex"] = 0u, ["vertexCount"] = 3u },
        };
        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Equal(2, m.Submeshes.Count);
        Assert.Equal(new[] { 0, 1, 2 }, m.Submeshes[0]);
        Assert.Equal(new[] { 1, 2, 3 }, m.Submeshes[1]);
        Assert.Equal(6, m.Indices.Length); // flattened across submeshes
    }

    [Fact]
    public void NonTriangleSubmesh_KeepsEmptySlot()
    {
        var mesh = Mesh(0, MakeVerts(3), 3, Indices(0, 1, 2));
        ((Dictionary<string, object>)((List<object>)mesh["m_SubMeshes"])[0])["topology"] = 1;
        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Single(m.Submeshes);
        Assert.Empty(m.Submeshes[0]); // slot kept aligned with materials
    }

    // Submesh ranges and index values, both of which used to be taken on trust.
    //
    // The buffer overrun threw out of BitConverter and was caught at the bundle level, which turned every
    // object in that bundle into a box. The out-of-range index is the worse one and is silent: it fits the
    // index buffer, so nothing rejected it, and it went into the cache through MeshCache.Write and on to
    // ImporterMesh.AddSurface on the main thread on every warm load for the life of that entry.

    [Theory]
    [InlineData(6u, 3u)]   // firstByte past the end of a 6-byte buffer
    [InlineData(0u, 9u)]   // indexCount claims more indices than the buffer holds
    [InlineData(4u, 3u)]   // starts inside the buffer and runs off the end
    public void SubmeshRangeOutsideTheIndexBuffer_DropsThatSubmesh(uint firstByte, uint indexCount)
    {
        var mesh = Mesh(0, MakeVerts(3), 3, Indices(0, 1, 2));
        var sm = (Dictionary<string, object>)((List<object>)mesh["m_SubMeshes"])[0];
        sm["firstByte"] = firstByte;
        sm["indexCount"] = indexCount;

        UnityMesh m = UnityMesh.Read(mesh);

        Assert.Empty(Assert.Single(m.Submeshes)); // slot kept, aligned with the material palette
        Assert.False(m.Usable);
    }

    [Fact]
    public void SubmeshIndexPastTheVertexCount_DropsThatSubmesh()
    {
        // Three vertices, but the index buffer names vertex 7. This fits the buffer, so only a check
        // against the vertex array catches it.
        var mesh = Mesh(0, MakeVerts(3), 3, Indices(0, 1, 7));

        UnityMesh m = UnityMesh.Read(mesh);

        Assert.Empty(Assert.Single(m.Submeshes));
        Assert.Empty(m.Indices);
        Assert.False(m.Usable);
    }

    [Fact]
    public void OneBadSubmesh_DoesNotTakeTheGoodOneWithIt()
    {
        var mesh = Mesh(0, MakeVerts(4), 4, Array.Empty<byte>());
        mesh["m_IndexBuffer"] = Indices(0, 1, 2, 1, 2, 9); // second submesh names a vertex that is not there
        mesh["m_SubMeshes"] = new List<object>
        {
            new Dictionary<string, object> { ["firstByte"] = 0u, ["indexCount"] = 3u, ["topology"] = 0 },
            new Dictionary<string, object> { ["firstByte"] = 6u, ["indexCount"] = 3u, ["topology"] = 0 },
        };

        UnityMesh m = UnityMesh.Read(mesh);

        Assert.Equal(new[] { 0, 1, 2 }, m.Submeshes[0]);
        Assert.Empty(m.Submeshes[1]);
        Assert.Equal(new[] { 0, 1, 2 }, m.Indices); // the flattened buffer carries only what survived
        Assert.True(m.Usable);
    }

    [Fact]
    public void BaseVertex_MovesTheIndexWindow()
    {
        // Unity keeps 16-bit indices on a mesh with more vertices than they can address and moves the
        // window with baseVertex instead; the GPU adds it to every index at draw time. Ignoring it read
        // the wrong vertices — silently, since the indices are in range either way.
        var mesh = Mesh(0, MakeVerts(6), 6, Indices(0, 1, 2));
        ((Dictionary<string, object>)((List<object>)mesh["m_SubMeshes"])[0])["baseVertex"] = 3u;

        UnityMesh m = UnityMesh.Read(mesh);

        Assert.Equal(new[] { 3, 4, 5 }, m.Submeshes[0]);
    }

    [Fact]
    public void BaseVertexPastTheVertexCount_DropsThatSubmesh()
    {
        var mesh = Mesh(0, MakeVerts(3), 3, Indices(0, 1, 2));
        ((Dictionary<string, object>)((List<object>)mesh["m_SubMeshes"])[0])["baseVertex"] = 2u;

        UnityMesh m = UnityMesh.Read(mesh);

        Assert.Empty(Assert.Single(m.Submeshes)); // 2 + 2 = 4, past the last vertex
    }

    [Fact]
    public void CompressedMesh_NotUsable()
    {
        Assert.False(UnityMesh.Read(Mesh(0, new byte[12], 1, Indices(0), compression: 1)).Usable);
    }

    [Fact]
    public void StreamData_NotUsable()
    {
        Assert.False(UnityMesh.Read(Mesh(0, new byte[12], 1, Indices(0), streamPath: "x.resS")).Usable);
    }

    [Fact]
    public void NonTriangleTopology_ProducesNoIndices()
    {
        Dictionary<string, object> mesh = Mesh(0, new byte[12], 1, Indices(0, 0, 0));
        ((Dictionary<string, object>)((List<object>)mesh["m_SubMeshes"])[0])["topology"] = 1; // line strip
        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Empty(m.Indices);
        Assert.False(m.Usable);
    }

    [Fact]
    public void MissingChannel_ReturnsEmptyNormals()
    {
        // Only a position channel present -> ReadChannel(index 1) is out of range.
        var mesh = Mesh(0, new byte[12], 1, Indices(0, 0, 0));
        ((Dictionary<string, object>)mesh["m_VertexData"])["m_Channels"] = new List<object>
        {
            Channel(0, 0, 0, 3),
        };
        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Empty(m.Normals);
    }

    [Fact]
    public void MissingName_DefaultsToEmpty()
    {
        Dictionary<string, object> mesh = Mesh(0, new byte[12], 1, Indices(0, 0, 0));
        mesh.Remove("m_Name");
        Assert.Equal(string.Empty, UnityMesh.Read(mesh).Name);
    }

    [Fact]
    public void ZeroVertices_NotUsable()
    {
        UnityMesh m = UnityMesh.Read(Mesh(0, Array.Empty<byte>(), 0, Array.Empty<byte>()));
        Assert.Empty(m.Vertices);
        Assert.False(m.Usable);
    }

    [Fact]
    public void Index32Format()
    {
        var mesh = Mesh(0, MakeVerts(2), 2, Array.Empty<byte>());
        mesh["m_IndexFormat"] = 1; // UInt32
        mesh["m_IndexBuffer"] = new byte[] { 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 };
        ((Dictionary<string, object>)((List<object>)mesh["m_SubMeshes"])[0])["indexCount"] = 3u;
        UnityMesh m = UnityMesh.Read(mesh);
        Assert.Equal(new[] { 0, 1, 0 }, m.Indices);
    }

    // A 1-vertex skinned mesh: position in stream 0; BlendWeight (ch12, float4) + BlendIndices (ch13, of
    // the given format) in stream 1; plus one bind pose whose translation-x (e03) is `bindTx`.
    private static Dictionary<string, object> SkinnedMesh(int indexFormat, float[] weights, int[] indices, float bindTx)
    {
        int indexSize = new[] { 4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4 }[indexFormat];
        var vd = new byte[16 + 16 + 4 * indexSize]; // stream1 starts at 16 (aligned after 12-byte stream0)
        Buffer.BlockCopy(new[] { 1f, 2f, 3f }, 0, vd, 0, 12);
        Buffer.BlockCopy(weights, 0, vd, 16, 16);
        for (int i = 0; i < 4; i++)
        {
            byte[] enc = indexSize switch
            {
                1 => new[] { (byte)indices[i] },
                2 => BitConverter.GetBytes((ushort)indices[i]),
                _ => BitConverter.GetBytes(indices[i]),
            };
            Buffer.BlockCopy(enc, 0, vd, 32 + i * indexSize, indexSize);
        }

        var channels = new List<object> { Channel(0, 0, 0, 3) };
        for (int i = 1; i < 12; i++)
            channels.Add(Channel(0, 0, 0, 0));
        channels.Add(Channel(1, 0, 0, 4));            // ch12 BlendWeight float4
        channels.Add(Channel(1, 16, indexFormat, 4)); // ch13 BlendIndices

        var bindPose = new Dictionary<string, object>();
        for (int row = 0; row < 4; row++)
            for (int col = 0; col < 4; col++)
                bindPose[$"e{row}{col}"] = row == col ? 1f : 0f;
        bindPose["e03"] = bindTx;

        return new Dictionary<string, object>
        {
            ["m_Name"] = "T",
            ["m_MeshCompression"] = (byte)0,
            ["m_StreamData"] = new Dictionary<string, object> { ["path"] = "", ["offset"] = 0UL, ["size"] = 0u },
            ["m_VertexData"] = new Dictionary<string, object>
            {
                ["m_VertexCount"] = 1u,
                ["m_Channels"] = channels,
                ["m_DataSize"] = vd,
            },
            ["m_IndexBuffer"] = Indices(0, 0, 0),
            ["m_IndexFormat"] = 0,
            ["m_SubMeshes"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["firstByte"] = 0u, ["indexCount"] = 3u, ["topology"] = 0,
                    ["firstVertex"] = 0u, ["vertexCount"] = 1u,
                },
            },
            ["m_BindPose"] = new List<object> { bindPose },
        };
    }

    [Fact]
    public void ReadsSkinning_WeightsAndBindPose()
    {
        UnityMesh m = UnityMesh.Read(SkinnedMesh(10, new[] { 0.5f, 0.3f, 0.2f, 0f }, new[] { 1, 2, 3, 0 }, 7f));
        Assert.Equal(new[] { 0.5f, 0.3f, 0.2f, 0f }, m.BoneWeights);
        Assert.Equal(new[] { 1, 2, 3, 0 }, m.BoneIndices);
        Assert.Equal(7f, Assert.Single(m.BindPoses)[12]); // e03 (translation x) at column-major index 12
    }

    [Theory]
    [InlineData(6)]  // UInt8
    [InlineData(8)]  // UInt16
    [InlineData(10)] // UInt32
    public void ReadsBlendIndices_AcrossFormats(int format)
    {
        UnityMesh m = UnityMesh.Read(SkinnedMesh(format, new[] { 1f, 0f, 0f, 0f }, new[] { 5, 6, 7, 8 }, 0f));
        Assert.Equal(new[] { 5, 6, 7, 8 }, m.BoneIndices);
    }

    [Fact]
    public void NonSkinnedMesh_HasNoBoneData()
    {
        UnityMesh m = UnityMesh.Read(Mesh(0, new byte[12], 1, Indices(0, 0, 0)));
        Assert.Empty(m.BoneWeights);
        Assert.Empty(m.BoneIndices);
        Assert.Empty(m.BindPoses);
    }

    private static byte[] MakeVerts(int count)
    {
        var vd = new byte[count * 12];
        for (int i = 0; i < count; i++)
            Buffer.BlockCopy(new[] { (float)i, i, i }, 0, vd, i * 12, 12);
        return vd;
    }
}
