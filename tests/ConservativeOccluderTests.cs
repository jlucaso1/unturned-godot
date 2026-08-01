using System;
using System.IO;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class ConservativeOccluderTests
{
    [Fact]
    public void EveryCoarseCornerIsAtOrBelowTheWholeSourceCell()
    {
        float[,] source =
        {
            { 8, 8, 8, 8, 8 },
            { 8, 1, 8, 7, 8 },
            { 8, 8, 6, 8, 8 },
            { 8, 5, 8, 2, 8 },
            { 8, 8, 8, 8, 8 },
        };
        ConservativeOccluderGrid grid = ConservativeOccluder.Build(4, 2, (x, y) => source[x, y]);

        const int step = 2;
        for (int cx = 0; cx < 2; cx++)
            for (int cy = 0; cy < 2; cy++)
            {
                float sourceMinimum = float.MaxValue;
                for (int x = cx * step; x <= (cx + 1) * step; x++)
                    for (int y = cy * step; y <= (cy + 1) * step; y++)
                        sourceMinimum = MathF.Min(sourceMinimum, source[x, y]);

                int a = (cx * grid.VerticesPerEdge) + cy;
                Assert.True(grid.Heights[a] <= sourceMinimum);
                Assert.True(grid.Heights[a + 1] <= sourceMinimum);
                Assert.True(grid.Heights[a + grid.VerticesPerEdge] <= sourceMinimum);
                Assert.True(grid.Heights[a + grid.VerticesPerEdge + 1] <= sourceMinimum);
            }
    }

    [Fact]
    public void FlatGridPreservesHeightAndExpectedWinding()
    {
        ConservativeOccluderGrid grid = ConservativeOccluder.Build(4, 2, (_, _) => 3.5f);
        Assert.Equal(3, grid.VerticesPerEdge);
        Assert.Equal(9, grid.Heights.Length);
        Assert.All(grid.Heights, height => Assert.Equal(3.5f, height));
        Assert.Equal(24, grid.Indices.Length);
        Assert.Equal(new[] { 0, 3, 1, 1, 3, 4 }, grid.Indices[..6]);
    }

    [Fact]
    public void RejectsInvalidTopologySamplerAndHeight()
    {
        Assert.Throws<ArgumentNullException>(() => ConservativeOccluder.Build(4, 2, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConservativeOccluder.Build(0, 1, (_, _) => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConservativeOccluder.Build(4, 0, (_, _) => 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => ConservativeOccluder.Build(4, 3, (_, _) => 0));
        Assert.Throws<InvalidDataException>(() =>
            ConservativeOccluder.Build(4, 2, (x, y) => x == 1 && y == 1 ? float.NaN : 0));
    }
}
