using System;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class TerrainTests
{
    [Fact]
    public void Heightmap_ReadsBigEndian_257Grid()
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var bytes = new byte[res * res * 2];
        // Put a known value at [1,2]: raw 0x8123, stored high byte first.
        int idx = (1 * res + 2) * 2;
        bytes[idx] = 0x81;
        bytes[idx + 1] = 0x23;

        using var dir = new TempDir();
        HeightmapTile tile = HeightmapTile.Read(dir.Write("t.heightmap", bytes), 3, -4);

        Assert.Equal(3, tile.CoordX);
        Assert.Equal(-4, tile.CoordY);
        Assert.False(tile.HasMaterializedHeights);
        Assert.Equal(0x8123 / 65535f, tile.HeightAt(1, 2), 6);
        Assert.Equal(0f, tile.HeightAt(0, 0));
        Assert.False(tile.HasMaterializedHeights); // normal runtime reads do not allocate the float grid
        var sampler = new HeightmapSampler(new[] { tile });
        int sampleBytes = System.Environment.GetEnvironmentVariable("UG_COMPACT_HEIGHTMAP") == "0"
            ? sizeof(float) : sizeof(ushort);
        Assert.Equal((long)res * res * sampleBytes, sampler.StorageBytes);
        float cell = Landscape.TILE_SIZE / Landscape.HEIGHTMAP_RESOLUTION_MINUS_ONE;
        Assert.True(sampler.TrySampleHeight((tile.CoordX * Landscape.TILE_SIZE) + (2 * cell),
            (tile.CoordY * Landscape.TILE_SIZE) + (1 * cell), out float sampled));
        Assert.Equal((-Landscape.TILE_HEIGHT / 2f) + ((0x8123 / 65535f) * Landscape.TILE_HEIGHT), sampled, 5);
        Assert.Equal(0x8123 / 65535f, tile.Heights[1, 2], 6);
        Assert.True(tile.HasMaterializedHeights); // legacy compatibility view is lazy
    }

    [Fact]
    public void Heightmap_ShortFile_Throws()
    {
        using var dir = new TempDir();
        Assert.Throws<IOException>(() => HeightmapTile.Read(dir.Write("t.heightmap", new byte[10]), 0, 0));
    }

    [Fact]
    public void GetWorldPosition_MatchesUnturnedFormula()
    {
        // hx->Z, hy->X transpose; worldY = h*2048 - 1024.
        Vector3 p = Landscape.GetWorldPosition(tileX: 1, tileY: 2, hx: 0, hy: 256, heightNormalized: 0.5f);
        Assert.Equal(1 * 1024 + 1024, p.X); // hy=256 -> full tile in X
        Assert.Equal(0f, p.Y);              // 0.5 -> sea level
        Assert.Equal(2 * 1024, p.Z);        // hx=0
    }

    [Fact]
    public void Splatmap_ReadsLayers()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        // texel [0,0] layer 3 = 255 -> weight 1.
        bytes[3] = 255;

        SplatmapTile splat = SplatmapTile.Parse(bytes, 0, 0);
        Assert.Equal(1f, splat.WeightAt(0, 0, 3));
        Assert.Equal(0f, splat.WeightAt(0, 0, 0));
        Assert.Same(bytes, splat.Weights);
        Assert.Equal(res * res * SplatmapTile.LAYERS, splat.Weights.Length);
    }

    [Fact]
    public void Splatmap_ShortBuffer_Throws()
    {
        Assert.Throws<IOException>(() => SplatmapTile.Parse(new byte[10], 0, 0));
    }

    [Fact]
    public void Splatmap_ActiveLayerMask_ReportsOnlyPaintedLayers()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        bytes[SplatmapTile.WeightIndex(0, 0, 5)] = 255;   // the first texel paints layer 5...
        bytes[SplatmapTile.WeightIndex(200, 71, 2)] = 1;  // ...and one deep inside paints layer 2, barely

        Assert.Equal((1 << 5) | (1 << 2), SplatmapTile.Parse(bytes, 0, 0).ActiveLayerMask());
    }

    [Fact]
    public void Splatmap_ActiveLayerMask_UnpaintedTileIsEmpty()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        Assert.Equal(0, SplatmapTile.Parse(bytes, 0, 0).ActiveLayerMask());
    }

    [Fact]
    public void Splatmap_ActiveLayerMask_EveryLayerPaintedStopsEarly()
    {
        // The scan gives up as soon as all eight are accounted for; painting them all in the first texel
        // proves the early exit reports the same answer the full walk would.
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
            bytes[SplatmapTile.WeightIndex(0, 0, layer)] = 32;

        Assert.Equal(0xFF, SplatmapTile.Parse(bytes, 0, 0).ActiveLayerMask());
    }

    [Fact]
    public void Splatmap_TryRead_MissingFile_ReturnsNull()
    {
        Assert.Null(SplatmapTile.TryRead("/no/such.splatmap", 0, 0));
    }

    [Fact]
    public void Splatmap_TryRead_ExistingFile()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        using var dir = new TempDir();
        Assert.NotNull(SplatmapTile.TryRead(dir.Write("t.splatmap", bytes), 1, 1));
    }

    [Theory]
    [InlineData(-5f, 0.15f, 0.35f, 0.6f)]   // water
    [InlineData(1f, 0.85f, 0.8f, 0.55f)]    // sand
    [InlineData(10f, 0.3f, 0.55f, 0.25f)]   // grass
    [InlineData(35f, 0.45f, 0.4f, 0.3f)]    // rock
    [InlineData(100f, 0.95f, 0.95f, 0.95f)] // peak
    public void HeightColor_Bands(float y, float r, float g, float b)
    {
        Color c = TerrainColor.HeightColor(y);
        Assert.Equal(new Color(r, g, b), c);
    }

    [Fact]
    public void ForVertex_NoSplat_UsesHeightColor()
    {
        Assert.Equal(TerrainColor.HeightColor(10f), TerrainColor.ForVertex(null, 0, 0, 10f));
    }

    [Fact]
    public void ForVertex_WithSplat_BlendsAndClampsEdge()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        // Give the last texel [255,255] full weight on layer 1 (sand palette).
        int idx = ((255 * res) + 255) * SplatmapTile.LAYERS + 1;
        bytes[idx] = 255;
        SplatmapTile splat = SplatmapTile.Parse(bytes, 0, 0);

        // Heightmap edge index 256 must clamp onto splat texel 255.
        Color c = TerrainColor.ForVertex(splat, 256, 256, 0f);
        Assert.Equal(TerrainPalette.LayerColors[1], c);
    }

    [Fact]
    public void ForVertex_WithSplat_InteriorTexel_NoClamp()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var bytes = new byte[res * res * SplatmapTile.LAYERS];
        int idx = ((5 * res) + 6) * SplatmapTile.LAYERS + 7; // texel [5,6] layer 7 (snow)
        bytes[idx] = 255;
        SplatmapTile splat = SplatmapTile.Parse(bytes, 0, 0);

        // hx=5, hy=6 are below the splat resolution, so both clamp ternaries take the non-clamped side.
        Assert.Equal(TerrainPalette.LayerColors[7], TerrainColor.ForVertex(splat, 5, 6, 0f));
    }

    [Fact]
    public void Palette_Blend_ZeroWeights_FallsBackToLayer0()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        SplatmapTile splat = SplatmapTile.Parse(new byte[res * res * SplatmapTile.LAYERS], 0, 0);
        Assert.Equal(TerrainPalette.LayerColors[0], TerrainPalette.Blend(splat, 0, 0));
    }

    // Golden reference for Blend, computed straight from the source bytes with the exact same accumulation
    // order. Independent of SplatmapTile's internal storage, so it stays valid while Blend is optimized —
    // any change that is not bit-for-bit identical will fail the characterization tests below.
    private static Color ReferenceBlend(byte[] data, int x, int y)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        float r = 0, g = 0, b = 0, total = 0;
        int p = (x * res + y) * SplatmapTile.LAYERS;
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
        {
            float w = data[p + layer] / 255f;
            Color c = TerrainPalette.LayerColors[layer];
            r += c.R * w;
            g += c.G * w;
            b += c.B * w;
            total += w;
        }
        if (total <= 0f)
            return TerrainPalette.LayerColors[0];
        return new Color(r / total, g / total, b / total);
    }

    private static void AssertBitIdentical(Color expected, Color actual, int x, int y) =>
        Assert.True(expected.R == actual.R && expected.G == actual.G && expected.B == actual.B,
            $"Blend not bit-identical at ({x},{y}): expected {expected}, got {actual}");

    [Fact]
    public void Palette_Blend_BitIdentical_AcrossVariedWeights()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var data = new byte[res * res * SplatmapTile.LAYERS];
        // Deterministic fill so every layer contributes and the normalization path is exercised broadly.
        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)((i * 37 + 13) % 256);
        // A couple of all-zero texels to hit the fallback branch too.
        int zero = (10 * res + 20) * SplatmapTile.LAYERS;
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
            data[zero + layer] = 0;

        SplatmapTile splat = SplatmapTile.Parse(data, 0, 0);

        var probes = new[]
        {
            (0, 0), (0, res - 1), (res - 1, 0), (res - 1, res - 1),
            (1, 1), (10, 20), (128, 200), (255, 128), (200, 55), (63, 191),
        };
        foreach ((int x, int y) in probes)
            AssertBitIdentical(ReferenceBlend(data, x, y), TerrainPalette.Blend(splat, x, y), x, y);
    }

    [Fact]
    public void Palette_Blend_BitIdentical_SweepEveryLayerPair()
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        // Every ordered pair of layers mixed at 100/155 vs 200/255 weights — a broad set of two-layer
        // normalizations where any reordering of the float accumulation would surface.
        for (int i = 0; i < SplatmapTile.LAYERS; i++)
        {
            for (int j = 0; j < SplatmapTile.LAYERS; j++)
            {
                var data = new byte[res * res * SplatmapTile.LAYERS];
                int p = (7 * res + 9) * SplatmapTile.LAYERS;
                data[p + i] = 100;
                data[p + j] = (byte)(data[p + j] + 200); // overlaps add when i==j
                SplatmapTile splat = SplatmapTile.Parse(data, 0, 0);
                AssertBitIdentical(ReferenceBlend(data, 7, 9), TerrainPalette.Blend(splat, 7, 9), 7, 9);
            }
        }
    }
}
