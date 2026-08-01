using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace UnturnedGodot.Unity;

// Reads an object's bytes into a dynamic value using its TypeTree: structs become
// Dictionary<string, object>, arrays become List<object> (or byte[] for byte-sized elements),
// and leaves become boxed primitives/strings. Alignment follows each node's kAlignBytes flag.
public static class TypeTreeReader
{
    private sealed class Tree
    {
        public TypeTreeNode Node = null!;
        public readonly List<Tree> Children = new();
    }

    // Types are shared across thousands of objects, so cache the built hierarchy by node-list identity.
    // Concurrent because a load decodes several bundles at once, one thread each: a plain Dictionary
    // written from two of them corrupts its buckets. Two threads racing to build the same tree just build
    // it twice and keep one; the tree is derived purely from `nodes`, so either is the same.
    private static readonly ConcurrentDictionary<List<TypeTreeNode>, Tree> TreeCache = new();

    public static Dictionary<string, object> Read(List<TypeTreeNode> nodes, UnityBinaryReader reader) =>
        (Dictionary<string, object>)ReadValue(TreeCache.GetOrAdd(nodes, BuildTree), reader);

    private static Tree BuildTree(List<TypeTreeNode> nodes)
    {
        var root = new Tree { Node = nodes[0] };
        var stack = new Stack<Tree>();
        stack.Push(root);
        for (int i = 1; i < nodes.Count; i++)
        {
            TypeTreeNode node = nodes[i];
            while (stack.Peek().Node.Level >= node.Level)
                stack.Pop();
            var child = new Tree { Node = node };
            stack.Peek().Children.Add(child);
            stack.Push(child);
        }
        return root;
    }

    private static object ReadValue(Tree t, UnityBinaryReader r)
    {
        TypeTreeNode node = t.Node;
        bool align = node.AlignAfter;
        object value;

        switch (node.Type)
        {
            case "SInt8": value = (sbyte)r.ReadByte(); break;
            case "UInt8": case "char": value = r.ReadByte(); break;
            case "SInt16": case "short": value = r.ReadInt16(); break;
            case "UInt16": case "unsigned short": value = r.ReadUInt16(); break;
            case "SInt32": case "int": value = r.ReadInt32(); break;
            case "UInt32": case "unsigned int": case "Type*": value = r.ReadUInt32(); break;
            case "SInt64": case "long long": value = r.ReadInt64(); break;
            case "UInt64": case "unsigned long long": case "FileSize": value = r.ReadUInt64(); break;
            case "float": value = BitConverterFloat(r); break;
            case "bool": value = r.ReadBoolean(); break;
            case "string": value = ReadString(t, r); align = true; break;
            case "TypelessData": value = ReadTypelessData(r); break;
            default:
                if (t.Children.Count == 1 && t.Children[0].Node.IsArray)
                    value = ReadArray(t.Children[0], r);
                else if (node.IsArray)
                    value = ReadArray(t, r);
                else
                    value = ReadStruct(t, r);
                break;
        }

        if (align)
            r.Align4();
        return value;
    }

    private static Dictionary<string, object> ReadStruct(Tree t, UnityBinaryReader r)
    {
        var dict = new Dictionary<string, object>(t.Children.Count); // exact size: one entry per child
        foreach (Tree child in t.Children)
            dict[child.Node.Name] = ReadValue(child, r);
        return dict;
    }

    // An "Array" node has two children: [0] size (int), [1] the element template.
    private static object ReadArray(Tree arrayNode, UnityBinaryReader r)
    {
        Tree sizeNode = arrayNode.Children[0];
        Tree dataNode = arrayNode.Children[1];

        int size = (int)ReadValue(sizeNode, r);

        // Byte-sized elements are returned as a raw buffer.
        if (dataNode.Children.Count == 0 && dataNode.Node.ByteSize == 1)
        {
            byte[] bytes = r.ReadBytes(size);
            if (arrayNode.Node.AlignAfter)
                r.Align4();
            return bytes;
        }

        var list = new List<object>(size);
        for (int i = 0; i < size; i++)
            list.Add(ReadValue(dataNode, r));
        if (arrayNode.Node.AlignAfter)
            r.Align4();
        return list;
    }

    private static string ReadString(Tree t, UnityBinaryReader r)
    {
        // A string node wraps an Array of chars: read its length, then the bytes.
        Tree arrayNode = t.Children[0];
        int size = (int)ReadValue(arrayNode.Children[0], r);
        byte[] bytes = r.ReadBytes(size);
        return Encoding.UTF8.GetString(bytes);
    }

    private static byte[] ReadTypelessData(UnityBinaryReader r)
    {
        int size = r.ReadInt32();
        return r.ReadBytes(size);
    }

    private static float BitConverterFloat(UnityBinaryReader r)
    {
        uint bits = r.ReadUInt32();
        return System.BitConverter.Int32BitsToSingle((int)bits);
    }
}
