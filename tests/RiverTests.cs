using System;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class RiverTests
{
    private static River Open(TempDir dir, byte[] bytes) => new(dir.Write("r.bin", bytes));

    [Fact]
    public void ReadsAllPrimitives()
    {
        using var dir = new TempDir();
        var guid = Guid.NewGuid();
        byte[] bytes = new RiverBytes()
            .Byte(200)
            .Bool(true)
            .UInt16(40000)
            .Int16(-1234)
            .UInt32(3_000_000_000)
            .Int32(-123456)
            .UInt64(12345678901234)
            .Single(3.5f)
            .Str("hello world")
            .Guid(guid)
            .Vector3(new Vector3(1, 2, 3))
            .Vector3(new Vector3(10, 20, 30)) // euler
            .ToArray();

        using River r = Open(dir, bytes);
        Assert.Equal(200, r.ReadByte());
        Assert.True(r.ReadBoolean());
        Assert.Equal(40000, r.ReadUInt16());
        Assert.Equal(-1234, r.ReadInt16());
        Assert.Equal(3_000_000_000u, r.ReadUInt32());
        Assert.Equal(-123456, r.ReadInt32());
        Assert.Equal(12345678901234ul, r.ReadUInt64());
        Assert.Equal(3.5f, r.ReadSingle());
        Assert.Equal("hello world", r.ReadString());
        Assert.Equal(guid, r.ReadGuid());
        Assert.Equal(new Vector3(1, 2, 3), r.ReadSingleVector3());
        Assert.Equal(new Vector3(10, 20, 30), r.ReadEulerDegrees());
    }

    [Fact]
    public void ReadByte_AtEof_ReturnsZero()
    {
        using var dir = new TempDir();
        using River r = Open(dir, Array.Empty<byte>());
        Assert.Equal(0, r.ReadByte());
    }

    [Fact]
    public void ReadString_Empty()
    {
        using var dir = new TempDir();
        using River r = Open(dir, new RiverBytes().Str("").ToArray());
        Assert.Equal("", r.ReadString());
    }

    [Fact]
    public void ReadGuid_WrongLength_ReturnsEmpty()
    {
        using var dir = new TempDir();
        using River r = Open(dir, new RiverBytes().Bytes(new byte[10]).ToArray());
        Assert.Equal(Guid.Empty, r.ReadGuid());
    }

    [Fact]
    public void Fill_PastEof_Throws()
    {
        using var dir = new TempDir();
        using River r = Open(dir, new byte[] { 1, 2 }); // only 2 bytes for a 4-byte read
        Assert.Throws<EndOfStreamException>(() => r.ReadInt32());
    }

    [Fact]
    public void ReadString_TruncatedPayload_Throws()
    {
        using var dir = new TempDir();
        // length prefix says 5 but only 2 bytes follow
        using River r = Open(dir, new byte[] { 5, (byte)'a', (byte)'b' });
        Assert.Throws<EndOfStreamException>(() => r.ReadString());
    }

    [Fact]
    public void ReadBytes_TruncatedPayload_Throws()
    {
        using var dir = new TempDir();
        // uint16 length 5, only 1 byte follows
        using River r = Open(dir, new byte[] { 5, 0, 9 });
        Assert.Throws<EndOfStreamException>(() => r.ReadGuid());
    }
}
