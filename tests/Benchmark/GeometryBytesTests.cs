using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests;

public class GeometryBytesTests
{
    [Fact]
    public void EstimateSumsVerticesAndIndices() =>
        Assert.Equal(100 * 24 + 300 * 4, GeometryBytes.Estimate(100, 300));

    [Fact]
    public void IndexingIsANetWinForTheTerrainCase()
    {
        // 16 tiles: non-indexed = 6,291,456 verts / 0 indices; indexed = 1,056,784 verts / 6,291,456 indices.
        long before = GeometryBytes.Estimate(6_291_456, 0);
        long after = GeometryBytes.Estimate(1_056_784, 6_291_456);
        Assert.True(after < before, $"expected indexing to shrink geometry bytes ({after} vs {before})");
    }
}
