using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Unity;

// Unity's m_CompressedMesh: the geometry a mesh keeps when its import settings quantize it instead of
// storing raw vertex buffers. The game's own bundle never uses it, but workshop mods do heavily (22% of
// the meshes in California 2's bundle), and without this those objects have no geometry at all.
//
// Every layout decision is read from the data:
//   - how many vertices there are comes from the vertex vector's own item count,
//   - which UV channels exist and how many components each has comes from m_UVInfo,
//   - normals are stored as two components plus a sign bit, so Z is reconstructed on the unit sphere,
//   - bit widths and quantization ranges travel inside each PackedBitVector.
public sealed class CompressedMesh
{
    public Vector3[] Vertices { get; private set; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; private set; } = Array.Empty<Vector3>();
    public Vector2[] Uvs { get; private set; } = Array.Empty<Vector2>();
    public int[] Triangles { get; private set; } = Array.Empty<int>();

    public bool HasGeometry => Vertices.Length > 0 && Triangles.Length > 0;

    // Unity packs the UV channel table into m_UVInfo: four bits per channel, the top one marking the
    // channel as present and the low two holding its component count minus one.
    private const int UvInfoBitsPerChannel = 4;
    private const uint UvDimensionMask = 3;
    private const uint UvChannelExists = 4;
    private const int UvMaxChannels = 8;

    // Vertices and (compressed) normals have a fixed component count in the format itself.
    private const int VertexComponents = 3;
    private const int NormalComponents = 2; // the third is rebuilt from the sign bit

    public static CompressedMesh Read(object? node)
    {
        var result = new CompressedMesh();
        if (node is not Dictionary<string, object> fields)
            return result;

        PackedBitVector packedVertices = PackedBitVector.Read(Field(fields, "m_Vertices"));
        float[] vertexFloats = packedVertices.UnpackFloats();
        int vertexCount = vertexFloats.Length / VertexComponents;
        if (vertexCount == 0)
            return result;

        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
        {
            int o = i * VertexComponents;
            vertices[i] = new Vector3(vertexFloats[o], vertexFloats[o + 1], vertexFloats[o + 2]);
        }

        result.Vertices = vertices;
        result.Normals = ReadNormals(fields, vertexCount);
        result.Uvs = ReadFirstUvChannel(fields, vertexCount);

        uint[] triangles = PackedBitVector.Read(Field(fields, "m_Triangles")).UnpackUInts();
        var indices = new int[triangles.Length - (triangles.Length % 3)];
        for (int i = 0; i < indices.Length; i++)
            indices[i] = (int)triangles[i];
        result.Triangles = indices;

        return result;
    }

    // Two quantized components plus one sign bit per normal: Z is whatever is left on the unit sphere.
    private static Vector3[] ReadNormals(Dictionary<string, object> fields, int vertexCount)
    {
        float[] packed = PackedBitVector.Read(Field(fields, "m_Normals")).UnpackFloats();
        uint[] signs = PackedBitVector.Read(Field(fields, "m_NormalSigns")).UnpackUInts();

        int count = Math.Min(packed.Length / NormalComponents, vertexCount);
        if (count == 0)
            return Array.Empty<Vector3>();

        var normals = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            float x = packed[i * NormalComponents];
            float y = packed[(i * NormalComponents) + 1];
            float zSquared = 1f - (x * x) - (y * y);

            Vector3 normal;
            if (zSquared >= 0f)
            {
                normal = new Vector3(x, y, Mathf.Sqrt(zSquared));
            }
            else
            {
                // Quantization can push the pair just off the sphere; renormalize instead of taking a
                // square root of a negative number.
                normal = new Vector3(x, y, 0f).Normalized();
            }

            if (i < signs.Length && signs[i] == 0)
                normal.Z = -normal.Z;

            normals[i] = normal;
        }

        return normals;
    }

    // The first existing UV channel, in the layout m_UVInfo describes. Channels are stored one after the
    // other in a single vector, so reaching channel N means skipping the components of the ones before it.
    private static Vector2[] ReadFirstUvChannel(Dictionary<string, object> fields, int vertexCount)
    {
        PackedBitVector packed = PackedBitVector.Read(Field(fields, "m_UV"));
        if (packed.IsEmpty)
            return Array.Empty<Vector2>();

        uint info = fields.TryGetValue("m_UVInfo", out object? value) ? Convert.ToUInt32(value) : 0u;
        if (info == 0)
        {
            // Pre-5.0 meshes have no channel table: the vector is UV0 (two components), optionally
            // followed by UV1.
            return ReadUvChannel(packed, vertexCount, components: 2, offset: 0);
        }

        int offset = 0;
        for (int channel = 0; channel < UvMaxChannels; channel++)
        {
            uint bits = (info >> (UvInfoBitsPerChannel * channel)) & ((1u << UvInfoBitsPerChannel) - 1u);
            if ((bits & UvChannelExists) == 0)
                continue;

            int components = (int)(bits & UvDimensionMask) + 1;
            Vector2[] uvs = ReadUvChannel(packed, vertexCount, components, offset);
            if (uvs.Length > 0)
                return uvs; // the first channel present is the one the renderer samples

            offset += components * vertexCount;
        }

        return Array.Empty<Vector2>();
    }

    private static Vector2[] ReadUvChannel(PackedBitVector packed, int vertexCount, int components,
        int offset)
    {
        if (components < 2 || vertexCount <= 0)
            return Array.Empty<Vector2>();

        float[] floats = packed.UnpackFloats(components * vertexCount, offset);
        if (floats.Length == 0)
            return Array.Empty<Vector2>();

        var uvs = new Vector2[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            uvs[i] = new Vector2(floats[i * components], floats[(i * components) + 1]);

        return uvs;
    }

    private static object? Field(Dictionary<string, object> fields, string key) =>
        fields.TryGetValue(key, out object? value) ? value : null;
}
