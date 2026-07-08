using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class UnityBinaryReaderTests
{
    [Fact]
    public void BigEndian_ReadsAllPrimitives()
    {
        byte[] data =
        {
            0x2A,                                           // byte 42
            0x01,                                           // bool true
            0x12, 0x34,                                     // uint16 0x1234
            0xFF, 0xFE,                                     // int16 -2
            0x00, 0x00, 0x00, 0x05,                         // uint32 5
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, // uint64 7
            (byte)'h', (byte)'i', 0x00,                     // cstring "hi"
        };
        var r = new UnityBinaryReader(data, bigEndian: true);
        Assert.Equal(42, r.ReadByte());
        Assert.True(r.ReadBoolean());
        Assert.Equal(0x1234, r.ReadUInt16());
        Assert.Equal(-2, r.ReadInt16());
        Assert.Equal(5u, r.ReadUInt32());
        Assert.Equal(7ul, r.ReadUInt64());
        Assert.Equal("hi", r.ReadCString());
        Assert.Equal(21, r.Length);
    }

    [Fact]
    public void LittleEndian_And_SignedReads()
    {
        byte[] data = { 0x34, 0x12, 0xFB, 0xFF, 0xFF, 0xFF, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var r = new UnityBinaryReader(data, bigEndian: false);
        Assert.Equal(0x1234, r.ReadUInt16());
        Assert.Equal(-5, r.ReadInt32());
        Assert.Equal(8L, r.ReadInt64());
    }

    [Fact]
    public void Align_SkipsPadding()
    {
        var r = new UnityBinaryReader(new byte[16], bigEndian: true) { Position = 1 };
        r.Align4();
        Assert.Equal(4, r.Position);
        r.Align4(); // already aligned -> no-op
        Assert.Equal(4, r.Position);
        r.Position = 5;
        r.Align(8);
        Assert.Equal(8, r.Position);
        r.Align(8); // already aligned -> no-op
        Assert.Equal(8, r.Position);
    }

    [Fact]
    public void ReadBytes_ReturnsSlice()
    {
        var r = new UnityBinaryReader(new byte[] { 1, 2, 3, 4 }, bigEndian: true) { Position = 1 };
        Assert.Equal(new byte[] { 2, 3 }, r.ReadBytes(2));
    }
}
