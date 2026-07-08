using System;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class Lz4Tests
{
    [Fact]
    public void LiteralsOnly()
    {
        // token 0x30 = 3 literals, 0 match; block ends after literals.
        byte[] src = { 0x30, (byte)'A', (byte)'B', (byte)'C' };
        Assert.Equal("ABC", System.Text.Encoding.ASCII.GetString(Lz4.Decompress(src, 3)));
    }

    [Fact]
    public void ExtendedLiteralLength()
    {
        // token 0xF0 = literal length 15 + extension byte 0 = 15 literals.
        var src = new byte[2 + 15];
        src[0] = 0xF0;
        src[1] = 0x00;
        for (int i = 0; i < 15; i++)
            src[2 + i] = (byte)(i + 1);
        byte[] outp = Lz4.Decompress(src, 15);
        for (int i = 0; i < 15; i++)
            Assert.Equal(i + 1, outp[i]);
    }

    [Fact]
    public void OverlappingMatch()
    {
        // 1 literal 'a', then match offset 1, length nibble 2 (+4 = 6) -> 7 'a's total.
        byte[] src = { 0x12, (byte)'a', 0x01, 0x00 };
        Assert.Equal(new string('a', 7), System.Text.Encoding.ASCII.GetString(Lz4.Decompress(src, 7)));
    }

    [Fact]
    public void ExtendedMatchLength_With255Loop()
    {
        // 1 literal 'a', match length nibble 15 + (255 + 2) = 272, +4 = 276 -> 277 'a's.
        byte[] src = { 0x1F, (byte)'a', 0x01, 0x00, 0xFF, 0x02 };
        byte[] outp = Lz4.Decompress(src, 277);
        Assert.Equal(277, outp.Length);
        Assert.All(outp, b => Assert.Equal((byte)'a', b));
    }
}
