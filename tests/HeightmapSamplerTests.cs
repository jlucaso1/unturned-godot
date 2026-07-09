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

    [Fact]
    public void SampleNormal_FlatTile_PointsStraightUp()
    {
        HeightmapSampler sampler = Flat(0.5f);
        Assert.True(sampler.TrySampleNormal(123.4f, 678.9f, out Godot.Vector3 normal));
        Assert.Equal(0f, normal.X, 5);
        Assert.Equal(1f, normal.Y, 5);
        Assert.Equal(0f, normal.Z, 5);
    }

    [Fact]
    public void SampleNormal_MissingTile_ReturnsFalseAndUp()
    {
        HeightmapSampler sampler = Flat(0.5f);
        Assert.False(sampler.TrySampleNormal(-5f, -5f, out Godot.Vector3 normal));
        Assert.Equal(Godot.Vector3.Up, normal);
    }

    [Fact]
    public void SampleNormal_RampAlongX_TiltsAgainstTheSlope()
    {
        // Same ramp as the height test: rising along worldX (hy index). The plane y = slopeX * x has
        // upward normal ~ (-slopeX, 1, 0).
        var heights = new float[Res, Res];
        for (int x = 0; x < Res; x++)
        {
            heights[x, 0] = 0.2f;
            heights[x, 1] = 0.6f;
        }
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });

        float cell = Landscape.TILE_SIZE / (float)Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;
        float slopeX = (0.6f - 0.2f) * Landscape.TILE_HEIGHT / cell;
        // ty = 0.5 > tx = 0 -> the (h00, h01, h11) triangle.
        Assert.True(sampler.TrySampleNormal(0.5f * cell, 0f, out Godot.Vector3 normal));
        Assert.Equal(-slopeX, normal.X / normal.Y, 3); // dY/dX recovered from the normal
        Assert.Equal(0f, normal.Z, 5);
        Assert.True(normal.Y > 0f);
    }

    [Fact]
    public void SampleNormal_MatchesTheHeightTriangle_NotBilinear()
    {
        // The saddle cell again: in the tx >= ty triangle (h00, h10, h11) the plane's slopes come from
        // h10 - h00 (along Z) and h11 - h10 (along X); the normal must be that plane's, i.e. consistent
        // with what TrySampleHeight returns across the same triangle.
        var heights = new float[Res, Res];
        heights[0, 0] = 0.5f;
        heights[1, 0] = 0.7f;
        heights[0, 1] = 0.5f;
        heights[1, 1] = 0.5f;
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });

        float cell = Landscape.TILE_SIZE / (float)Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;
        Assert.True(sampler.TrySampleNormal(0.4f * cell, 0.6f * cell, out Godot.Vector3 normal));

        // Numerical slopes from the height sampler inside the same triangle (points near tx=0.6, ty=0.4).
        Assert.True(sampler.TrySampleHeight(0.40f * cell, 0.60f * cell, out float y0));
        Assert.True(sampler.TrySampleHeight(0.42f * cell, 0.60f * cell, out float yx));
        Assert.True(sampler.TrySampleHeight(0.40f * cell, 0.62f * cell, out float yz));
        float slopeX = (yx - y0) / (0.02f * cell);
        float slopeZ = (yz - y0) / (0.02f * cell);

        Assert.Equal(-slopeX, normal.X / normal.Y, 2);
        Assert.Equal(-slopeZ, normal.Z / normal.Y, 2);
    }
}
