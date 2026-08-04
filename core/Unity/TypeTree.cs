using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Unity;

// One node of a Unity TypeTree (blob format). Describes a field: its type, name, size and the
// alignment/array flags needed to walk an object's bytes.
public sealed class TypeTreeNode
{
    public int Level;
    public bool IsArray;
    public string Type = string.Empty;
    public string Name = string.Empty;
    public int ByteSize;
    public int MetaFlag;

    // Bit 0x4000 (kAlignBytesFlag) means the reader must align to 4 bytes after this field.
    public bool AlignAfter => (MetaFlag & 0x4000) != 0;
}

public static class TypeTree
{
    // Unity writes a node's nesting depth as a byte in the blob format, so nothing legitimate nests
    // deeper than this; the cap keeps a corrupt legacy tree from recursing the stack away.
    private const int MaxLevel = 255;

    // Reads the pre-blob type tree (format < 12, the Unity 4.x per-map bundles): each node is written
    // inline as its own fields followed by its children, so the flat list this returns comes from a
    // depth-first walk. Node order and levels match what ReadBlob produces, which is all TypeTreeReader
    // needs to rebuild the hierarchy.
    public static List<TypeTreeNode> ReadLegacy(UnityBinaryReader r)
    {
        var nodes = new List<TypeTreeNode>();
        ReadLegacyNode(r, level: 0, nodes);
        return nodes;
    }

    private static void ReadLegacyNode(UnityBinaryReader r, int level, List<TypeTreeNode> nodes)
    {
        if (level > MaxLevel)
            throw new System.NotSupportedException($"Type tree nested deeper than {MaxLevel} levels");

        string type = r.ReadCString();
        string name = r.ReadCString();
        int byteSize = r.ReadInt32();
        r.ReadInt32();          // index
        int isArray = r.ReadInt32();
        r.ReadInt32();          // node version
        int metaFlag = r.ReadInt32();

        nodes.Add(new TypeTreeNode
        {
            Level = level,
            IsArray = isArray != 0,
            Type = type,
            Name = name,
            ByteSize = byteSize,
            MetaFlag = metaFlag,
        });

        int childCount = r.ReadInt32();
        for (int i = 0; i < childCount; i++)
            ReadLegacyNode(r, level + 1, nodes);
    }

    // Reads the blob-format type tree (Unity 2019+/format >= 12): a flat node array plus a string
    // buffer that node type/name offsets index into (or the shared CommonString table).
    public static List<TypeTreeNode> ReadBlob(UnityBinaryReader r, int formatVersion)
    {
        int nodeCount = r.ReadInt32();
        int stringBufferSize = r.ReadInt32();

        var raw = new (ushort version, byte level, byte flags, uint typeOffset, uint nameOffset,
            int byteSize, int index, int metaFlag)[nodeCount];

        for (int i = 0; i < nodeCount; i++)
        {
            ushort version = r.ReadUInt16();
            byte level = r.ReadByte();
            byte flags = r.ReadByte();
            uint typeOffset = r.ReadUInt32();
            uint nameOffset = r.ReadUInt32();
            int byteSize = r.ReadInt32();
            int index = r.ReadInt32();
            int metaFlag = r.ReadInt32();
            if (formatVersion >= 19)
                r.ReadUInt64(); // ref type hash
            raw[i] = (version, level, flags, typeOffset, nameOffset, byteSize, index, metaFlag);
        }

        byte[] stringBuffer = r.ReadBytes(stringBufferSize);

        var nodes = new List<TypeTreeNode>(nodeCount);
        foreach (var n in raw)
        {
            nodes.Add(new TypeTreeNode
            {
                Level = n.level,
                IsArray = n.flags != 0,
                Type = ResolveString(n.typeOffset, stringBuffer),
                Name = ResolveString(n.nameOffset, stringBuffer),
                ByteSize = n.byteSize,
                MetaFlag = n.metaFlag,
            });
        }
        return nodes;
    }

    private static string ResolveString(uint offset, byte[] localBuffer)
    {
        // The high bit selects the shared table; otherwise index into the local string buffer.
        if ((offset & 0x80000000) != 0)
            return CommonString.Get(offset & 0x7FFFFFFF);

        int start = (int)offset;
        int end = start;
        while (end < localBuffer.Length && localBuffer[end] != 0)
            end++;
        return Encoding.UTF8.GetString(localBuffer, start, end - start);
    }
}
