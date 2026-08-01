using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class NavmeshPartitionTests
{
    private static NavFlag Strip(int quads)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int x = 0; x <= quads; x++)
        {
            vertices.Add(new Vector3(x, 0, 0));
            vertices.Add(new Vector3(x, 0, 1));
        }
        for (int x = 0; x < quads; x++)
        {
            int a = x * 2, b = a + 1, c = a + 2, d = a + 3;
            triangles.AddRange(new[] { a, b, c, b, d, c });
        }
        return new NavFlag { Vertices = vertices.ToArray(), Triangles = triangles.ToArray() };
    }

    [Fact]
    public void RegionsAreSpatialAndBounded()
    {
        List<NavmeshRegionData> regions = NavmeshPartition.Build(
            Strip(12), cellSize: 4f, maxTriangles: 3);

        Assert.True(regions.Count > 3);
        Assert.Equal(24, SumTriangles(regions));
        Assert.All(regions, region => Assert.InRange(region.SourceTriangleCount, 1, 3));
        Assert.All(regions, region =>
        {
            Assert.Equal(region.SourceTriangleCount * 3, region.Triangles.Length);
            Assert.All(region.Triangles, index => Assert.InRange(index, 0, region.Vertices.Length - 1));
        });
    }

    [Fact]
    public void SkippedSourceTrianglesAreNotPublished()
    {
        var skipped = new HashSet<int> { 0, 3, 8 };
        List<NavmeshRegionData> regions = NavmeshPartition.Build(
            Strip(6), skipped, cellSize: 2f, maxTriangles: 4);

        Assert.Equal(12 - skipped.Count, SumTriangles(regions));
    }

    [Fact]
    public void EmptyFlagProducesNoRegions()
    {
        Assert.Empty(NavmeshPartition.Build(new NavFlag()));
    }

    [Theory]
    [InlineData(0f, 10)]
    [InlineData(-1f, 10)]
    [InlineData(10f, 0)]
    [InlineData(10f, -1)]
    public void InvalidLimitsAreRejected(float cellSize, int maxTriangles)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NavmeshPartition.Build(Strip(1), cellSize: cellSize, maxTriangles: maxTriangles));
    }

    private static int SumTriangles(IEnumerable<NavmeshRegionData> regions)
    {
        int total = 0;
        foreach (NavmeshRegionData region in regions)
            total += region.SourceTriangleCount;
        return total;
    }
}
