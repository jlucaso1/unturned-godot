using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// Interprets a Unity Mesh object (read via TypeTreeReader) into plain geometry. Handles the common
// path Unturned object models use: uncompressed, inline vertex data, Float32/Float16/UNorm8 channels.
public sealed class UnityMesh
{
    public string Name = string.Empty;
    public Vector3[] Vertices = Array.Empty<Vector3>();
    public Vector3[] Normals = Array.Empty<Vector3>();
    public int[] Indices = Array.Empty<int>();

    // False when the mesh uses compression or external stream data we don't decode (caller falls back).
    public bool Usable { get; private set; }

    private static readonly int[] FormatSize =
    {
        4, 2, 1, 1, 2, 2, 1, 1, 2, 2, 4, 4, // 0..11: Float32,Float16,UNorm8,SNorm8,UNorm16,SNorm16,UInt8,SInt8,UInt16,SInt16,UInt32,SInt32
    };

    public static UnityMesh Read(Dictionary<string, object> mesh)
    {
        var result = new UnityMesh { Name = mesh.TryGetValue("m_Name", out object? n) ? (string)n : string.Empty };

        if (ToInt(mesh["m_MeshCompression"]) != 0)
            return result; // compressed meshes not supported

        var streamData = (Dictionary<string, object>)mesh["m_StreamData"];
        if (((string)streamData["path"]).Length != 0)
            return result; // vertex data lives in an external .resS

        var vertexData = (Dictionary<string, object>)mesh["m_VertexData"];
        int vertexCount = ToInt(vertexData["m_VertexCount"]);
        var channels = (List<object>)vertexData["m_Channels"];
        byte[] buffer = (byte[])vertexData["m_DataSize"];

        int[] strides = ComputeStreamStrides(channels, out int[] streamOffsets, vertexCount);

        result.Vertices = ReadChannel(channels, 0, buffer, vertexCount, strides, streamOffsets);
        result.Normals = ReadChannel(channels, 1, buffer, vertexCount, strides, streamOffsets);
        result.Indices = ReadIndices(mesh);
        result.Usable = result.Vertices.Length > 0 && result.Indices.Length > 0;
        return result;
    }

    private static int[] ComputeStreamStrides(List<object> channels, out int[] streamOffsets, int vertexCount)
    {
        int streamCount = 1;
        foreach (object c in channels)
        {
            var ch = (Dictionary<string, object>)c;
            if (ToInt(ch["dimension"]) > 0)
                streamCount = Math.Max(streamCount, ToInt(ch["stream"]) + 1);
        }

        var strides = new int[streamCount];
        foreach (object c in channels)
        {
            var ch = (Dictionary<string, object>)c;
            int dim = ToInt(ch["dimension"]);
            if (dim == 0)
                continue;
            int stream = ToInt(ch["stream"]);
            int end = ToInt(ch["offset"]) + dim * FormatSize[ToInt(ch["format"])];
            strides[stream] = Math.Max(strides[stream], end);
        }

        streamOffsets = new int[streamCount];
        for (int s = 1; s < streamCount; s++)
        {
            int prev = streamOffsets[s - 1] + vertexCount * strides[s - 1];
            streamOffsets[s] = (prev + 15) & ~15; // streams align to 16 bytes
        }
        return strides;
    }

    private static Vector3[] ReadChannel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<Vector3>();

        var ch = (Dictionary<string, object>)channels[index];
        int dim = ToInt(ch["dimension"]);
        if (dim < 3)
            return Array.Empty<Vector3>();

        int stream = ToInt(ch["stream"]);
        int format = ToInt(ch["format"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int componentSize = FormatSize[format];

        var values = new Vector3[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            values[v] = new Vector3(
                ReadComponent(buffer, p, format),
                ReadComponent(buffer, p + componentSize, format),
                ReadComponent(buffer, p + 2 * componentSize, format));
        }
        return values;
    }

    private static float ReadComponent(byte[] buffer, int offset, int format) => format switch
    {
        0 => BitConverter.ToSingle(buffer, offset),                       // Float32
        1 => (float)BitConverter.ToHalf(buffer, offset),                  // Float16
        2 => buffer[offset] / 255f,                                       // UNorm8
        _ => BitConverter.ToSingle(buffer, offset),
    };

    private static int[] ReadIndices(Dictionary<string, object> mesh)
    {
        byte[] indexBuffer = (byte[])mesh["m_IndexBuffer"];
        bool is32 = ToInt(mesh["m_IndexFormat"]) == 1;
        var submeshes = (List<object>)mesh["m_SubMeshes"];

        var indices = new List<int>();
        foreach (object s in submeshes)
        {
            var sm = (Dictionary<string, object>)s;
            if (ToInt(sm["topology"]) != 0)
                continue; // only triangle lists

            int firstByte = ToInt(sm["firstByte"]);
            int indexCount = ToInt(sm["indexCount"]);
            int size = is32 ? 4 : 2;
            for (int i = 0; i < indexCount; i++)
            {
                int p = firstByte + i * size;
                // Index buffer values are absolute vertex indices.
                indices.Add(is32 ? BitConverter.ToInt32(indexBuffer, p) : BitConverter.ToUInt16(indexBuffer, p));
            }
        }
        return indices.ToArray();
    }

    private static int ToInt(object value) => Convert.ToInt32(value);
}
