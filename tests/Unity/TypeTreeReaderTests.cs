using System.Collections.Generic;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TypeTreeReaderTests
{
    private static TypeTreeNode N(int level, string type, string name, int byteSize = 0,
        bool array = false, bool align = false) =>
        new()
        {
            Level = level,
            Type = type,
            Name = name,
            ByteSize = byteSize,
            IsArray = array,
            MetaFlag = align ? 0x4000 : 0,
        };

    private static Dictionary<string, object> Read(List<TypeTreeNode> nodes, byte[] bytes) =>
        TypeTreeReader.Read(nodes, new UnityBinaryReader(bytes, bigEndian: false));

    [Fact]
    public void ReadsAllPrimitiveTypes()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "SInt8", "a"), N(1, "UInt8", "b"), N(1, "SInt16", "c"), N(1, "UInt16", "d"),
            N(1, "int", "e"), N(1, "unsigned int", "f"), N(1, "SInt64", "g"), N(1, "UInt64", "h"),
            N(1, "float", "i"), N(1, "bool", "j"),
        };
        byte[] bytes = new RiverBytes()
            .Byte(0xFB)         // sbyte -5
            .Byte(200)
            .Int16(-300)
            .UInt16(40000)
            .Int32(100000)
            .UInt32(3_000_000_000)
            .UInt64(999)        // SInt64 read as long
            .UInt64(12345)
            .Single(1.5f)
            .Byte(1)
            .ToArray();

        Dictionary<string, object> d = Read(nodes, bytes);
        Assert.Equal((sbyte)-5, d["a"]);
        Assert.Equal((byte)200, d["b"]);
        Assert.Equal((short)-300, d["c"]);
        Assert.Equal((ushort)40000, d["d"]);
        Assert.Equal(100000, d["e"]);
        Assert.Equal(3_000_000_000u, d["f"]);
        Assert.Equal(999L, d["g"]);
        Assert.Equal(12345ul, d["h"]);
        Assert.Equal(1.5f, d["i"]);
        Assert.Equal(true, d["j"]);
    }

    [Fact]
    public void ReadsAllTypeAliases()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "char", "c1"), N(1, "short", "s1"), N(1, "unsigned short", "s2"),
            N(1, "SInt32", "i1"), N(1, "UInt32", "u1"), N(1, "Type*", "p1"),
            N(1, "long long", "l1"), N(1, "unsigned long long", "l2"), N(1, "FileSize", "l3"),
        };
        byte[] bytes = new RiverBytes()
            .Byte(65).Int16(-1).UInt16(2).Int32(3).UInt32(4).UInt32(5)
            .UInt64(6).UInt64(7).UInt64(8).ToArray();

        Dictionary<string, object> d = Read(nodes, bytes);
        Assert.Equal((byte)65, d["c1"]);
        Assert.Equal((short)-1, d["s1"]);
        Assert.Equal(3, d["i1"]);
        Assert.Equal(5u, d["p1"]);
        Assert.Equal(8ul, d["l3"]);
    }

    [Fact]
    public void AlignsAfterFlaggedField()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "bool", "flag", align: true),
            N(1, "int", "value"),
        };
        // bool at 0, align pads to 4, int at offset 4.
        byte[] bytes = { 1, 0xFF, 0xFF, 0xFF, 0x2A, 0x00, 0x00, 0x00 };
        Dictionary<string, object> d = Read(nodes, bytes);
        Assert.Equal(true, d["flag"]);
        Assert.Equal(42, d["value"]);
    }

    [Fact]
    public void ReadsIntArray()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "vector", "v"),
            N(2, "Array", "Array", array: true, align: true),
            N(3, "int", "size"),
            N(3, "int", "data"),
        };
        byte[] bytes = new RiverBytes().Int32(3).Int32(10).Int32(20).Int32(30).ToArray();
        var list = (List<object>)Read(nodes, bytes)["v"];
        Assert.Equal(new object[] { 10, 20, 30 }, list);
    }

    [Fact]
    public void ReadsByteArray()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "vector", "v"),
            N(2, "Array", "Array", array: true, align: true),
            N(3, "int", "size"),
            N(3, "UInt8", "data", byteSize: 1),
        };
        // 3 bytes then a 1-byte pad from the array alignment; the array content is unaffected.
        byte[] bytes = new RiverBytes().Int32(3).Byte(1).Byte(2).Byte(3).Byte(0).ToArray();
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])Read(nodes, bytes)["v"]);
    }

    [Fact]
    public void ReadsByteArray_NoAlign()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "vector", "v"),
            N(2, "Array", "Array", array: true), // no align flag
            N(3, "int", "size"),
            N(3, "UInt8", "data", byteSize: 1),
        };
        byte[] bytes = new RiverBytes().Int32(2).Byte(7).Byte(8).ToArray();
        Assert.Equal(new byte[] { 7, 8 }, (byte[])Read(nodes, bytes)["v"]);
    }

    [Fact]
    public void ReadsArrayOfStructs()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "vector", "v"),
            N(2, "Array", "Array", array: true),
            N(3, "int", "size"),
            N(3, "Pair", "data"),
            N(4, "int", "a"),
            N(4, "int", "b"),
        };
        byte[] bytes = new RiverBytes().Int32(2).Int32(1).Int32(2).Int32(3).Int32(4).ToArray();
        var list = (List<object>)Read(nodes, bytes)["v"];
        Assert.Equal(2, ((Dictionary<string, object>)list[0])["b"]);
        Assert.Equal(3, ((Dictionary<string, object>)list[1])["a"]);
    }

    [Fact]
    public void CachesTreePerNodeList()
    {
        var nodes = new List<TypeTreeNode> { N(0, "Base", "Base"), N(1, "int", "x") };
        byte[] bytes = new RiverBytes().Int32(5).ToArray();
        Assert.Equal(5, Read(nodes, bytes)["x"]);
        Assert.Equal(5, Read(nodes, bytes)["x"]); // second call hits the tree cache
    }

    [Fact]
    public void ReadsString()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "string", "s"),
            N(2, "Array", "Array", array: true, align: true),
            N(3, "int", "size"),
            N(3, "char", "data", byteSize: 1),
        };
        byte[] bytes = new RiverBytes().Int32(5).ToArray();
        var full = new List<byte>(bytes);
        full.AddRange(System.Text.Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("hello", Read(nodes, full.ToArray())["s"]);
    }

    [Fact]
    public void ReadsDouble_AndKeepsTheFieldsAfterItInStep()
    {
        // "double" is one of Unity's own common type strings, so a tree can name it. Without a case it
        // fell to `default`, was treated as a struct with no children and consumed ZERO bytes — so the
        // int after it read the double's first four bytes and every later field was off by eight. The
        // trailing field is the whole point of this test: the defect was never in the double itself.
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "double", "d"),
            N(1, "int", "after"),
        };
        byte[] bytes = new RiverBytes().Double(-1234.5678).Int32(42).ToArray();

        Dictionary<string, object> d = Read(nodes, bytes);

        Assert.Equal(-1234.5678, Assert.IsType<double>(d["d"]), 9);
        Assert.Equal(42, d["after"]);
    }

    [Fact]
    public void ReadsTypelessData()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "TypelessData", "raw"),
        };
        byte[] bytes = new RiverBytes().Int32(3).Byte(9).Byte(8).Byte(7).ToArray();
        Assert.Equal(new byte[] { 9, 8, 7 }, (byte[])Read(nodes, bytes)["raw"]);
    }

    [Fact]
    public void DirectArrayNode()
    {
        // A struct field that is itself an "Array" node (a sibling keeps Base from looking like a vector).
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "int", "x"),
            N(1, "Array", "arr", array: true),
            N(2, "int", "size"),
            N(2, "int", "data"),
        };
        byte[] bytes = new RiverBytes().Int32(9).Int32(2).Int32(5).Int32(6).ToArray();
        Dictionary<string, object> d = Read(nodes, bytes);
        Assert.Equal(9, d["x"]);
        Assert.Equal(new object[] { 5, 6 }, (List<object>)d["arr"]);
    }

    [Fact]
    public void NestedStruct()
    {
        var nodes = new List<TypeTreeNode>
        {
            N(0, "Base", "Base"),
            N(1, "Inner", "inner"),
            N(2, "int", "x"),
            N(2, "int", "y"),
        };
        byte[] bytes = new RiverBytes().Int32(7).Int32(11).ToArray();
        var inner = (Dictionary<string, object>)Read(nodes, bytes)["inner"];
        Assert.Equal(7, inner["x"]);
        Assert.Equal(11, inner["y"]);
    }
}
