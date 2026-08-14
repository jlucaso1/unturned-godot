using System;
using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Tests.Helpers;

// Builds a SerializedFile holding a Unity prefab: GameObjects, the Transform tree that parents them, and
// the Rigidbody/BoxCollider/CharacterJoint components a ragdoll is made of.
//
// This is what resources.assets carries, and reading it is how the game's own ragdoll proportions and
// joint limits get into this project rather than being invented. Authoring the file here means the
// reader can be tested on shapes a real install does not conveniently contain — a prefab that is not
// there, a bone with a collider but no body, a joint pointing at a Rigidbody on nothing.
//
// Version 15, like AssetFileBuilder, and for the same reason: an object selects its type tree by class
// id, so a payload and the tree that reads it cannot drift apart unnoticed.
public sealed class PrefabFileBuilder
{
    public const int GameObjectClassId = 1;
    public const int TransformClassId = 4;
    public const int RigidbodyClassId = 54;
    public const int BoxColliderClassId = 65;
    public const int CharacterJointClassId = 144;

    // One bone as the caller describes it; the builder turns each into the four or five objects Unity
    // spreads it over.
    public sealed record Bone(
        string Name,
        string? Parent = null,
        float Mass = 1f,
        (float X, float Y, float Z) Center = default,
        (float X, float Y, float Z) Size = default,
        float TwistLow = 0f,
        float TwistHigh = 0f,
        float Swing1 = 0f,
        float Swing2 = 0f,
        bool HasBody = true,
        bool HasCollider = true);

    private readonly List<(long PathId, int ClassId, byte[] Payload)> _objects = new();
    private long _nextPathId = 1000;

    // A whole prefab: a root GameObject named `rootName`, with `bones` hanging off it by name.
    //
    // The root itself carries no Rigidbody, exactly as Ragdoll_Zombie does not — it is the empty holder
    // the bones are parented to, and a reader that counted it would invent a bone nothing animates.
    public PrefabFileBuilder AddPrefab(string rootName, IReadOnlyList<Bone> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);

        // Every id is allocated before anything is written, because a joint names the Rigidbody of a bone
        // that may be declared after it — a ragdoll is a tree, and its file is a flat list.
        long rootGo = Reserve();
        long rootTransform = Reserve();
        var slots = new Dictionary<string, Slot>(StringComparer.Ordinal);
        foreach (Bone bone in bones)
        {
            slots[bone.Name] = new Slot(
                GameObject: Reserve(),
                Transform: Reserve(),
                Body: bone.HasBody ? Reserve() : 0,
                Collider: bone.HasCollider ? Reserve() : 0,
                Joint: bone.Parent != null ? Reserve() : 0);
        }

        var rootChildren = new List<long>();
        var childrenOf = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (Bone bone in bones)
            childrenOf[bone.Name] = new List<long>();

        foreach (Bone bone in bones)
        {
            Slot slot = slots[bone.Name];
            var components = new List<long>();

            if (slot.Body != 0)
            {
                components.Add(slot.Body);
                Emit(slot.Body, RigidbodyClassId, RigidbodyPayload(slot.GameObject, bone.Mass));
            }

            if (slot.Collider != 0)
            {
                components.Add(slot.Collider);
                Emit(slot.Collider, BoxColliderClassId,
                    BoxColliderPayload(slot.GameObject, bone.Center, bone.Size));
            }

            if (slot.Joint != 0)
            {
                components.Add(slot.Joint);
                // m_ConnectedBody names the PARENT's Rigidbody, which is how the reader recovers a
                // bone's parent by name. A parent with no body leaves it zero, and the bone comes back
                // parented to nothing — which is the shape a broken prefab has.
                long connected = bone.Parent != null && slots.TryGetValue(bone.Parent, out Slot parent)
                    ? parent.Body
                    : 0;
                Emit(slot.Joint, CharacterJointClassId, CharacterJointPayload(slot.GameObject, connected,
                    bone.TwistLow, bone.TwistHigh, bone.Swing1, bone.Swing2));
            }

            Emit(slot.GameObject, GameObjectClassId, GameObjectPayload(bone.Name, components));

            if (bone.Parent != null && childrenOf.TryGetValue(bone.Parent, out List<long>? siblings))
                siblings.Add(slot.Transform);
            else
                rootChildren.Add(slot.Transform);
        }

        foreach (Bone bone in bones)
        {
            Slot slot = slots[bone.Name];
            Emit(slot.Transform, TransformClassId,
                TransformPayload(slot.GameObject, childrenOf[bone.Name]));
        }

        // The root carries no Rigidbody, exactly as Ragdoll_Zombie does not: it is the empty holder the
        // bones hang off, and a reader that counted it would invent a bone nothing animates.
        Emit(rootGo, GameObjectClassId, GameObjectPayload(rootName, Array.Empty<long>()));
        Emit(rootTransform, TransformClassId, TransformPayload(rootGo, rootChildren));
        return this;
    }

    private readonly record struct Slot(long GameObject, long Transform, long Body, long Collider,
        long Joint);

    private long Reserve() => _nextPathId++;

    private void Emit(long pathId, int classId, byte[] payload) =>
        _objects.Add((pathId, classId, payload));

    public byte[] Build()
    {
        var types = new List<(int ClassId, byte[] Tree)>
        {
            (GameObjectClassId, GameObjectTree()),
            (TransformClassId, TransformTree()),
            (RigidbodyClassId, RigidbodyTree()),
            (BoxColliderClassId, BoxColliderTree()),
            (CharacterJointClassId, CharacterJointTree()),
        };

        var meta = new List<byte>();
        WriteCString(meta, "5.x.x");
        WriteI32(meta, 0);
        meta.Add(1); // enable type tree
        WriteI32(meta, types.Count);
        foreach ((int classId, byte[] tree) in types)
        {
            WriteI32(meta, classId);
            meta.AddRange(new byte[16]); // old type hash
            meta.AddRange(tree);
        }

        _objects.Sort((a, b) => a.PathId.CompareTo(b.PathId));
        WriteI32(meta, _objects.Count);
        int byteStart = 0;
        foreach ((long pathId, int classId, byte[] payload) in _objects)
        {
            while (meta.Count % 4 != 0)
                meta.Add(0);
            WriteI64(meta, pathId);
            WriteU32(meta, (uint)byteStart);
            WriteU32(meta, (uint)payload.Length);
            WriteI32(meta, 0);
            WriteU16(meta, (ushort)classId);
            WriteI16(meta, 0);
            meta.Add(0);
            byteStart += (payload.Length + 3) & ~3;
        }

        var header = new List<byte>();
        WriteU32Be(header, 0);
        WriteU32Be(header, 0);
        WriteU32Be(header, 15);
        int dataOffsetPos = header.Count;
        WriteU32Be(header, 0);
        header.Add(0);
        header.AddRange(new byte[3]);

        var all = new List<byte>(header);
        all.AddRange(meta);
        while (all.Count % 16 != 0)
            all.Add(0);
        uint dataOffset = (uint)all.Count;
        for (int i = 0; i < 4; i++)
            all[dataOffsetPos + i] = (byte)(dataOffset >> ((3 - i) * 8));
        foreach ((long _, int _, byte[] payload) in _objects)
        {
            all.AddRange(payload);
            while ((all.Count - dataOffset) % 4 != 0)
                all.Add(0);
        }

        return all.ToArray();
    }

    // ---- payloads ---------------------------------------------------------------------------------

    private static byte[] GameObjectPayload(string name, IReadOnlyList<long> components)
    {
        var body = new List<byte>();
        WriteI32(body, components.Count);
        foreach (long component in components)
        {
            WriteI32(body, 0); // m_FileID
            WriteI64(body, component);
        }

        Align(body);
        WriteString(body, name);
        return body.ToArray();
    }

    private static byte[] TransformPayload(long gameObject, IReadOnlyList<long> children)
    {
        var body = new List<byte>();
        WriteI32(body, 0);
        WriteI64(body, gameObject);
        WriteI32(body, children.Count);
        foreach (long child in children)
        {
            WriteI32(body, 0);
            WriteI64(body, child);
        }

        Align(body);
        return body.ToArray();
    }

    private static byte[] RigidbodyPayload(long gameObject, float mass)
    {
        var body = new List<byte>();
        WriteI32(body, 0);
        WriteI64(body, gameObject);
        WriteFloat(body, mass);
        Align(body);
        return body.ToArray();
    }

    private static byte[] BoxColliderPayload(long gameObject, (float X, float Y, float Z) center,
        (float X, float Y, float Z) size)
    {
        var body = new List<byte>();
        WriteI32(body, 0);
        WriteI64(body, gameObject);
        WriteVector(body, size);
        WriteVector(body, center);
        Align(body);
        return body.ToArray();
    }

    private static byte[] CharacterJointPayload(long gameObject, long connectedBody, float twistLow,
        float twistHigh, float swing1, float swing2)
    {
        var body = new List<byte>();
        WriteI32(body, 0);
        WriteI64(body, gameObject);
        WriteI32(body, 0);
        WriteI64(body, connectedBody);
        WriteLimit(body, twistLow);
        WriteLimit(body, twistHigh);
        WriteLimit(body, swing1);
        WriteLimit(body, swing2);
        Align(body);
        return body.ToArray();
    }

    // ---- type trees -------------------------------------------------------------------------------

    private static byte[] GameObjectTree()
    {
        var t = new TreeWriter();
        t.Node(0, "GameObject", "Base", -1);
        t.Node(1, "vector", "m_Component", -1);
        t.Node(2, "Array", "Array", -1, array: true, align: true);
        t.Node(3, "int", "size", 4);
        t.Node(3, "ComponentPair", "data", -1);
        t.Node(4, "PPtr<Component>", "component", -1);
        t.Node(5, "int", "m_FileID", 4);
        t.Node(5, "SInt64", "m_PathID", 8);
        t.String(1, "m_Name");
        return t.Build();
    }

    private static byte[] TransformTree()
    {
        var t = new TreeWriter();
        t.Node(0, "Transform", "Base", -1);
        t.Node(1, "PPtr<GameObject>", "m_GameObject", -1);
        t.Node(2, "int", "m_FileID", 4);
        t.Node(2, "SInt64", "m_PathID", 8);
        t.Node(1, "vector", "m_Children", -1);
        t.Node(2, "Array", "Array", -1, array: true, align: true);
        t.Node(3, "int", "size", 4);
        t.Node(3, "PPtr<Transform>", "data", -1);
        t.Node(4, "int", "m_FileID", 4);
        t.Node(4, "SInt64", "m_PathID", 8);
        return t.Build();
    }

    private static byte[] RigidbodyTree()
    {
        var t = new TreeWriter();
        t.Node(0, "Rigidbody", "Base", -1);
        t.Node(1, "PPtr<GameObject>", "m_GameObject", -1);
        t.Node(2, "int", "m_FileID", 4);
        t.Node(2, "SInt64", "m_PathID", 8);
        t.Node(1, "float", "m_Mass", 4);
        return t.Build();
    }

    private static byte[] BoxColliderTree()
    {
        var t = new TreeWriter();
        t.Node(0, "BoxCollider", "Base", -1);
        t.Node(1, "PPtr<GameObject>", "m_GameObject", -1);
        t.Node(2, "int", "m_FileID", 4);
        t.Node(2, "SInt64", "m_PathID", 8);
        Vector(t, 1, "m_Size");
        Vector(t, 1, "m_Center");
        return t.Build();
    }

    private static byte[] CharacterJointTree()
    {
        var t = new TreeWriter();
        t.Node(0, "CharacterJoint", "Base", -1);
        t.Node(1, "PPtr<GameObject>", "m_GameObject", -1);
        t.Node(2, "int", "m_FileID", 4);
        t.Node(2, "SInt64", "m_PathID", 8);
        t.Node(1, "PPtr<Rigidbody>", "m_ConnectedBody", -1);
        t.Node(2, "int", "m_FileID", 4);
        t.Node(2, "SInt64", "m_PathID", 8);
        Limit(t, 1, "m_LowTwistLimit");
        Limit(t, 1, "m_HighTwistLimit");
        Limit(t, 1, "m_Swing1Limit");
        Limit(t, 1, "m_Swing2Limit");
        return t.Build();
    }

    private static void Vector(TreeWriter t, int level, string name)
    {
        t.Node(level, "Vector3f", name, 12);
        t.Node(level + 1, "float", "x", 4);
        t.Node(level + 1, "float", "y", 4);
        t.Node(level + 1, "float", "z", 4);
    }

    // A SoftJointLimit carries a spring and a bounciness beside its angle; this port models neither, but
    // the bytes are still there and still have to be consumed in the right order.
    private static void Limit(TreeWriter t, int level, string name)
    {
        t.Node(level, "SoftJointLimit", name, 12);
        t.Node(level + 1, "float", "limit", 4);
        t.Node(level + 1, "float", "bounciness", 4);
        t.Node(level + 1, "float", "contactDistance", 4);
    }

    private sealed class TreeWriter
    {
        private readonly List<byte> _nodes = new();
        private readonly List<byte> _strings = new();
        private readonly Dictionary<string, uint> _offsets = new(StringComparer.Ordinal);
        private int _count;

        internal void Node(int level, string type, string name, int byteSize, bool array = false,
            bool align = false)
        {
            WriteU16(_nodes, 1);
            _nodes.Add((byte)level);
            _nodes.Add((byte)(array ? 1 : 0));
            WriteU32(_nodes, Offset(type));
            WriteU32(_nodes, Offset(name));
            WriteI32(_nodes, byteSize);
            WriteI32(_nodes, 0);
            WriteI32(_nodes, align ? 0x4000 : 0);
            _count++;
        }

        internal void String(int level, string name)
        {
            Node(level, "string", name, -1, align: true);
            Node(level + 1, "Array", "Array", -1, array: true, align: true);
            Node(level + 2, "int", "size", 4);
            Node(level + 2, "char", "data", 1);
        }

        internal byte[] Build()
        {
            var blob = new List<byte>();
            WriteI32(blob, _count);
            WriteI32(blob, _strings.Count);
            blob.AddRange(_nodes);
            blob.AddRange(_strings);
            return blob.ToArray();
        }

        private uint Offset(string value)
        {
            if (_offsets.TryGetValue(value, out uint offset))
                return offset;

            offset = (uint)_strings.Count;
            _strings.AddRange(Encoding.ASCII.GetBytes(value));
            _strings.Add(0);
            _offsets[value] = offset;
            return offset;
        }
    }

    // ---- little-endian writers --------------------------------------------------------------------

    private static void Align(List<byte> b)
    {
        while (b.Count % 4 != 0)
            b.Add(0);
    }

    private static void WriteVector(List<byte> b, (float X, float Y, float Z) v)
    {
        WriteFloat(b, v.X);
        WriteFloat(b, v.Y);
        WriteFloat(b, v.Z);
    }

    private static void WriteLimit(List<byte> b, float limit)
    {
        WriteFloat(b, limit);
        WriteFloat(b, 0f); // bounciness
        WriteFloat(b, 0f); // contactDistance
    }

    private static void WriteString(List<byte> b, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        WriteI32(b, bytes.Length);
        b.AddRange(bytes);
        Align(b);
    }

    private static void WriteCString(List<byte> b, string s)
    {
        b.AddRange(Encoding.ASCII.GetBytes(s));
        b.Add(0);
    }

    private static void WriteFloat(List<byte> b, float value) =>
        WriteU32(b, (uint)BitConverter.SingleToInt32Bits(value));

    private static void WriteU16(List<byte> b, ushort v)
    {
        b.Add((byte)v);
        b.Add((byte)(v >> 8));
    }

    private static void WriteI16(List<byte> b, short v) => WriteU16(b, (ushort)v);

    private static void WriteI32(List<byte> b, int v) => WriteU32(b, (uint)v);

    private static void WriteU32(List<byte> b, uint v)
    {
        for (int i = 0; i < 4; i++)
            b.Add((byte)(v >> (i * 8)));
    }

    private static void WriteI64(List<byte> b, long v)
    {
        for (int i = 0; i < 8; i++)
            b.Add((byte)((ulong)v >> (i * 8)));
    }

    private static void WriteU32Be(List<byte> b, uint v)
    {
        for (int i = 3; i >= 0; i--)
            b.Add((byte)(v >> (i * 8)));
    }
}
