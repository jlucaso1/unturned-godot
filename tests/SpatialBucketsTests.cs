using System;
using System.Linq;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class SpatialBucketsTests
{
    [Fact]
    public void Add_PreservesCellAndInsertionOrderIncludingNegativeCoordinates()
    {
        var buckets = new SpatialBuckets<string>(10f);
        buckets.Add(1, 9, "a");
        buckets.Add(-0.01f, -10f, "negative");
        buckets.Add(9, 2, "b");

        Assert.Equal(3, buckets.Count);
        Assert.Equal(new[] { "a", "b" }, buckets.Groups[(0, 0)]);
        Assert.Equal(new[] { "negative" }, buckets.Groups[(-1, -1)]);
        Assert.Equal(new[] { "a", "b", "negative" }, buckets.Flatten());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Constructor_RejectsInvalidCellSizes(float size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpatialBuckets<int>(size));
}
