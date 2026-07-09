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
    public Vector2[] Uvs = Array.Empty<Vector2>();
    public int[] Indices = Array.Empty<int>();       // all triangle indices flattened
    public List<int[]> Submeshes = new();            // per-submesh triangle indices (parallel to materials)

    // Skinning (empty for non-skinned meshes): 4 bone weights + 4 bone indices per vertex, and one bind
    // pose matrix per bone (Unity column-major, 16 floats). Populated from the BlendWeight/BlendIndices
    // vertex channels and m_BindPose.
    public float[] BoneWeights = Array.Empty<float>();
    public int[] BoneIndices = Array.Empty<int>();
    public List<float[]> BindPoses = new();
    public const int BonesPerVertex = 4;

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
        result.Uvs = ReadUvChannel(channels, 4, buffer, vertexCount, strides, streamOffsets); // UV0
        result.BoneWeights = ReadFloat4Channel(channels, 12, buffer, vertexCount, strides, streamOffsets);
        result.BoneIndices = ReadInt4Channel(channels, 13, buffer, vertexCount, strides, streamOffsets);
        result.BindPoses = ReadBindPoses(mesh);
        result.Submeshes = ReadSubmeshes(mesh);
        // Flatten the submesh index arrays into one buffer in a single pass. The old running Concat
        // reallocated and recopied the whole growing array once per submesh — O(submeshes * total
        // indices), quadratic in submesh count.
        int totalIndices = 0;
        foreach (int[] sm in result.Submeshes)
            totalIndices += sm.Length;
        var flat = new int[totalIndices];
        int flatOffset = 0;
        foreach (int[] sm in result.Submeshes)
        {
            Array.Copy(sm, 0, flat, flatOffset, sm.Length);
            flatOffset += sm.Length;
        }
        result.Indices = flat;
        result.Usable = result.Vertices.Length > 0 && totalIndices > 0;
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

    private static Vector2[] ReadUvChannel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<Vector2>();

        var ch = (Dictionary<string, object>)channels[index];
        int dim = ToInt(ch["dimension"]);
        if (dim < 2)
            return Array.Empty<Vector2>();

        int stream = ToInt(ch["stream"]);
        int format = ToInt(ch["format"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int componentSize = FormatSize[format];

        var values = new Vector2[vertexCount];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            values[v] = new Vector2(ReadComponent(buffer, p, format), ReadComponent(buffer, p + componentSize, format));
        }
        return values;
    }

    // 4 float bone weights per vertex (BlendWeight channel), flattened; empty when absent.
    private static float[] ReadFloat4Channel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<float>();
        var ch = (Dictionary<string, object>)channels[index];
        if (ToInt(ch["dimension"]) < 4)
            return Array.Empty<float>();

        int stream = ToInt(ch["stream"]);
        int format = ToInt(ch["format"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int size = FormatSize[format];

        var values = new float[vertexCount * 4];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            for (int c = 0; c < 4; c++)
                values[v * 4 + c] = ReadComponent(buffer, p + c * size, format);
        }
        return values;
    }

    // 4 bone indices per vertex (BlendIndices channel; UInt8/16/32), flattened; empty when absent.
    private static int[] ReadInt4Channel(List<object> channels, int index, byte[] buffer,
        int vertexCount, int[] strides, int[] streamOffsets)
    {
        if (index >= channels.Count)
            return Array.Empty<int>();
        var ch = (Dictionary<string, object>)channels[index];
        if (ToInt(ch["dimension"]) < 4)
            return Array.Empty<int>();

        int stream = ToInt(ch["stream"]);
        int format = ToInt(ch["format"]);
        int stride = strides[stream];
        int baseOffset = streamOffsets[stream] + ToInt(ch["offset"]);
        int size = FormatSize[format];

        var values = new int[vertexCount * 4];
        for (int v = 0; v < vertexCount; v++)
        {
            int p = baseOffset + v * stride;
            for (int c = 0; c < 4; c++)
                values[v * 4 + c] = ReadIntComponent(buffer, p + c * size, format);
        }
        return values;
    }

    private static int ReadIntComponent(byte[] buffer, int offset, int format) => format switch
    {
        6 => buffer[offset],                            // UInt8
        8 => BitConverter.ToUInt16(buffer, offset),     // UInt16
        _ => BitConverter.ToInt32(buffer, offset),      // UInt32/SInt32 (format 10/11)
    };

    // One bind pose matrix (16 floats, Unity column-major e00..e33) per bone.
    private static List<float[]> ReadBindPoses(Dictionary<string, object> mesh)
    {
        var result = new List<float[]>();
        if (!mesh.TryGetValue("m_BindPose", out object? bp) || bp is not List<object> poses)
            return result;

        foreach (object pose in poses)
        {
            var m = (Dictionary<string, object>)pose;
            var matrix = new float[16];
            for (int col = 0; col < 4; col++)
                for (int row = 0; row < 4; row++)
                    matrix[col * 4 + row] = Convert.ToSingle(m[$"e{row}{col}"]); // column-major
            result.Add(matrix);
        }
        return result;
    }

    private static float ReadComponent(byte[] buffer, int offset, int format) => format switch
    {
        0 => BitConverter.ToSingle(buffer, offset),                       // Float32
        1 => (float)BitConverter.ToHalf(buffer, offset),                  // Float16
        2 => buffer[offset] / 255f,                                       // UNorm8
        _ => BitConverter.ToSingle(buffer, offset),
    };

    // One index array per submesh (triangle lists only), parallel to the palette's materials.
    private static List<int[]> ReadSubmeshes(Dictionary<string, object> mesh)
    {
        byte[] indexBuffer = (byte[])mesh["m_IndexBuffer"];
        bool is32 = ToInt(mesh["m_IndexFormat"]) == 1;
        int size = is32 ? 4 : 2;

        var result = new List<int[]>();
        foreach (object s in (List<object>)mesh["m_SubMeshes"])
        {
            var sm = (Dictionary<string, object>)s;
            if (ToInt(sm["topology"]) != 0)
            {
                result.Add(Array.Empty<int>()); // keep index alignment with materials
                continue;
            }

            int firstByte = ToInt(sm["firstByte"]);
            int indexCount = ToInt(sm["indexCount"]);
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
            {
                int p = firstByte + i * size;
                // Index buffer values are absolute vertex indices.
                indices[i] = is32 ? BitConverter.ToInt32(indexBuffer, p) : BitConverter.ToUInt16(indexBuffer, p);
            }
            result.Add(indices);
        }
        return result;
    }

    private static int ToInt(object value) => Convert.ToInt32(value);
}
