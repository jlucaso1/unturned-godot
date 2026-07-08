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
}
