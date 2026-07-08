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
    public void SampleHeight_Bilinear_InterpolatesBetweenSamples()
    {
        // A ramp along worldX (hy index): height rises from 0 at hy=0 to full at hy=256. Midway across the
        // first cell (worldX = half a cell), the sampled height is half the two corner heights.
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
    public void SampleHeight_MissingTile_ReturnsFalse()
    {
        HeightmapSampler sampler = Flat(0.5f);
        Assert.False(sampler.TrySampleHeight(-5f, -5f, out _)); // tile (-1,-1) not loaded
    }
}
