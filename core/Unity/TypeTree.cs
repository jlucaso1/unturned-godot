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
