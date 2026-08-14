using System;
using System.IO;
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

    // Malformed blocks. These matter more than their size suggests: LZ4 is the codec UnityFS uses for the
    // blocks-info header, so this loop runs over bytes straight off disk before anything about the bundle
    // has been checked. Each case below used to read or write outside one of the two buffers and fault
    // with whatever the runtime raised from the bad access; they now fail as InvalidDataException, which
    // is a type the bundle walkers already contain.

    [Fact]
    public void LiteralRunPastTheInput_Throws()
    {
        // token 0x50 = 5 literals, but only two bytes follow.
        byte[] src = { 0x50, (byte)'A', (byte)'B' };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 5));
    }

    [Fact]
    public void LiteralRunPastTheOutput_Throws()
    {
        // Three literals are present, but the block claims to decode to two bytes.
        byte[] src = { 0x30, (byte)'A', (byte)'B', (byte)'C' };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 2));
    }

    [Fact]
    public void TruncatedMatchOffset_Throws()
    {
        // 1 literal, then a single byte where a two-byte offset belongs. The old `sp < src.Length` loop
        // condition let this through and read src[sp + 1] one past the end of the block.
        byte[] src = { 0x10, (byte)'a', 0x01 };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 8));
    }

    [Fact]
    public void MatchOffsetBeyondWhatWasProduced_Throws()
    {
        // 1 literal then offset 5: matchPos = 1 - 5 = -4, which indexed before the output buffer.
        byte[] src = { 0x12, (byte)'a', 0x05, 0x00 };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 16));
    }

    [Fact]
    public void ZeroMatchOffset_Throws()
    {
        // Offset 0 is no distance at all; the copy would read the byte it is writing.
        byte[] src = { 0x12, (byte)'a', 0x00, 0x00 };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 16));
    }

    [Fact]
    public void MatchPastTheOutput_Throws()
    {
        // 1 literal + a 6-byte match = 7 bytes, into a block that claims to decode to 4.
        byte[] src = { 0x12, (byte)'a', 0x01, 0x00 };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 4));
    }

    [Fact]
    public void ExtendedLengthWithNoTerminator_Throws()
    {
        // Literal length 15 promises an extension byte that the block does not carry, so the do/while
        // walked off the end of the span.
        byte[] src = { 0xF0 };
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 32));
    }

    [Fact]
    public void ExtendedLengthThatOverflows_Throws()
    {
        // A long enough run of 0xFF sums past int.MaxValue. Accumulated in int that wrapped negative, and
        // a negative length passes any naive "longer than the buffer" test.
        var src = new byte[(int.MaxValue / 255) + 2];
        Array.Fill(src, (byte)0xFF);
        src[0] = 0xF0; // literal length 15, then the 0xFF run

        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(src, 32));
    }

    [Fact]
    public void NegativeOutputSize_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Lz4.Decompress(new byte[] { 0x00 }, -1));
    }
}
