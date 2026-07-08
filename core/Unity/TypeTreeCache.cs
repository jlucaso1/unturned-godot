using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Unity;

// Compact on-disk cache of the masterbundle's per-class-id type trees. Decoding an entity later (e.g. the
// character from the type-tree-stripped resources.assets) needs these trees, but they are tiny while the
// masterbundle SerializedFile they come from is ~171 MB decompressed — so cache them once instead of
// re-decoding the whole bundle. The caller supplies an invalidation stamp (the masterbundle's mtime + size)
// so a changed bundle is detected on read.
public static class TypeTreeCache
{
    private const uint Magic = 0x54544755; // "UGTT"

    public static void Write(Stream stream, IReadOnlyDictionary<int, List<TypeTreeNode>> trees, long stamp)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(stamp);
        w.Write(trees.Count);
        foreach (KeyValuePair<int, List<TypeTreeNode>> entry in trees)
        {
            w.Write(entry.Key);
            w.Write(entry.Value.Count);
            foreach (TypeTreeNode node in entry.Value)
            {
                w.Write(node.Level);
                w.Write(node.IsArray);
                w.Write(node.ByteSize);
                w.Write(node.MetaFlag);
                w.Write(node.Type);
                w.Write(node.Name);
            }
        }
    }

    // Returns the cached trees, or null if the stream isn't a type-tree cache (bad magic) or its stamp
    // doesn't match the expected one (a stale cache for a different masterbundle).
    public static Dictionary<int, List<TypeTreeNode>>? Read(Stream stream, long expectedStamp)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic || r.ReadInt64() != expectedStamp)
            return null;

        int entryCount = r.ReadInt32();
        var trees = new Dictionary<int, List<TypeTreeNode>>(entryCount);
        for (int e = 0; e < entryCount; e++)
        {
            int classId = r.ReadInt32();
            int nodeCount = r.ReadInt32();
            var nodes = new List<TypeTreeNode>(nodeCount);
            for (int n = 0; n < nodeCount; n++)
                nodes.Add(new TypeTreeNode
                {
                    Level = r.ReadInt32(),
                    IsArray = r.ReadBoolean(),
                    ByteSize = r.ReadInt32(),
                    MetaFlag = r.ReadInt32(),
                    Type = r.ReadString(),
                    Name = r.ReadString(),
                });
            trees[classId] = nodes;
        }
        return trees;
    }
}
