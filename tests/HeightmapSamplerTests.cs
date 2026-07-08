using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class HeightmapSamplerTests
{
    private const int Res = Landscape.HEIGHTMAP_RESOLUTION;

    private static HeightmapSampler Flat(float h)
    {
        var heights = new float[Res, Res];
        for (int x = 0; x < Res; x++)
            for (int y = 0; y < Res; y++)
                heights[x, y] = h;
        return new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });
    }

    [Fact]
    public void SampleHeight_MatchesGetWorldPosition_AtGridPoints()
    {
        var heights = new float[Res, Res];
        heights[0, 0] = 0.5f;
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });

        // Grid point (hx=0, hy=0) -> world (0,0). Its Y must equal GetWorldPosition's.
        float expected = Landscape.GetWorldPosition(0, 0, 0, 0, 0.5f).Y;
        Assert.True(sampler.TrySampleHeight(0f, 0f, out float y));
        Assert.Equal(expected, y, 3);
    }

    [Fact]
    public void SampleHeight_FlatTile_ReturnsConstant()
    {
        HeightmapSampler sampler = Flat(0.5f);
        float expected = (-Landscape.TILE_HEIGHT / 2f) + (0.5f * Landscape.TILE_HEIGHT); // = 0
        Assert.True(sampler.TrySampleHeight(123.4f, 678.9f, out float y));
        Assert.Equal(expected, y, 3);
    }

    [Fact]
    public void SampleHeight_LinearAcrossAnAxisRamp()
    {
        // A ramp along worldX (hy index): planar, so the triangulation reproduces the exact midpoint.
        var heights = new float[Res, Res];
        for (int x = 0; x < Res; x++)
        {
            heights[x, 0] = 0.2f;
            heights[x, 1] = 0.6f;
        }
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });

        float halfCell = Landscape.TILE_SIZE / (Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE * 2f); // worldX of hy=0.5
        Assert.True(sampler.TrySampleHeight(halfCell, 0f, out float y));
        float expected = (-Landscape.TILE_HEIGHT / 2f) + (0.4f * Landscape.TILE_HEIGHT); // midway 0.2..0.6
        Assert.Equal(expected, y, 2);
    }

    [Fact]
    public void SampleHeight_UsesTheMeshTriangle_NotBilinear()
    {
        // A saddle cell (only h10 raised) is non-planar, so the triangulation and bilinear disagree. A point
        // in the tx>=ty triangle (h00,h10,h11) must follow that plane, matching the rendered mesh.
        var heights = new float[Res, Res];
        heights[0, 0] = 0.5f;
        heights[1, 0] = 0.7f; // h10 (index [hx=1, hy=0])
        heights[0, 1] = 0.5f;
        heights[1, 1] = 0.5f;
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });

        float cell = Landscape.TILE_SIZE / Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;
        // tx = 0.6 (worldZ), ty = 0.4 (worldX) -> in the (h00,h10,h11) triangle.
        Assert.True(sampler.TrySampleHeight(0.4f * cell, 0.6f * cell, out float y));
        float triangle = 0.5f + (0.6f * (0.7f - 0.5f)) + (0.4f * (0.5f - 0.7f)); // = 0.54
        float bilinear = 0.572f; // lerp(lerp(.5,.7,.6), .5, .4)
        float expected = (-Landscape.TILE_HEIGHT / 2f) + (triangle * Landscape.TILE_HEIGHT);
        Assert.Equal(expected, y, 1);
        Assert.NotEqual((-Landscape.TILE_HEIGHT / 2f) + (bilinear * Landscape.TILE_HEIGHT), y, 1);
    }

    [Fact]
    public void SampleHeight_MissingTile_ReturnsFalse()
    {
        HeightmapSampler sampler = Flat(0.5f);
        Assert.False(sampler.TrySampleHeight(-5f, -5f, out _)); // tile (-1,-1) not loaded
    }
}
