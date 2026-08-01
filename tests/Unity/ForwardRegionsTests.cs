using System;
using System.Collections.Generic;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

public class ForwardRegionsTests
{
    // A forward-only reader over `data`, plus the count of bytes it was asked for (a bundle's LZMA block
    // costs the same whether the bytes are kept or skipped, so that count is what the caller pays).
    private sealed class Reader
    {
        private readonly byte[] _data;
        public int Position { get; private set; }

        public Reader(byte[] data) => _data = data;

        public int Read(byte[] destination, int offset, int count)
        {
            int available = Math.Min(count, _data.Length - Position);
            if (available <= 0)
                return 0;

            Buffer.BlockCopy(_data, Position, destination, offset, available);
            Position += available;
            return available;
        }
    }

    private static byte[] Ramp(int length)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++)
            data[i] = (byte)i;
        return data;
    }

    private static Dictionary<int, byte[]> ReadRegions(Reader reader, long nodeSize,
        params ForwardRegions.Region[] regions)
    {
        var read = new Dictionary<int, byte[]>();
        ForwardRegions.Read(reader.Read, nodeSize, regions, (index, bytes) => read[index] = bytes,
            scratchSize: 8);
        return read;
    }

    [Fact]
    public void Read_TakesEachRegionAndDrainsTheRest()
    {
        var reader = new Reader(Ramp(64));
        Dictionary<int, byte[]> read = ReadRegions(reader, 64,
            new ForwardRegions.Region(8, 4, 0),
            new ForwardRegions.Region(40, 2, 1));

        Assert.Equal(new byte[] { 8, 9, 10, 11 }, read[0]);
        Assert.Equal(new byte[] { 40, 41 }, read[1]);
        Assert.Equal(64, reader.Position); // the node was drained, so the next one starts aligned
    }

    [Fact]
    public void Read_VisitsRegionsInOffsetOrder_WhateverOrderTheyCameIn()
    {
        var reader = new Reader(Ramp(32));
        var order = new List<int>();
        ForwardRegions.Read(reader.Read, 32, new[]
        {
            new ForwardRegions.Region(20, 2, 0),
            new ForwardRegions.Region(4, 2, 1),
            new ForwardRegions.Region(12, 2, 2),
        }, (index, _) => order.Add(index));

        Assert.Equal(new[] { 1, 2, 0 }, order);
    }

    [Fact]
    public void Read_OverlappingRegions_ReuseTheBytesAlreadyRead()
    {
        // The stream cannot go back, so a region that starts inside the previous one is served from that
        // one's buffer. Content-identical textures really do share their pixels.
        var reader = new Reader(Ramp(32));
        Dictionary<int, byte[]> read = ReadRegions(reader, 32,
            new ForwardRegions.Region(4, 8, 0),
            new ForwardRegions.Region(8, 8, 1),   // starts inside the first, ends after it
            new ForwardRegions.Region(10, 2, 2)); // entirely inside the second

        Assert.Equal(new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 }, read[0]);
        Assert.Equal(new byte[] { 8, 9, 10, 11, 12, 13, 14, 15 }, read[1]);
        Assert.Equal(new byte[] { 10, 11 }, read[2]);
    }

    [Fact]
    public void Read_RangeNestedInsideAnother_DoesNotLoseTheOuterCoverage()
    {
        // [4,12) is read whole, [6,8) sits inside it, and [10,12) then needs the tail of the FIRST one.
        // Keeping only the most recent buffer threw those bytes away and the last range came back with
        // whatever the stream had moved on to.
        var reader = new Reader(Ramp(32));
        Dictionary<int, byte[]> read = ReadRegions(reader, 32,
            new ForwardRegions.Region(4, 8, 0),
            new ForwardRegions.Region(6, 2, 1),
            new ForwardRegions.Region(10, 2, 2));

        Assert.Equal(new byte[] { 4, 5, 6, 7, 8, 9, 10, 11 }, read[0]);
        Assert.Equal(new byte[] { 6, 7 }, read[1]);
        Assert.Equal(new byte[] { 10, 11 }, read[2]);
    }

    [Fact]
    public void Read_ChainOfNestedRanges_AllComeFromTheOutermost()
    {
        var reader = new Reader(Ramp(64));
        Dictionary<int, byte[]> read = ReadRegions(reader, 64,
            new ForwardRegions.Region(0, 16, 0),
            new ForwardRegions.Region(2, 2, 1),
            new ForwardRegions.Region(4, 2, 2),
            new ForwardRegions.Region(14, 2, 3),
            new ForwardRegions.Region(16, 2, 4)); // just past the buffered range: read from the stream

        Assert.Equal(new byte[] { 2, 3 }, read[1]);
        Assert.Equal(new byte[] { 4, 5 }, read[2]);
        Assert.Equal(new byte[] { 14, 15 }, read[3]);
        Assert.Equal(new byte[] { 16, 17 }, read[4]);
    }

    [Fact]
    public void Read_StopsAtTheGivenSize_LeavingTheRestOfTheStream()
    {
        // A caller that owes nothing beyond its last region passes that end as the size, and the bytes
        // after it are never decoded. That is the whole point: decoding is the cost.
        var reader = new Reader(Ramp(1024));
        Dictionary<int, byte[]> read = ReadRegions(reader, 12, new ForwardRegions.Region(8, 4, 0));

        Assert.Equal(new byte[] { 8, 9, 10, 11 }, read[0]);
        Assert.Equal(12, reader.Position);
    }

    [Fact]
    public void Read_RegionOutsideTheNode_IsSkipped()
    {
        var reader = new Reader(Ramp(16));
        Dictionary<int, byte[]> read = ReadRegions(reader, 16,
            new ForwardRegions.Region(12, 8, 0),  // runs past the end
            new ForwardRegions.Region(-1, 2, 1),  // before the start
            new ForwardRegions.Region(4, -2, 2),  // negative length
            new ForwardRegions.Region(4, 2, 3));

        Assert.Equal(new[] { 3 }, new List<int>(read.Keys));
        Assert.Equal(new byte[] { 4, 5 }, read[3]);
    }

    [Fact]
    public void Read_StreamThatEndsEarly_StopsWithoutEmittingAPartialRegion()
    {
        // Truncated input: the region it stopped inside is not emitted, and neither is anything after it.
        var reader = new Reader(Ramp(10));
        Dictionary<int, byte[]> read = ReadRegions(reader, 32,
            new ForwardRegions.Region(2, 2, 0),
            new ForwardRegions.Region(8, 8, 1),
            new ForwardRegions.Region(20, 2, 2));

        Assert.Equal(new byte[] { 2, 3 }, read[0]);
        Assert.False(read.ContainsKey(1));
        Assert.False(read.ContainsKey(2));
    }

    [Fact]
    public void Read_StreamThatEndsInAGap_Stops()
    {
        var reader = new Reader(Ramp(6));
        Dictionary<int, byte[]> read = ReadRegions(reader, 32, new ForwardRegions.Region(16, 2, 0));
        Assert.Empty(read);
    }

    [Fact]
    public void Read_NoRegions_JustDrainsTheNode()
    {
        var reader = new Reader(Ramp(20));
        Dictionary<int, byte[]> read = ReadRegions(reader, 20);

        Assert.Empty(read);
        Assert.Equal(20, reader.Position);
    }

    [Fact]
    public void Read_DrainThatEndsEarly_Stops()
    {
        var reader = new Reader(Ramp(4));
        ForwardRegions.Read(reader.Read, 64, Array.Empty<ForwardRegions.Region>(), (_, _) => { });
        Assert.Equal(4, reader.Position);
    }
}
