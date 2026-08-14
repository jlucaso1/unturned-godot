using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class TerrainHeightfieldTests
{
    private const int Res = Landscape.HEIGHTMAP_RESOLUTION;
    private const float Center = (Res - 1) / 2f;

    private static float[,] SampleHeights()
    {
        var heights = new float[Res, Res];
        for (int x = 0; x < Res; x++)
            for (int y = 0; y < Res; y++)
                heights[x, y] = (((x * 7) + (y * 13)) % 100) / 100f; // varied, deterministic
        return heights;
    }

    // Every heightfield grid sample lands exactly where Landscape's own placement maths puts that
    // heightmap index. Encodes Godot's HeightMapShape3D layout (sample (w, d) sits at local
    // (w-center, MapData[d*width+w], d-center)) and checks it against Landscape.GetWorldPosition for a
    // full 257x257 grid across several tiles.
    //
    // This is a check on the PLACEMENT ALGEBRA — the transposition, the reversed depth axis, the centring
    // — and nothing else. It used to be named and commented as though it proved the collider reproduced
    // the render mesh, and it did not: it compares the heightfield against the ideal surface both are
    // supposed to describe, never calling TerrainBuilder at all, so it passed just as happily during the
    // period when the mesh was actually built at every SECOND index and the two surfaces were metres
    // apart on a ridge. Whether the drawn tile agrees can only be asked of a built tile, which needs an
    // engine: TerrainBuilderTests.TheDrawnSurfaceIsTheSurfaceTheSamplerReports does it there, against
    // BuildTileMesh's own vertex array.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 3)]
    [InlineData(-1, 4)]
    public void Placement_ReproducesLandscapeWorldPositions(int tileX, int tileY)
    {
        float[,] heights = SampleHeights();
        float[] mapData = TerrainHeightfield.MapData(heights);
        Transform3D xform = TerrainHeightfield.CollisionTransform(tileX, tileY);

        for (int hx = 0; hx < Res; hx++)
            for (int hy = 0; hy < Res; hy++)
            {
                int w = hy, d = Res - 1 - hx;
                var local = new Vector3(w - Center, mapData[(d * Res) + w], d - Center);
                Vector3 collision = xform * local;
                Vector3 render = Landscape.UnityToGodot(
                    Landscape.GetWorldPosition(tileX, tileY, hx, hy, heights[hx, hy]));
                Assert.Equal(render.X, collision.X, 0.001f);
                Assert.Equal(render.Y, collision.Y, 0.001f);
                Assert.Equal(render.Z, collision.Z, 0.001f);
            }
    }

    // The collider and the sampler have to be reading the same grid, at the same resolution. Both are
    // asked for the height directly over each heightfield sample, and a sampler that walked a coarser (or
    // finer) grid than the collider would answer with an interpolation between neighbours instead of the
    // sample itself — which is exactly what the subsampled render mesh was doing to the drawn surface.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 4)]
    public void TheSamplerAndTheHeightfieldReadTheSameGrid(int tileX, int tileY)
    {
        float[,] heights = SampleHeights();
        float[] mapData = TerrainHeightfield.MapData(heights);
        Transform3D xform = TerrainHeightfield.CollisionTransform(tileX, tileY);
        var sampler = new HeightmapSampler(new[] { HeightmapTile.FromHeights(tileX, tileY, heights) });

        // Index 256 on either axis is the row this tile shares with its neighbour: it lands exactly on the
        // seam, where the sampler resolves to the NEXT tile — which is right, and is not loaded here. The
        // 0..255 corners cover every cell this tile owns.
        for (int hx = 0; hx < Res - 1; hx++)
            for (int hy = 0; hy < Res - 1; hy++)
            {
                int w = hy, d = Res - 1 - hx;
                Vector3 collision = xform * new Vector3(w - Center, mapData[(d * Res) + w], d - Center);
                // The sampler works in Unity's +Z, which is the collider's -Z.
                Assert.True(sampler.TrySampleHeight(collision.X, -collision.Z, out float sampled));
                Assert.Equal(collision.Y, sampled, 0.001f);
            }
    }

    [Fact]
    public void MapData_StoresWorldHeights_WithDepthReversed()
    {
        var heights = new float[Res, Res];
        heights[0, 0] = 0f; // normalized min -> world -TILE_HEIGHT/2
        heights[1, 2] = 1f; // normalized max -> world +TILE_HEIGHT/2
        float[] data = TerrainHeightfield.MapData(heights);

        // hx=0 lands at depth res-1 (reversed); hx=1 at depth res-2.
        Assert.Equal(-Landscape.TILE_HEIGHT / 2f, data[((Res - 1) * Res) + 0], 0.001f);
        Assert.Equal(Landscape.TILE_HEIGHT / 2f, data[((Res - 2) * Res) + 2], 0.001f);
    }

    [Fact]
    public void CompactMapData_MatchesFloatConversionWithoutIntermediateGrids()
    {
        var raw = new ushort[Res * Res];
        raw[0] = 0;
        raw[(1 * Res) + 2] = ushort.MaxValue;
        raw[(8 * Res) + 9] = 0x8123;

        float[] compact = TerrainHeightfield.MapData(raw);

        Assert.Equal(-Landscape.TILE_HEIGHT / 2f, compact[(Res - 1) * Res], 0.001f);
        Assert.Equal(Landscape.TILE_HEIGHT / 2f, compact[((Res - 2) * Res) + 2], 0.001f);
        Assert.Equal((-Landscape.TILE_HEIGHT / 2f)
            + ((0x8123 / (float)ushort.MaxValue) * Landscape.TILE_HEIGHT),
            compact[((Res - 1 - 8) * Res) + 9], 0.001f);
        Assert.Throws<System.ArgumentException>(() => TerrainHeightfield.MapData(new ushort[3]));
    }

    [Fact]
    public void CellSize_IsFourMetres() => Assert.Equal(4f, TerrainHeightfield.CellSize, 0.0001f);
}
