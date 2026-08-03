using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

// Validates the mesh interpreter against a real mesh extracted from the bundle (ground-truth
// vertex positions captured via UnityPy's OBJ export).
public class UnityMeshTests
{
    private static Dictionary<string, object> LoadFixtureMesh(out float[][] expectedPositions)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mesh_light2.json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;

        var channels = new List<object>();
        foreach (JsonElement c in root.GetProperty("channels").EnumerateArray())
            channels.Add(new Dictionary<string, object>
            {
                ["stream"] = c.GetProperty("stream").GetInt32(),
                ["offset"] = c.GetProperty("offset").GetInt32(),
                ["format"] = c.GetProperty("format").GetInt32(),
                ["dimension"] = c.GetProperty("dimension").GetInt32(),
            });

        var sm = root.GetProperty("submesh");
        var mesh = new Dictionary<string, object>
        {
            ["m_Name"] = root.GetProperty("name").GetString()!,
            ["m_MeshCompression"] = (byte)root.GetProperty("meshCompression").GetInt32(),
            ["m_StreamData"] = new Dictionary<string, object>
            {
                ["path"] = root.GetProperty("streamData").GetProperty("path").GetString()!,
                ["offset"] = (ulong)root.GetProperty("streamData").GetProperty("offset").GetInt64(),
                ["size"] = (uint)root.GetProperty("streamData").GetProperty("size").GetInt32(),
            },
            ["m_VertexData"] = new Dictionary<string, object>
            {
                ["m_VertexCount"] = (uint)root.GetProperty("vertexCount").GetInt32(),
                ["m_Channels"] = channels,
                ["m_DataSize"] = Convert.FromBase64String(root.GetProperty("vertexData_b64").GetString()!),
            },
            ["m_IndexBuffer"] = Convert.FromBase64String(root.GetProperty("indexBuffer_b64").GetString()!),
            ["m_IndexFormat"] = root.GetProperty("indexFormat").GetInt32(),
            ["m_SubMeshes"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["firstByte"] = (uint)sm.GetProperty("firstByte").GetInt32(),
                    ["indexCount"] = (uint)sm.GetProperty("indexCount").GetInt32(),
                    ["topology"] = sm.GetProperty("topology").GetInt32(),
                    ["firstVertex"] = (uint)sm.GetProperty("firstVertex").GetInt32(),
                    ["vertexCount"] = (uint)sm.GetProperty("vertexCount").GetInt32(),
                },
            },
        };

        var list = new List<float[]>();
        foreach (JsonElement p in root.GetProperty("expectedPositions").EnumerateArray())
        {
            var v = new List<float>();
            foreach (JsonElement f in p.EnumerateArray())
                v.Add(f.GetSingle());
            list.Add(v.ToArray());
        }
        expectedPositions = list.ToArray();
        return mesh;
    }

    [Fact]
    public void DecodesRealMesh_PositionsMatchGroundTruth()
    {
        Dictionary<string, object> dict = LoadFixtureMesh(out float[][] expected);
        UnityMesh mesh = UnityMesh.Read(dict);

        Assert.True(mesh.Usable);
        Assert.Equal(expected.Length, mesh.Vertices.Length);
        Assert.Equal(6, mesh.Indices.Length); // 2 triangles

        // UnityPy's OBJ export negates X (Unity LH -> OBJ RH); our reader keeps raw Unity coords,
        // so raw.X == -objX. Y and Z match directly.
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(-expected[i][0], mesh.Vertices[i].X, 4);
            Assert.Equal(expected[i][1], mesh.Vertices[i].Y, 4);
            Assert.Equal(expected[i][2], mesh.Vertices[i].Z, 4);
        }
    }

    // Unity keeps flags in the high nibble of a channel's `dimension`, so the field is not the component
    // count it looks like. The game's own vehicle wheels ship a normals channel written as 52 — four
    // Float16 components with 0x3 above them — and reading it whole took the stream's stride from 32
    // bytes to 116: every vertex then decoded from the wrong offset, and the buffer looked far shorter
    // than the vertex count needed, which dropped the mesh entirely.
    [Fact]
    public void ChannelDimensionIgnoresTheFlagsUnityPacksAboveIt()
    {
        Dictionary<string, object> dict = LoadFixtureMesh(out float[][] expected);
        var channels = (List<object>)((Dictionary<string, object>)dict["m_VertexData"])["m_Channels"];

        // Same mesh, with the flags the wheels carry set on every channel that has any components.
        foreach (object c in channels)
        {
            var channel = (Dictionary<string, object>)c;
            int dimension = Convert.ToInt32(channel["dimension"]);
            if (dimension > 0)
                channel["dimension"] = dimension | 0x30;
        }

        UnityMesh mesh = UnityMesh.Read(dict);

        Assert.True(mesh.Usable);
        Assert.Equal(expected.Length, mesh.Vertices.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(-expected[i][0], mesh.Vertices[i].X, 4);
    }

    // A mesh whose vertex buffer Unity moved into a .resS decodes from the bytes the caller read out of
    // that stream, and the layout inside them is the inline one — same channels, same stride.
    [Fact]
    public void ReadsAStreamedVertexBufferFromTheBytesTheCallerSupplies()
    {
        Dictionary<string, object> dict = LoadFixtureMesh(out float[][] expected);
        var vertexData = (Dictionary<string, object>)dict["m_VertexData"];
        byte[] buffer = (byte[])vertexData["m_DataSize"];

        // What a streamed mesh actually looks like on disk: the inline buffer is empty and m_StreamData
        // names the range instead.
        vertexData["m_DataSize"] = Array.Empty<byte>();
        dict["m_StreamData"] = new Dictionary<string, object>
        {
            ["path"] = "archive:/CAB-abc/CAB-abc.resS",
            ["offset"] = 4096UL,
            ["size"] = (uint)buffer.Length,
        };

        UnityMesh.StreamRef stream = UnityMesh.ReadStreamRef(dict);
        Assert.True(stream.IsStreamed);
        Assert.Equal("CAB-abc.resS", stream.FileName);
        Assert.Equal(4096, stream.Offset);
        Assert.Equal(buffer.Length, stream.Size);

        // Without the bytes the mesh stays unusable, exactly as it did before this reader could ask.
        Assert.False(UnityMesh.Read(dict).Usable);

        UnityMesh mesh = UnityMesh.Read(dict, buffer);
        Assert.True(mesh.Usable);
        Assert.Equal(expected.Length, mesh.Vertices.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.Equal(-expected[i][0], mesh.Vertices[i].X, 4);
    }

    // A range that came back short (a truncated stream node, a mismatched offset) must not fault the
    // channel readers, which index the buffer straight from the header's strides.
    [Fact]
    public void AShortStreamedBufferLeavesTheMeshUnusableRatherThanFaulting()
    {
        Dictionary<string, object> dict = LoadFixtureMesh(out _);
        var vertexData = (Dictionary<string, object>)dict["m_VertexData"];
        byte[] buffer = (byte[])vertexData["m_DataSize"];
        vertexData["m_DataSize"] = Array.Empty<byte>();
        dict["m_StreamData"] = new Dictionary<string, object>
        {
            ["path"] = "archive:/CAB-abc/CAB-abc.resS",
            ["offset"] = 0UL,
            ["size"] = (uint)buffer.Length,
        };

        Assert.False(UnityMesh.Read(dict, buffer[..(buffer.Length / 2)]).Usable);
    }

    // A compressed mesh carries its geometry in m_CompressedMesh whatever m_StreamData says, so it must
    // never be planned into a .resS pass.
    [Fact]
    public void ACompressedMeshIsNeverTreatedAsStreamed()
    {
        Dictionary<string, object> dict = LoadFixtureMesh(out _);
        dict["m_MeshCompression"] = (byte)1;
        dict["m_StreamData"] = new Dictionary<string, object>
        {
            ["path"] = "archive:/CAB-abc/CAB-abc.resS",
            ["offset"] = 0UL,
            ["size"] = 128U,
        };

        Assert.False(UnityMesh.ReadStreamRef(dict).IsStreamed);
    }
}
