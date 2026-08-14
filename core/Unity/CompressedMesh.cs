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

    // Skinning, in exactly the layout UnityMesh's uncompressed path produces: four weights and four bone
    // indices per vertex, flattened. Empty when the mesh carries none, or when the two packed runs do not
    // between them cover every vertex.
    public float[] BoneWeights { get; private set; } = Array.Empty<float>();
    public int[] BoneIndices { get; private set; } = Array.Empty<int>();

    public bool HasGeometry => Vertices.Length > 0 && Triangles.Length > 0;

    // Unity packs the UV channel table into m_UVInfo: four bits per channel, the top one marking the
    // channel as present and the low two holding its component count minus one.
    private const int UvInfoBitsPerChannel = 4;
    private const uint UvDimensionMask = 3;
    private const uint UvChannelExists = 4;

    // Vertices and (compressed) normals have a fixed component count in the format itself.
    private const int VertexComponents = 3;
    private const int NormalComponents = 2; // the third is rebuilt from the sign bit

    // Skin influences per vertex, and the scale the weights are quantized against. Unity writes the
    // weights as 5-bit integers that sum to 31 across a vertex's influences rather than as normalized
    // floats, which is what lets it stop early: once the running sum reaches 31 the vertex is complete and
    // its remaining slots are implicitly zero, so a vertex bound to one bone costs one item instead of
    // four. The same 31 is what AssetStudio, UnityPy and AssetRipper all read this run against.
    private const int BonesPerVertex = UnityMesh.BonesPerVertex;
    private const int WeightScale = 31;

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
        result.Uvs = ReadUv0(fields, vertexCount);
        ReadSkin(fields, vertexCount, result);

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

    // UV0, in the layout m_UVInfo describes. Channels are stored one after the other in a single vector
    // and UV0 is the first of them, so when it is present it starts at offset zero.
    //
    // This used to return the first channel that *existed*, which is the same thing for every mesh that
    // has a UV0 and quietly wrong for one that does not: a mesh carrying only UV1 (a lightmap channel,
    // whose coordinates address an atlas rather than the albedo) had those handed back as UV0 and drew its
    // texture through them. The uncompressed path reads channel 4 — UV0 — by name and returns nothing when
    // it is absent, so this now answers the same way: UV0 or no UVs, and a flat-coloured surface is the
    // honest result of a mesh that has no albedo coordinates.
    private static Vector2[] ReadUv0(Dictionary<string, object> fields, int vertexCount)
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

        uint bits = info & ((1u << UvInfoBitsPerChannel) - 1u); // channel 0 is the low nibble
        if ((bits & UvChannelExists) == 0)
            return Array.Empty<Vector2>();

        return ReadUvChannel(packed, vertexCount, (int)(bits & UvDimensionMask) + 1, offset: 0);
    }

    // Skin influences, out of the two runs Unity packs them into.
    //
    // The layout is not one record per vertex: m_Weights is a flat run of 5-bit weights and m_BoneIndices
    // a flat run of bone ids, and a vertex ends as soon as its weights reach the full 31 — so a vertex
    // bound to one bone contributes a single item to each run and a vertex bound to four contributes
    // three weights plus a fourth derived from the remainder. Walking the two cursors is therefore the
    // only way to know which vertex an item belongs to; nothing in the data indexes it directly.
    //
    // This is the run the README's first-person arms are waiting on: the `Viewmodel` rig keeps its skin
    // weights here, so without it that mesh imports as an unposable bind-pose shell.
    private static void ReadSkin(Dictionary<string, object> fields, int vertexCount, CompressedMesh result)
    {
        uint[] weights = PackedBitVector.Read(Field(fields, "m_Weights")).UnpackUInts();
        uint[] boneIndices = PackedBitVector.Read(Field(fields, "m_BoneIndices")).UnpackUInts();
        if (weights.Length == 0 || boneIndices.Length == 0)
            return;

        var vertexWeights = new float[vertexCount * BonesPerVertex];
        var vertexBones = new int[vertexCount * BonesPerVertex];

        int vertex = 0;   // the vertex being filled
        int slot = 0;     // which of its four influences
        int bone = 0;     // cursor into the bone-index run
        int sum = 0;      // this vertex's weights so far, in quantized units

        for (int i = 0; i < weights.Length && vertex < vertexCount; i++)
        {
            if (bone >= boneIndices.Length)
                break; // the two runs disagree; fall through to the coverage test below

            int at = (vertex * BonesPerVertex) + slot;
            vertexWeights[at] = weights[i] / (float)WeightScale;
            vertexBones[at] = (int)boneIndices[bone++];
            sum += (int)weights[i];
            slot++;

            if (sum >= WeightScale)
            {
                // The vertex is fully weighted; whatever slots are left keep their zeroes.
                vertex++;
                slot = 0;
                sum = 0;
            }
            else if (slot == BonesPerVertex - 1)
            {
                // Three weights that do not add up: the fourth is the remainder and is not written out,
                // but its bone id is.
                if (bone >= boneIndices.Length)
                    break;
                int last = (vertex * BonesPerVertex) + slot;
                vertexWeights[last] = (WeightScale - sum) / (float)WeightScale;
                vertexBones[last] = (int)boneIndices[bone++];
                vertex++;
                slot = 0;
                sum = 0;
            }
        }

        // All or nothing. A run that stops short leaves the remaining vertices weightless, and a weightless
        // vertex under a skeleton collapses onto the model's origin — a far more visible defect than the
        // bind-pose shell this replaces, and one that would look like a modelling error rather than a
        // truncated read.
        if (vertex != vertexCount)
            return;

        result.BoneWeights = vertexWeights;
        result.BoneIndices = vertexBones;
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
