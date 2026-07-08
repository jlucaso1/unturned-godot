using System;
using System.Collections.Generic;

namespace UnturnedGodot.Unity;

// One object entry from the SerializedFile's object table.
public sealed class SerializedObject
{
    public long PathId;
    public int ClassId;
    public long ByteStart; // absolute offset into the file bytes
    public int ByteSize;
    public List<TypeTreeNode> TypeTree = new();
}

// Parses a Unity SerializedFile (format version 22, the version Unturned's 2022.3 bundles use):
// header, type table (with type trees) and object table. Enough to locate and read Mesh objects.
public sealed class SerializedFile
{
    public bool BigEndian { get; private set; }
    public IReadOnlyList<SerializedObject> Objects { get; }

    // The canonical type tree for each built-in class id present in this file. Type trees are identical
    // across files of the same Unity version, so these can decode another file (e.g. the game's
    // resources.assets) that was saved with its type trees stripped.
    public IReadOnlyDictionary<int, List<TypeTreeNode>> TypeTreesByClassId { get; }

    private readonly byte[] _data;

    private SerializedFile(byte[] data, bool bigEndian, List<SerializedObject> objects,
        Dictionary<int, List<TypeTreeNode>> typeTreesByClassId)
    {
        _data = data;
        BigEndian = bigEndian;
        Objects = objects;
        TypeTreesByClassId = typeTreesByClassId;
    }

    // A reader positioned at the object's data, in the file's endianness.
    public UnityBinaryReader ReaderFor(SerializedObject obj) =>
        new(_data, BigEndian) { Position = (int)obj.ByteStart };

    public static SerializedFile Read(byte[] data,
        IReadOnlyDictionary<int, List<TypeTreeNode>>? classTypeTrees = null)
    {
        var r = new UnityBinaryReader(data, bigEndian: true); // header is always big-endian

        r.ReadUInt32();           // metadataSize (legacy)
        r.ReadUInt32();           // fileSize (legacy)
        int version = (int)r.ReadUInt32();
        long dataOffset = r.ReadUInt32(); // legacy dataOffset (kept for version < 22)

        // v22 is Unturned's 2022.3 masterbundle; v15 is the Unity 5.x per-map bundles (Roads.unity3d).
        if (version is not (15 or 22))
            throw new NotSupportedException($"Unsupported SerializedFile version {version}");

        byte endianess = r.ReadByte();
        r.ReadBytes(3);           // reserved
        if (version >= 22)
        {
            r.ReadUInt32();       // metadataSize
            r.ReadInt64();        // fileSize
            dataOffset = r.ReadInt64();
            r.ReadInt64();        // unknown
        }

        bool bigEndian = endianess != 0;
        r.BigEndian = bigEndian;  // metadata and data use the file endianness

        r.ReadCString();          // unity version
        r.ReadInt32();            // target platform
        bool enableTypeTree = r.ReadBoolean();

        int typeCount = r.ReadInt32();
        var typeClassIds = new int[typeCount];
        var typeTrees = new List<TypeTreeNode>[typeCount];
        for (int i = 0; i < typeCount; i++)
            (typeClassIds[i], typeTrees[i]) = ReadType(r, version, enableTypeTree);

        // Pre-16 objects reference their type by class id, not by an index into the type table.
        var treeByClassId = new Dictionary<int, List<TypeTreeNode>>();
        if (version < 16)
            for (int i = 0; i < typeCount; i++)
                treeByClassId[typeClassIds[i]] = typeTrees[i];

        // The canonical (non-empty) type tree per class id, for reuse against type-tree-stripped files.
        var typeTreesByClassId = new Dictionary<int, List<TypeTreeNode>>();
        for (int i = 0; i < typeCount; i++)
            if (typeTrees[i].Count > 0)
                typeTreesByClassId.TryAdd(typeClassIds[i], typeTrees[i]);

        int objectCount = r.ReadInt32();
        var objects = new List<SerializedObject>(objectCount);
        for (int i = 0; i < objectCount; i++)
        {
            r.Align4();
            long pathId = r.ReadInt64();
            long byteStart = (version >= 22 ? r.ReadInt64() : r.ReadUInt32()) + dataOffset;
            int byteSize = (int)r.ReadUInt32();
            int typeId = r.ReadInt32();

            int classId;
            List<TypeTreeNode> tree;
            if (version < 16)
            {
                classId = r.ReadUInt16();
                r.ReadInt16();    // script type index
                r.ReadByte();     // stripped (version 15/16)
                tree = treeByClassId.TryGetValue(classId, out List<TypeTreeNode>? t) ? t : new();
            }
            else
            {
                classId = typeClassIds[typeId];
                tree = typeTrees[typeId];
            }

            // When this file's type trees are stripped (enableTypeTree = 0), fall back to a supplied
            // per-class-id database (e.g. gathered from the masterbundle, same Unity version).
            if (tree.Count == 0 && classTypeTrees != null &&
                classTypeTrees.TryGetValue(classId, out List<TypeTreeNode>? provided))
                tree = provided;

            objects.Add(new SerializedObject
            {
                PathId = pathId,
                ClassId = classId,
                ByteStart = byteStart,
                ByteSize = byteSize,
                TypeTree = tree,
            });
        }

        return new SerializedFile(data, bigEndian, objects, typeTreesByClassId);
    }

    private static (int classId, List<TypeTreeNode> tree) ReadType(
        UnityBinaryReader r, int version, bool enableTypeTree)
    {
        int classId = r.ReadInt32();
        if (version >= 16)
        {
            r.ReadBoolean();  // is stripped type
            r.ReadInt16();    // script type index
        }

        // MonoBehaviour types carry an extra 16-byte script id (negative class id pre-16, else class 114).
        if ((version < 16 && classId < 0) || (version >= 16 && classId == 114))
            r.ReadBytes(16);
        r.ReadBytes(16);      // old type hash

        var tree = new List<TypeTreeNode>();
        if (enableTypeTree)
        {
            tree = TypeTree.ReadBlob(r, version);
            if (version >= 21)
            {
                int dependencyCount = r.ReadInt32(); // type dependencies
                for (int i = 0; i < dependencyCount; i++)
                    r.ReadInt32();
            }
        }
        return (classId, tree);
    }
}
