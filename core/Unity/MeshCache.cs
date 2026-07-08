using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Godot;

namespace UnturnedGodot.Unity;

// One submesh of a cached model: its triangle indices plus the material's flat color and (optional)
// texture cache key. Unturned objects are mostly flat-colored, with textures on some props.
public readonly struct CachedSubmesh
{
    public readonly int[] Indices;
    public readonly Color Color;
    public readonly string TextureKey;         // "" when the submesh has no resolved texture
    public readonly UnityMaterial.Blend Blend; // opaque / cutout (alpha clip) / alpha blend

    public CachedSubmesh(int[] indices, Color color, string textureKey, UnityMaterial.Blend blend)
    {
        Indices = indices;
        Color = color;
        TextureKey = textureKey;
        Blend = blend;
    }
}

// Compact on-disk format for an extracted model (positions, normals, UVs and per-submesh indices +
// texture keys), so the 1.4 GB bundle is parsed once and runtime loads only the small meshes it needs.
public static class MeshCache
{
    private const uint Magic = 0x324D4755; // "UGM2"

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
        byte[] data;
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            data = buffer.ToArray();
        }
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
            int indexCount = ReadInt32(data, ref pos);
            int[] indices = ReadIntArray(data, ref pos, indexCount);
            submeshes.Add(new CachedSubmesh(indices, color, textureKey, blend));
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
