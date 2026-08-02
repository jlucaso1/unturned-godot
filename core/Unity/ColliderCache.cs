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
//
// The file opens with a magic and its own payload length, like every other cache format here. Without a
// header a truncated file — a process killed mid-write — was indistinguishable from a whole one: the count
// read as whatever the first four bytes happened to be and the reader ran off the end, which surfaced as a
// load failure that nothing invalidated, so the map stayed unloadable until the cache was deleted by hand.
//
// The length is what makes truncation detectable without parsing: a magic alone would still call a file cut
// short after its first four bytes "current", so the completeness check would accept it forever and the
// object would silently load with no collision.
public static class ColliderCache
{
    // "UGCL". Files written before this magic existed have no header at all; they are treated as stale and
    // re-extracted, which is also how a bad or truncated file recovers.
    private const uint Magic = 0x4C434755;

    // magic + payload length.
    private const int HeaderBytes = 8;

    // Kind byte + a 12-float transform. Every collider costs at least this, so the declared count can be
    // bounded against the bytes actually present instead of being trusted into an allocation.
    private const int MinBytesPerCollider = 1 + (12 * 4);

    // True when the file carries the current magic AND its length matches the one recorded in its header —
    // i.e. the write completed. False for the header-less legacy format, a truncated file, a short file, or
    // a missing path. Mirrors MeshCache.IsCurrent, but checks completeness rather than only the magic:
    // callers use this to decide whether a GUID needs re-extracting, so "starts right" is not enough.
    public static bool IsCurrent(string path)
    {
        try
        {
            using FileStream s = File.OpenRead(path);
            Span<byte> head = stackalloc byte[HeaderBytes];
            if (s.Read(head) != HeaderBytes)
                return false;
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(head) != Magic)
                return false;
            int payload = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            return payload >= 0 && s.Length == HeaderBytes + (long)payload;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static void Write(Stream stream, IReadOnlyList<CachedCollider> colliders)
    {
        // The payload is measured before it is written, so the header can carry its length: BinaryWriter
        // over the destination cannot go back and patch it on a non-seekable stream.
        using var payload = new MemoryStream();
        WritePayload(payload, colliders);

        using var header = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        header.Write(Magic);
        header.Write((int)payload.Length);
        header.Flush();
        payload.Position = 0;
        payload.CopyTo(stream);
    }

    private static void WritePayload(Stream stream, IReadOnlyList<CachedCollider> colliders)
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
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("Not an Unturned collider cache file.");

        int declaredPayload = r.ReadInt32();
        if (declaredPayload < 0)
            throw new InvalidDataException($"Collider cache declares a negative length ({declaredPayload}).");
        if (stream.CanSeek && stream.Length - stream.Position < declaredPayload)
            throw new EndOfStreamException("Collider cache is shorter than its header declares.");

        int count = r.ReadInt32();
        if (count < 0)
            throw new InvalidDataException($"Collider cache declares a negative count ({count}).");

        // A corrupt-but-plausible count must not become an allocation. Every collider costs at least a kind
        // byte and a transform, so the bytes actually present bound how many there can be — otherwise a
        // count of int.MaxValue would OutOfMemory here, which is not a decode failure any caller catches.
        long available = stream.CanSeek ? stream.Length - stream.Position : declaredPayload - sizeof(int);
        if ((long)count * MinBytesPerCollider > available)
        {
            throw new InvalidDataException(
                $"Collider cache declares {count} colliders but carries only {available} bytes.");
        }

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
