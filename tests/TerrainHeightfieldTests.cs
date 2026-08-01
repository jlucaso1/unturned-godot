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

    // The whole point: every heightfield grid sample must land exactly on the render mesh vertex, so the
    // player walks on the same surface the terrain is drawn at. Encodes Godot's HeightMapShape3D layout
    // (sample (w, d) sits at local (w-center, MapData[d*width+w], d-center)) and checks it against the
    // independent Landscape render math for a full 257x257 grid across several tiles.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 3)]
    [InlineData(-1, 4)]
    public void Placement_ReproducesRenderMeshVertices(int tileX, int tileY)
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
