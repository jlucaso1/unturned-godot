using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace UnturnedGodot.Unity;

// One submesh of a cached model: its triangle indices plus the material's flat color, (optional) texture
// cache key and the Standard shader's surface response. Unturned objects are mostly flat-colored and fully
// matte (_Metallic/_Glossiness 0), with textures and the occasional gloss/metal on some props.
public readonly struct CachedSubmesh
{
    public readonly int[] Indices;
    public readonly Color Color;
    public readonly string TextureKey;         // "" when the submesh has no resolved texture
    public readonly UnityMaterial.Blend Blend; // opaque / cutout (alpha clip) / alpha blend
    public readonly float Metallic;            // Unity _Metallic (0..1)
    public readonly float Smoothness;          // Unity _Glossiness; Godot roughness = 1 - this
    public readonly EShaderCull Cull;          // the material's shader-authored culling

    public CachedSubmesh(int[] indices, Color color, string textureKey, UnityMaterial.Blend blend,
        float metallic = 0f, float smoothness = 0f, EShaderCull cull = EShaderCull.Back)
    {
        Indices = indices;
        Color = color;
        TextureKey = textureKey;
        Blend = blend;
        Metallic = metallic;
        Smoothness = smoothness;
        Cull = cull;
    }
}

// Compact on-disk format for an extracted model (positions, normals, UVs and per-submesh indices +
// texture keys), so the 1.4 GB bundle is parsed once and runtime loads only the small meshes it needs.
public static class MeshCache
{
    // "UGMA": extraction now also caches each prefab's authored lower LOD level beside its mesh. A cache
    // written by UGM9 is complete by its own rules but has no <guid>.lod1.mesh anywhere, and completeness
    // is decided per mesh — a missing lower level is indistinguishable from a prefab that never had one.
    // Bumping is what forces one more extraction pass so the levels exist at all; without it the feature
    // stays silently off for every install that already has a warm cache.
    private const uint Magic = 0x414D4755;

    // True when the file starts with the current format magic; false for stale formats, short files or a
    // missing path. Cold-load detection uses this so a format bump re-extracts instead of crashing on Read.
    public static bool IsCurrent(string path)
    {
        try
        {
            using FileStream s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return s.Read(head) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic;
        }
        catch (IOException) { return false; }
    }

    // The same check against bytes already in hand, so a caller that reads the file anyway does one open
    // instead of two (IsCurrent(path) + ReadAllBytes) per cached mesh.
    public static bool IsCurrent(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;

    public static void Write(Stream stream, Vector3[] vertices, Vector3[] normals, Vector2[] uvs,
        IReadOnlyList<CachedSubmesh> submeshes)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);

        w.Write(vertices.Length);
        foreach (Vector3 v in vertices)
        {
            w.Write(v.X);
            w.Write(v.Y);
            w.Write(v.Z);
        }

        WriteVectors3(w, normals, vertices.Length);
        WriteVectors2(w, uvs, vertices.Length);

        w.Write(submeshes.Count);
        foreach (CachedSubmesh sm in submeshes)
        {
            w.Write(sm.TextureKey);
            w.Write(sm.Color.R);
            w.Write(sm.Color.G);
            w.Write(sm.Color.B);
            w.Write(sm.Color.A);
            w.Write((byte)sm.Blend);
            w.Write(sm.Metallic);
            w.Write(sm.Smoothness);
            w.Write((byte)sm.Cull);
            w.Write(sm.Indices.Length);
            foreach (int i in sm.Indices)
                w.Write(i);
        }
    }

    // Parses the whole file over a byte cursor: the big vertex/normal/uv/index blocks are bulk-reinterpreted
    // with MemoryMarshal (Godot's Vector3/Vector2 are blittable and match the sequential little-endian
    // write), instead of millions of virtual BinaryReader.ReadSingle/ReadInt32 dispatches per warm load.
    public static (Vector3[] vertices, Vector3[] normals, Vector2[] uvs, List<CachedSubmesh> submeshes) Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    // Parses straight over an already-materialized buffer — callers that hold the bytes (e.g. from
    // File.ReadAllBytes) skip the CopyTo + ToArray the Stream overload needs.
    public static (Vector3[] vertices, Vector3[] normals, Vector2[] uvs, List<CachedSubmesh> submeshes) Read(byte[] data)
    {
        int pos = 0;

        if (ReadUInt32(data, ref pos) != Magic)
            throw new InvalidDataException("Not a mesh cache stream");

        int vertexCount = ReadInt32(data, ref pos);
        Vector3[] vertices = ReadVector3Array(data, ref pos, vertexCount);

        Vector3[] normals = ReadBool(data, ref pos) ? ReadVector3Array(data, ref pos, vertexCount) : System.Array.Empty<Vector3>();
        Vector2[] uvs = ReadBool(data, ref pos) ? ReadVector2Array(data, ref pos, vertexCount) : System.Array.Empty<Vector2>();

        int submeshCount = ReadInt32(data, ref pos);
        var submeshes = new List<CachedSubmesh>(submeshCount);
        for (int s = 0; s < submeshCount; s++)
        {
            string textureKey = ReadString(data, ref pos);
            var color = new Color(ReadSingle(data, ref pos), ReadSingle(data, ref pos),
                ReadSingle(data, ref pos), ReadSingle(data, ref pos));
            var blend = (UnityMaterial.Blend)data[pos++];
            float metallic = ReadSingle(data, ref pos);
            float smoothness = ReadSingle(data, ref pos);
            var cull = (EShaderCull)data[pos++];
            int indexCount = ReadInt32(data, ref pos);
            int[] indices = ReadIntArray(data, ref pos, indexCount);
            submeshes.Add(new CachedSubmesh(indices, color, textureKey, blend, metallic, smoothness, cull));
        }

        return (vertices, normals, uvs, submeshes);
    }

    private static void WriteVectors3(BinaryWriter w, Vector3[] values, int expectedCount)
    {
        bool present = values.Length == expectedCount && expectedCount > 0;
        w.Write(present);
        if (present)
            foreach (Vector3 v in values)
            {
                w.Write(v.X);
                w.Write(v.Y);
                w.Write(v.Z);
            }
    }

    private static void WriteVectors2(BinaryWriter w, Vector2[] values, int expectedCount)
    {
        bool present = values.Length == expectedCount && expectedCount > 0;
        w.Write(present);
        if (present)
            foreach (Vector2 v in values)
            {
                w.Write(v.X);
                w.Write(v.Y);
            }
    }

    // Bulk reinterpret the little-endian float/int block (blittable, matching the sequential write layout).
    private static Vector3[] ReadVector3Array(byte[] d, ref int p, int count)
    {
        var values = new Vector3[count];
        MemoryMarshal.Cast<byte, Vector3>(d.AsSpan(p, count * 12)).CopyTo(values);
        p += count * 12;
        return values;
    }

    private static Vector2[] ReadVector2Array(byte[] d, ref int p, int count)
    {
        var values = new Vector2[count];
        MemoryMarshal.Cast<byte, Vector2>(d.AsSpan(p, count * 8)).CopyTo(values);
        p += count * 8;
        return values;
    }

    private static int[] ReadIntArray(byte[] d, ref int p, int count)
    {
        var values = new int[count];
        MemoryMarshal.Cast<byte, int>(d.AsSpan(p, count * 4)).CopyTo(values);
        p += count * 4;
        return values;
    }

    private static uint ReadUInt32(byte[] d, ref int p)
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    private static int ReadInt32(byte[] d, ref int p)
    {
        int v = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    private static float ReadSingle(byte[] d, ref int p)
    {
        float v = BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p));
        p += 4;
        return v;
    }

    private static bool ReadBool(byte[] d, ref int p) => d[p++] != 0;

    // Matches BinaryReader.ReadString: a 7-bit-encoded length prefix then that many UTF-8 bytes.
    private static string ReadString(byte[] d, ref int p)
    {
        int len = 0, shift = 0;
        byte b;
        do
        {
            b = d[p++];
            len |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        string s = System.Text.Encoding.UTF8.GetString(d, p, len);
        p += len;
        return s;
    }
}
