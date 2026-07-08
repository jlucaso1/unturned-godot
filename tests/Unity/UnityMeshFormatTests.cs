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
    public void UInt32Format_UsesDefaultComponentRead()
    {
        // Format 10 (UInt32, 4 bytes) exercises the fallback read-as-float path.
        byte[] vd = new byte[12];
        Buffer.BlockCopy(new[] { 4f, 5f, 6f }, 0, vd, 0, 12);
        UnityMesh m = UnityMesh.Read(Mesh(10, vd, 1, Indices(0, 0, 0)));
        Assert.Equal(new Godot.Vector3(4f, 5f, 6f), m.Vertices[0]);
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

    private static byte[] MakeVerts(int count)
    {
        var vd = new byte[count * 12];
        for (int i = 0; i < count; i++)
            Buffer.BlockCopy(new[] { (float)i, i, i }, 0, vd, i * 12, 12);
        return vd;
    }
}
