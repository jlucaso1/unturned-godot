using System;
using System.Collections.Generic;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class PackedBitVectorTests
{
    // Packs values of `bitSize` bits back to back, least significant bit first, the way Unity writes them.
    private static byte[] Pack(int bitSize, params uint[] values)
    {
        var bytes = new byte[((values.Length * bitSize) + 7) / 8];
        int bitPos = 0;
        foreach (uint value in values)
            for (int bit = 0; bit < bitSize; bit++, bitPos++)
                if (((value >> bit) & 1) != 0)
                    bytes[bitPos / 8] |= (byte)(1 << (bitPos % 8));

        return bytes;
    }

    private static Dictionary<string, object> Node(int numItems, int bitSize, byte[] data,
        float range = 0f, float start = 0f) => new()
        {
            ["m_NumItems"] = numItems,
            ["m_Range"] = range,
            ["m_Start"] = start,
            ["m_Data"] = data,
            ["m_BitSize"] = bitSize,
        };

    [Fact]
    public void UnpackUInts_ReadsValuesThatStraddleByteBoundaries()
    {
        // 5 bits per item: the second value starts mid-byte and the third spans two bytes.
        uint[] values = { 0, 31, 17, 8, 25 };
        PackedBitVector vector = PackedBitVector.Read(Node(values.Length, 5, Pack(5, values)));

        Assert.Equal(values, vector.UnpackUInts());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void UnpackUInts_RoundTripsEveryBitWidth(int bitSize)
    {
        uint max = bitSize >= 32 ? uint.MaxValue : (1u << bitSize) - 1u;
        uint[] values = { 0, max, max / 3, 1, max / 2 };

        PackedBitVector vector = PackedBitVector.Read(Node(values.Length, bitSize, Pack(bitSize, values)));

        Assert.Equal(values, vector.UnpackUInts());
    }

    [Fact]
    public void UnpackUInts_HonoursStartAndCount()
    {
        uint[] values = { 3, 1, 4, 1, 5, 9 };
        PackedBitVector vector = PackedBitVector.Read(Node(values.Length, 4, Pack(4, values)));

        Assert.Equal(new uint[] { 4, 1, 5 }, vector.UnpackUInts(count: 3, start: 2));
        Assert.Equal(new uint[] { 9 }, vector.UnpackUInts(count: 1, start: 5));
    }

    [Fact]
    public void UnpackFloats_MapsOntoTheQuantizationRange()
    {
        // 8-bit quantization of [10, 14]: 0 -> 10, 255 -> 14, 128 -> just past the middle.
        uint[] values = { 0, 255, 128 };
        PackedBitVector vector = PackedBitVector.Read(
            Node(values.Length, 8, Pack(8, values), range: 4f, start: 10f));

        float[] floats = vector.UnpackFloats();

        Assert.Equal(10f, floats[0], 5);
        Assert.Equal(14f, floats[1], 5);
        Assert.Equal(10f + (4f * 128f / 255f), floats[2], 5);
    }

    [Fact]
    public void UnpackFloats_WithoutARange_IsFlat()
    {
        // A range of zero means every item quantized to the same value; nothing to spread out.
        PackedBitVector vector = PackedBitVector.Read(Node(2, 8, Pack(8, 1, 200), range: 0f, start: 7f));

        Assert.Equal(new[] { 7f, 7f }, vector.UnpackFloats());
    }

    [Fact]
    public void Read_MissingOrEmptyVector_UnpacksToNothing()
    {
        Assert.True(PackedBitVector.Read(null).IsEmpty);
        Assert.True(PackedBitVector.Read("not a node").IsEmpty);
        Assert.True(PackedBitVector.Read(new Dictionary<string, object>()).IsEmpty);
        Assert.Empty(PackedBitVector.Read(Node(0, 8, Array.Empty<byte>())).UnpackUInts());
        Assert.Empty(PackedBitVector.Read(Node(4, 0, new byte[] { 1, 2 })).UnpackFloats());
    }

    [Fact]
    public void Unpack_OutOfRangeRequests_YieldNothing()
    {
        PackedBitVector vector = PackedBitVector.Read(Node(4, 8, Pack(8, 1, 2, 3, 4)));

        Assert.Empty(vector.UnpackUInts(count: 5));            // more than the vector holds
        Assert.Empty(vector.UnpackUInts(count: 2, start: 3));  // runs past the end
        Assert.Empty(vector.UnpackUInts(count: 1, start: -1));
        Assert.Empty(vector.UnpackUInts(count: 0));
    }

    [Fact]
    public void Unpack_TruncatedData_YieldsNothingRatherThanReadingPastTheBuffer()
    {
        // The header claims four 8-bit items but only two bytes were written.
        PackedBitVector vector = PackedBitVector.Read(Node(4, 8, new byte[] { 1, 2 }));

        Assert.Empty(vector.UnpackUInts());
    }

    [Fact]
    public void UnpackFloats_AtTheWidestBitSize_SpansTheWholeRange()
    {
        // 32-bit quantization: the denominator is uint.MaxValue rather than a shifted literal.
        PackedBitVector vector = PackedBitVector.Read(
            Node(2, 32, Pack(32, 0u, uint.MaxValue), range: 8f, start: -4f));

        float[] floats = vector.UnpackFloats();

        Assert.Equal(-4f, floats[0], 4);
        Assert.Equal(4f, floats[1], 4);
    }

    [Theory]
    [InlineData(33)]
    [InlineData(64)]
    public void BitSizeWiderThanAUInt_UnpacksToNothingRatherThanFoldingBits(int bitSize)
    {
        // The accumulator is a uint, and C# masks a shift count on a 32-bit operand to & 31 — so bit 32
        // would have folded onto bit 0 and every item come out as a plausible wrong number. UnpackFloats
        // then denormalized that against uint.MaxValue, so a m_CompressedMesh claiming this width decoded
        // to geometry that looked like data and was not.
        var data = new byte[bitSize]; // comfortably more than the header's items need
        Array.Fill(data, (byte)0xFF);
        PackedBitVector vector = PackedBitVector.Read(Node(4, bitSize, data, range: 1f));

        Assert.True(vector.IsEmpty);
        Assert.Empty(vector.UnpackUInts());
        Assert.Empty(vector.UnpackFloats());
    }

    [Fact]
    public void Read_DataOfTheWrongType_IsEmpty()
    {
        var node = new Dictionary<string, object>
        {
            ["m_NumItems"] = 4,
            ["m_BitSize"] = 8,
            ["m_Data"] = "not bytes",
        };

        Assert.True(PackedBitVector.Read(node).IsEmpty);
    }

    [Fact]
    public void Read_KeepsTheHeaderFields()
    {
        PackedBitVector vector = PackedBitVector.Read(Node(3, 6, new byte[] { 1, 2, 3 }, 2.5f, -1f));

        Assert.Equal(3, vector.NumItems);
        Assert.Equal(6, vector.BitSize);
        Assert.Equal(2.5f, vector.Range);
        Assert.Equal(-1f, vector.Start);
        Assert.False(vector.IsEmpty);
    }
}
