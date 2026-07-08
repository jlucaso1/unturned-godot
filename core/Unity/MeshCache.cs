using System.Collections.Generic;
using System.IO;
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

    public static (Vector3[] vertices, Vector3[] normals, Vector2[] uvs, List<CachedSubmesh> submeshes) Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a mesh cache stream");

        int vertexCount = r.ReadInt32();
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            vertices[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        Vector3[] normals = r.ReadBoolean() ? ReadVectors3(r, vertexCount) : System.Array.Empty<Vector3>();
        Vector2[] uvs = r.ReadBoolean() ? ReadVectors2(r, vertexCount) : System.Array.Empty<Vector2>();

        int submeshCount = r.ReadInt32();
        var submeshes = new List<CachedSubmesh>(submeshCount);
        for (int s = 0; s < submeshCount; s++)
        {
            string textureKey = r.ReadString();
            var color = new Color(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            var blend = (UnityMaterial.Blend)r.ReadByte();
            int indexCount = r.ReadInt32();
            var indices = new int[indexCount];
            for (int i = 0; i < indexCount; i++)
                indices[i] = r.ReadInt32();
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

    private static Vector3[] ReadVectors3(BinaryReader r, int count)
    {
        var values = new Vector3[count];
        for (int i = 0; i < count; i++)
            values[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return values;
    }

    private static Vector2[] ReadVectors2(BinaryReader r, int count)
    {
        var values = new Vector2[count];
        for (int i = 0; i < count; i++)
            values[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        return values;
    }
}
