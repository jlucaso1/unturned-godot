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
    private readonly byte[] _data;

    private SerializedFile(byte[] data, bool bigEndian, List<SerializedObject> objects)
    {
        _data = data;
        BigEndian = bigEndian;
        Objects = objects;
    }

    // A reader positioned at the object's data, in the file's endianness.
    public UnityBinaryReader ReaderFor(SerializedObject obj) =>
        new(_data, BigEndian) { Position = (int)obj.ByteStart };

    public static SerializedFile Read(byte[] data)
    {
        var r = new UnityBinaryReader(data, bigEndian: true); // header is always big-endian

        r.ReadUInt32();           // metadataSize (legacy)
        r.ReadUInt32();           // fileSize (legacy)
        int version = (int)r.ReadUInt32();
        r.ReadUInt32();           // dataOffset (legacy)

        if (version < 22)
            throw new NotSupportedException($"Unsupported SerializedFile version {version}");

        byte endianess = r.ReadByte();
        r.ReadBytes(3);           // reserved
        r.ReadUInt32();           // metadataSize
        r.ReadInt64();            // fileSize
        long dataOffset = r.ReadInt64();
        r.ReadInt64();            // unknown

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

        int objectCount = r.ReadInt32();
        var objects = new List<SerializedObject>(objectCount);
        for (int i = 0; i < objectCount; i++)
        {
            r.Align4();
            long pathId = r.ReadInt64();
            long byteStart = r.ReadInt64() + dataOffset;
            int byteSize = (int)r.ReadUInt32();
            int typeId = r.ReadInt32();
            objects.Add(new SerializedObject
            {
                PathId = pathId,
                ClassId = typeClassIds[typeId],
                ByteStart = byteStart,
                ByteSize = byteSize,
                TypeTree = typeTrees[typeId],
            });
        }

        return new SerializedFile(data, bigEndian, objects);
    }

    private static (int classId, List<TypeTreeNode> tree) ReadType(
        UnityBinaryReader r, int version, bool enableTypeTree)
    {
        int classId = r.ReadInt32();
        r.ReadBoolean();  // is stripped type
        r.ReadInt16();    // script type index

        // MonoBehaviour types carry an extra 16-byte script id.
        if (classId == 114)
            r.ReadBytes(16);
        r.ReadBytes(16);  // old type hash

        var tree = new List<TypeTreeNode>();
        if (enableTypeTree)
        {
            tree = TypeTree.ReadBlob(r, version);
            int dependencyCount = r.ReadInt32(); // type dependencies (version >= 21)
            for (int i = 0; i < dependencyCount; i++)
                r.ReadInt32();
        }
        return (classId, tree);
    }
}
