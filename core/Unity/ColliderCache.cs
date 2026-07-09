using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Unity;

public enum EColliderKind : byte { Box, Sphere, Capsule, Mesh }

// A decoded object collider ready to cache: either Unity primitive parameters or a collision mesh (Unity-space
// vertices + flattened triangle indices), plus its pose relative to the prefab root. The Unity->Godot flip and
// Godot shape construction happen when the collision body is built, so this stays in Unity units.
public readonly struct CachedCollider
{
    public readonly EColliderKind Kind;
    public readonly Transform3D LocalToRoot;
    public readonly Vector3 Center;      // Box / Sphere / Capsule
    public readonly Vector3 Size;        // Box
    public readonly float Radius;        // Sphere / Capsule
    public readonly float Height;        // Capsule
    public readonly int Direction;       // Capsule axis: 0=X, 1=Y, 2=Z
    public readonly Vector3[] Vertices;  // Mesh
    public readonly int[] Indices;       // Mesh

    public CachedCollider(EColliderKind kind, Transform3D localToRoot, Vector3 center, Vector3 size,
        float radius, float height, int direction, Vector3[] vertices, int[] indices)
    {
        Kind = kind;
        LocalToRoot = localToRoot;
        Center = center;
        Size = size;
        Radius = radius;
        Height = height;
        Direction = direction;
        Vertices = vertices;
        Indices = indices;
    }

    private static readonly Vector3[] NoVerts = Array.Empty<Vector3>();
    private static readonly int[] NoIndices = Array.Empty<int>();

    public static CachedCollider Box(Transform3D t, Vector3 center, Vector3 size)
        => new(EColliderKind.Box, t, center, size, 0f, 0f, 0, NoVerts, NoIndices);
    public static CachedCollider Sphere(Transform3D t, Vector3 center, float radius)
        => new(EColliderKind.Sphere, t, center, Vector3.Zero, radius, 0f, 0, NoVerts, NoIndices);
    public static CachedCollider Capsule(Transform3D t, Vector3 center, float radius, float height, int direction)
        => new(EColliderKind.Capsule, t, center, Vector3.Zero, radius, height, direction, NoVerts, NoIndices);
    public static CachedCollider Mesh(Transform3D t, Vector3[] vertices, int[] indices)
        => new(EColliderKind.Mesh, t, Vector3.Zero, Vector3.Zero, 0f, 0f, 0, vertices, indices);
}

// Per-GUID cache of an object's colliders, written once during extraction and read at load — small, so plain
// BinaryReader/Writer (unlike the hot-path mesh cache).
public static class ColliderCache
{
    public static void Write(Stream stream, IReadOnlyList<CachedCollider> colliders)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(colliders.Count);
        foreach (CachedCollider c in colliders)
        {
            w.Write((byte)c.Kind);
            WriteTransform(w, c.LocalToRoot);
            switch (c.Kind)
            {
                case EColliderKind.Box:
                    WriteVec3(w, c.Center);
                    WriteVec3(w, c.Size);
                    break;
                case EColliderKind.Sphere:
                    WriteVec3(w, c.Center);
                    w.Write(c.Radius);
                    break;
                case EColliderKind.Capsule:
                    WriteVec3(w, c.Center);
                    w.Write(c.Radius);
                    w.Write(c.Height);
                    w.Write(c.Direction);
                    break;
                default:
                    w.Write(c.Vertices.Length);
                    foreach (Vector3 v in c.Vertices)
                        WriteVec3(w, v);
                    w.Write(c.Indices.Length);
                    foreach (int i in c.Indices)
                        w.Write(i);
                    break;
            }
        }
    }

    public static List<CachedCollider> Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int count = r.ReadInt32();
        var result = new List<CachedCollider>(count);
        for (int n = 0; n < count; n++)
        {
            var kind = (EColliderKind)r.ReadByte();
            Transform3D t = ReadTransform(r);
            switch (kind)
            {
                case EColliderKind.Box:
                    result.Add(CachedCollider.Box(t, ReadVec3(r), ReadVec3(r)));
                    break;
                case EColliderKind.Sphere:
                    result.Add(CachedCollider.Sphere(t, ReadVec3(r), r.ReadSingle()));
                    break;
                case EColliderKind.Capsule:
                    result.Add(CachedCollider.Capsule(t, ReadVec3(r), r.ReadSingle(), r.ReadSingle(), r.ReadInt32()));
                    break;
                default:
                    var verts = new Vector3[r.ReadInt32()];
                    for (int i = 0; i < verts.Length; i++)
                        verts[i] = ReadVec3(r);
                    var indices = new int[r.ReadInt32()];
                    for (int i = 0; i < indices.Length; i++)
                        indices[i] = r.ReadInt32();
                    result.Add(CachedCollider.Mesh(t, verts, indices));
                    break;
            }
        }
        return result;
    }

    private static void WriteTransform(BinaryWriter w, Transform3D t)
    {
        WriteVec3(w, t.Basis.X);
        WriteVec3(w, t.Basis.Y);
        WriteVec3(w, t.Basis.Z);
        WriteVec3(w, t.Origin);
    }

    private static Transform3D ReadTransform(BinaryReader r)
        => new(new Basis(ReadVec3(r), ReadVec3(r), ReadVec3(r)), ReadVec3(r));

    private static void WriteVec3(BinaryWriter w, Vector3 v)
    {
        w.Write(v.X);
        w.Write(v.Y);
        w.Write(v.Z);
    }

    private static Vector3 ReadVec3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
