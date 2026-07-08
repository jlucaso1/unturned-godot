using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class TypeTreeBlobTests
{
    [Fact]
    public void ReadBlob_OlderVersion_NoRefTypeHash_AndCommonString()
    {
        // Version 18 (< 19) omits the ref-type hash. Type offset uses the common-string table.
        byte[] stringBuffer = System.Text.Encoding.ASCII.GetBytes("field\0");
        byte[] bytes = new RiverBytes()
            .Int32(1)                       // node count
            .Int32(stringBuffer.Length)     // string buffer size
            .UInt16(1)                      // node version
            .Byte(0)                        // level
            .Byte(0)                        // type flags
            .UInt32(0x80000000 | 208)       // type offset -> common string "GUID"
            .UInt32(0)                      // name offset -> local "field"
            .Int32(4)                       // byte size
            .Int32(0)                       // index
            .Int32(0)                       // meta flag
            .ToArray();
        var full = new System.Collections.Generic.List<byte>(bytes);
        full.AddRange(stringBuffer);

        var nodes = TypeTree.ReadBlob(new UnityBinaryReader(full.ToArray(), bigEndian: false), formatVersion: 18);
        Assert.Single(nodes);
        Assert.Equal("GUID", nodes[0].Type);
        Assert.Equal("field", nodes[0].Name);
    }

    [Fact]
    public void ReadBlob_LocalStringWithoutTerminator_ReadsToBufferEnd()
    {
        // A name string whose bytes run to the end of the buffer with no null terminator.
        byte[] stringBuffer = System.Text.Encoding.ASCII.GetBytes("abc");
        byte[] bytes = new RiverBytes()
            .Int32(1).Int32(stringBuffer.Length)
            .UInt16(1).Byte(0).Byte(0)
            .UInt32(0x80000000 | 208)   // type -> common "GUID"
            .UInt32(0)                  // name -> local "abc" (no terminator)
            .Int32(4).Int32(0).Int32(0).UInt64(0)
            .ToArray();
        var full = new System.Collections.Generic.List<byte>(bytes);
        full.AddRange(stringBuffer);

        var nodes = TypeTree.ReadBlob(new UnityBinaryReader(full.ToArray(), bigEndian: false), formatVersion: 22);
        Assert.Equal("abc", nodes[0].Name);
    }
}
