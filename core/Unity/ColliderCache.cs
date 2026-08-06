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

    // A ladder's climbing volume rather than solid geometry (UnityLayers.Ladder). It is a trigger in
    // Unity, on a layer the player capsule does not collide with, and exists only to be raycast against —
    // so it must become a body a climb probe can find and nothing else can walk into.
    public readonly bool IsLadder;

    // The PhysicMaterial name this collider carries, or empty. It decides which impact sound and which
    // impact effect a melee hit on this surface resolves to (PhysicsTool.GetColliderSharedPhysicsMaterialName),
    // and empty is a real answer rather than missing data: the original skips the impact entirely for a
    // collider with no material, and most colliders in the shipped content have none.
    public readonly string MaterialName;

    public CachedCollider(EColliderKind kind, Transform3D localToRoot, Vector3 center, Vector3 size,
        float radius, float height, int direction, Vector3[] vertices, int[] indices,
        bool isLadder = false, string materialName = "")
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
        IsLadder = isLadder;
        MaterialName = materialName;
    }

    private static readonly Vector3[] NoVerts = Array.Empty<Vector3>();
    private static readonly int[] NoIndices = Array.Empty<int>();

    public static CachedCollider Box(Transform3D t, Vector3 center, Vector3 size, bool isLadder = false,
        string materialName = "")
        => new(EColliderKind.Box, t, center, size, 0f, 0f, 0, NoVerts, NoIndices, isLadder, materialName);
    public static CachedCollider Sphere(Transform3D t, Vector3 center, float radius, bool isLadder = false,
        string materialName = "")
        => new(EColliderKind.Sphere, t, center, Vector3.Zero, radius, 0f, 0, NoVerts, NoIndices, isLadder,
            materialName);
    public static CachedCollider Capsule(Transform3D t, Vector3 center, float radius, float height,
        int direction, bool isLadder = false, string materialName = "")
        => new(EColliderKind.Capsule, t, center, Vector3.Zero, radius, height, direction, NoVerts,
            NoIndices, isLadder, materialName);
    public static CachedCollider Mesh(Transform3D t, Vector3[] vertices, int[] indices,
        bool isLadder = false, string materialName = "")
        => new(EColliderKind.Mesh, t, Vector3.Zero, Vector3.Zero, 0f, 0f, 0, vertices, indices, isLadder,
            materialName);
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
    // "UGC3". The magic doubles as the format version: "UGC2" files carry no physics-material table, and
    // "UGCL" before them no flags byte, so reading either with this reader would misparse every collider
    // after the first. Files written under any older magic — and the header-less format before those — are
    // treated as stale and re-extracted, which is also how a bad or truncated file recovers.
    private const uint Magic = 0x33434755;

    // magic + payload length.
    private const int HeaderBytes = 8;

    // Kind byte + flags byte + material index byte + a 12-float transform. Every collider costs at least
    // this, so the declared count can be bounded against the bytes actually present instead of being
    // trusted into an allocation.
    private const int MinBytesPerCollider = 1 + 1 + 1 + (12 * 4);

    // Bit 0 of the per-collider flags byte: this is a ladder's climbing volume, not solid geometry.
    private const byte LadderFlag = 1;

    // Physics-material names are written once per file as a table and referenced by a one-byte index, with
    // index 0 reserved for "no material". A prefab's colliders draw from a handful of names — the whole
    // shipped game defines 21 — and the same one repeats across every collider on a building, so the table
    // costs a few dozen bytes where inline strings would cost that much per collider.
    private const int NoMaterial = 0;

    // How many distinct names one file's table may hold, index 0 being the empty name. A prefab that
    // somehow exceeded this could not be addressed by a byte, and silently dropping the overflow to "no
    // material" would make an object quietly lose its impact sounds — so it is refused at write time.
    private const int MaxMaterialNames = 256;

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

        // The table comes before the colliders that index into it, so a reader has every name in hand
        // before it needs one. Index 0 is the empty name and is never written out.
        Dictionary<string, int> materialIndices = BuildMaterialTable(colliders, out List<string> names);
        w.Write(names.Count);
        foreach (string name in names)
            w.Write(name);

        foreach (CachedCollider c in colliders)
        {
            w.Write((byte)c.Kind);
            w.Write((byte)(c.IsLadder ? LadderFlag : 0));
            w.Write((byte)(materialIndices.TryGetValue(c.MaterialName, out int index) ? index : NoMaterial));
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

    // The distinct non-empty material names these colliders name, in first-seen order, and the index each
    // one is written under. Index 0 is "no material" and has no entry.
    private static Dictionary<string, int> BuildMaterialTable(IReadOnlyList<CachedCollider> colliders,
        out List<string> names)
    {
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        names = new List<string>();
        foreach (CachedCollider c in colliders)
        {
            if (c.MaterialName.Length == 0 || indices.ContainsKey(c.MaterialName))
                continue;
            if (names.Count + 1 >= MaxMaterialNames)
            {
                throw new InvalidDataException(
                    $"A prefab names more than {MaxMaterialNames - 1} distinct physics materials.");
            }
            names.Add(c.MaterialName);
            indices[c.MaterialName] = names.Count; // 0 is reserved, so the first name is 1
        }
        return indices;
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

        long payloadStart = stream.CanSeek ? stream.Position : -1;
        int count = r.ReadInt32();
        if (count < 0)
            throw new InvalidDataException($"Collider cache declares a negative count ({count}).");

        string[] materialNames = ReadMaterialTable(r, stream);

        // A corrupt-but-plausible count must not become an allocation. Every collider costs at least a kind
        // byte and a transform, so the bytes actually present bound how many there can be — otherwise a
        // count of int.MaxValue would OutOfMemory here, which is not a decode failure any caller catches.
        // Measured after the name table, since that is where the colliders actually start.
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
            bool isLadder = (r.ReadByte() & LadderFlag) != 0;
            string material = MaterialAt(materialNames, r.ReadByte());
            Transform3D t = ReadTransform(r);
            switch (kind)
            {
                case EColliderKind.Box:
                    result.Add(CachedCollider.Box(t, ReadVec3(r), ReadVec3(r), isLadder, material));
                    break;
                case EColliderKind.Sphere:
                    result.Add(CachedCollider.Sphere(t, ReadVec3(r), r.ReadSingle(), isLadder, material));
                    break;
                case EColliderKind.Capsule:
                    result.Add(CachedCollider.Capsule(t, ReadVec3(r), r.ReadSingle(), r.ReadSingle(),
                        r.ReadInt32(), isLadder, material));
                    break;
                default:
                    var verts = new Vector3[ReadBoundedCount(r, stream, sizeof(float) * 3, "vertices")];
                    for (int i = 0; i < verts.Length; i++)
                        verts[i] = ReadVec3(r);
                    var indices = new int[ReadBoundedCount(r, stream, sizeof(int), "indices")];
                    for (int i = 0; i < indices.Length; i++)
                        indices[i] = r.ReadInt32();
                    result.Add(CachedCollider.Mesh(t, verts, indices, isLadder, material));
                    break;
            }
        }

        // Finishing early is corruption too, and the quiet kind. Same-length damage that turns a count of
        // 1 into 0 leaves the header consistent, so IsCurrent accepts the file and every bound above
        // passes — Read would then hand back an empty list, no exception would reach ColliderLibrary, and
        // the object would load without collision forever. Ending anywhere but the declared boundary is
        // the signal that the payload is not what the header says it is.
        if (payloadStart >= 0 && stream.Position - payloadStart != declaredPayload)
        {
            throw new InvalidDataException(
                $"Collider cache parsed {stream.Position - payloadStart} bytes of a declared " +
                $"{declaredPayload}.");
        }

        return result;
    }

    // The file's physics-material names. Bounded the same way every other length prefix here is: a name is
    // at least its own one-byte length prefix, so a count past the bytes remaining is corruption rather
    // than an allocation to attempt.
    private static string[] ReadMaterialTable(BinaryReader r, Stream stream)
    {
        int count = r.ReadInt32();
        if (count < 0 || count >= MaxMaterialNames)
            throw new InvalidDataException($"Collider cache declares {count} physics-material names.");
        if (stream.CanSeek && count > stream.Length - stream.Position)
        {
            throw new InvalidDataException(
                $"Collider cache declares {count} physics-material names but carries only "
                + $"{stream.Length - stream.Position} bytes.");
        }

        var names = new string[count];
        for (int i = 0; i < count; i++)
            names[i] = r.ReadString();
        return names;
    }

    // Index 0 is "no material"; anything else names the table entry one before it. An index past the table
    // is corruption, but a benign kind — the collider still has its shape — so it reads as no material
    // rather than failing the whole file, which would leave the object with no collision at all.
    private static string MaterialAt(string[] names, byte index) =>
        index == NoMaterial || index > names.Length ? string.Empty : names[index - 1];

    // Reads a length prefix that is about to size an array, and refuses one the file cannot possibly back.
    // Bounding the collider count alone is not enough: a single mesh collider whose nested vertex or index
    // count is corrupted would still allocate before a byte of payload is read, and OutOfMemoryException is
    // not a decode failure — no caller catches it, so the load dies anyway.
    private static int ReadBoundedCount(BinaryReader r, Stream stream, int bytesPerItem, string what)
    {
        int count = r.ReadInt32();
        if (count < 0)
            throw new InvalidDataException($"Collider cache declares {count} {what}.");
        if (stream.CanSeek && (long)count * bytesPerItem > stream.Length - stream.Position)
        {
            throw new InvalidDataException(
                $"Collider cache declares {count} {what} but carries only " +
                $"{stream.Length - stream.Position} bytes.");
        }
        return count;
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
