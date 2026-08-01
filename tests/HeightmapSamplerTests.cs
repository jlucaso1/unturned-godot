using System.Collections.Generic;
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
    public void GodotTerrainSampling_MirrorsZBackToTheSourceTile()
    {
        // Source height rises along Unity +Z. The same physical point is Godot -Z after import.
        HeightmapSampler sampler = Tile(0, 0, (hx, _) => 0.2f + (0.001f * hx));
        float godotZ = -10f * Cell;

        Assert.True(TerrainCoordinates.TrySampleGodotHeight(sampler, 4f * Cell, godotZ, out float mirrored));
        Assert.True(sampler.TrySampleHeight(4f * Cell, -godotZ, out float source));
        Assert.Equal(source, mirrored, 5);
        Assert.False(sampler.TrySampleHeight(4f * Cell, godotZ, out _));
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

    private static float Cell => Landscape.TILE_SIZE / (float)Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;

    // heights[hx, hy]: hy indexes worldX, hx indexes worldZ (the transposed layout GetWorldPosition uses).
    private static HeightmapSampler Tile(int tileX, int tileY, System.Func<int, int, float> height)
    {
        var heights = new float[Res, Res];
        for (int hx = 0; hx < Res; hx++)
            for (int hy = 0; hy < Res; hy++)
                heights[hx, hy] = height(hx, hy);
        return new HeightmapSampler(new[] { HeightmapTile.FromHeights(tileX, tileY, heights) });
    }

    [Fact]
    public void SampleNormal_UniformRamp_MatchesThePlaneNormal()
    {
        // A plane rising along worldX (the hy index) has upward normal ~ (-slopeX, 1, 0). On a linear
        // surface the interpolated normal must reproduce it exactly — smoothing may not bend a flat slope.
        const float perCell = 0.001f;
        HeightmapSampler sampler = Tile(0, 0, (_, hy) => 0.2f + (perCell * hy));
        float slopeX = perCell * Landscape.TILE_HEIGHT / Cell;

        foreach (float at in new[] { 0.5f, 4.25f, 17.8f })
        {
            Assert.True(sampler.TrySampleNormal(at * Cell, at * Cell, out Godot.Vector3 normal));
            Assert.Equal(-slopeX, normal.X / normal.Y, 3); // dY/dX recovered from the normal
            Assert.Equal(0f, normal.Z, 5);
            Assert.True(normal.Y > 0f);
            Assert.Equal(1f, normal.Length(), 4);
        }
    }

    // The regression this smoothing exists for. On a curved surface the old face normal was constant
    // within each triangle and jumped at every edge; roads take this normal for a whole loft row, so the
    // jump became a hard shading seam straight across the carriageway.
    [Fact]
    public void SampleNormal_IsContinuousAcrossCellBoundaries()
    {
        // Curved in both axes, so consecutive cells genuinely have different slopes.
        HeightmapSampler sampler = Tile(0, 0, (hx, hy) => 0.3f + (0.00002f * hy * hy) + (0.00001f * hx * hx));

        // Absolute thresholds would only encode this particular curvature, so compare the shape of the
        // distribution instead: on a smooth field every step turns the normal by about the same amount,
        // while a faceted one is flat inside each triangle and dumps the whole cell-to-cell difference
        // into the single step that crosses the edge. The peak-to-mean ratio separates the two cleanly.
        const float step = 0.05f; // of a cell, so twenty samples per cell
        var turns = new List<float>();
        for (float t = 2.5f; t < 12f; t += step) // walks across ten cell boundaries
        {
            Assert.True(sampler.TrySampleNormal(t * Cell, 3.3f * Cell, out Godot.Vector3 a));
            Assert.True(sampler.TrySampleNormal((t + step) * Cell, 3.3f * Cell, out Godot.Vector3 b));
            turns.Add(Godot.Mathf.RadToDeg(a.AngleTo(b)));
        }

        float mean = 0f;
        foreach (float turn in turns)
            mean += turn;
        mean /= turns.Count;
        float worst = 0f;
        foreach (float turn in turns)
            worst = System.Math.Max(worst, turn);

        Assert.True(mean > 0f, "the test surface is not curved, so this proves nothing");
        // Face normals put ~20x the mean into the boundary step; smoothed ones stay near 1x.
        Assert.True(worst < 3f * mean,
            $"normal turns unevenly: worst step {worst:0.####} deg vs mean {mean:0.####} deg");
    }

    [Fact]
    public void SampleNormal_IsContinuousAcrossTileBoundaries()
    {
        // Two tiles side by side along worldX, sharing their edge (tile 0's hy=256 is tile 1's hy=0) and
        // carrying one continuous curved surface. Without stepping into the neighbour to difference, the
        // seam would show the same hard shading break, just at the tile edge.
        const int Last = Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;
        var a = new float[Res, Res];
        var b = new float[Res, Res];
        for (int hx = 0; hx < Res; hx++)
            for (int hy = 0; hy < Res; hy++)
            {
                a[hx, hy] = 0.3f + (0.000002f * hy * hy);
                b[hx, hy] = 0.3f + (0.000002f * (hy + Last) * (hy + Last)); // continues the same curve
            }

        var sampler = new HeightmapSampler(new[]
        {
            HeightmapTile.FromHeights(0, 0, a),
            HeightmapTile.FromHeights(1, 0, b),
        });

        float seam = Landscape.TILE_SIZE;
        Assert.True(sampler.TrySampleNormal(seam - (0.02f * Cell), 500f, out Godot.Vector3 before));
        Assert.True(sampler.TrySampleNormal(seam + (0.02f * Cell), 500f, out Godot.Vector3 after));

        float degrees = Godot.Mathf.RadToDeg(before.AngleTo(after));
        Assert.True(degrees < 0.05f, $"normal jumped {degrees:0.###} deg across the tile seam");
    }

    [Fact]
    public void SampleNormal_AtTheEdgeOfTheLoadedWorld_StillMatchesTheSlope()
    {
        // The outermost grid line has no neighbour to difference against, so that side folds back to a
        // one-sided difference. On a ramp that is still the exact slope, not a flattened one.
        const float perCell = 0.001f;
        HeightmapSampler sampler = Tile(0, 0, (_, hy) => 0.2f + (perCell * hy));
        float slopeX = perCell * Landscape.TILE_HEIGHT / Cell;

        Assert.True(sampler.TrySampleNormal(0f, 0f, out Godot.Vector3 normal)); // corner of the tile
        Assert.Equal(-slopeX, normal.X / normal.Y, 3);
    }

    [Fact]
    public void SampleNormal_AtTheTileEdges_UsesOneSidedSlopes()
    {
        // Tile (0,0) spans world [0, TILE_SIZE] on both axes, so its far corner is the last grid point of
        // the only loaded tile: both neighbours there fall outside the world and each axis falls back to a
        // one-sided difference. The normal must still be unit length and lean the same way as inside.
        var heights = new float[Res, Res];
        for (int x = 0; x < Res; x++)
            for (int y = 0; y < Res; y++)
                heights[x, y] = 0.2f + (0.001f * x);
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });

        // Just inside the last cell: sampling exactly at TILE_SIZE lands on the (absent) next tile.
        const float far = Landscape.TILE_SIZE - 0.5f;
        foreach ((float x, float z) in new[] { (0f, 0f), (far, far), (far, 0f), (0f, far) })
        {
            Assert.True(sampler.TrySampleNormal(x, z, out Godot.Vector3 corner), $"no normal at {x},{z}");
            Assert.True(corner.Y > 0f);
            Assert.Equal(1f, corner.Length(), 3);
        }

        Assert.True(sampler.TrySampleNormal(far / 2f, far / 2f, out Godot.Vector3 middle));
        Assert.True(sampler.TrySampleNormal(far / 2f, far, out Godot.Vector3 atEdge));
        Assert.True(middle.Z * atEdge.Z >= 0f); // same tilt direction, not mirrored by the fallback
    }

}
