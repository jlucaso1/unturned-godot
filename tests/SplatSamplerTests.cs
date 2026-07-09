using System;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class SplatSamplerTests
{
    private static readonly Guid Layer0 = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid Layer3 = Guid.Parse("33333333333333333333333333333333");

    // A tile whose splatmap is layer 0 everywhere except one cell painted layer 3, reduced to the
    // per-texel dominant-layer index exactly as the sampler consumes it.
    private static byte[] Tile(int paintedX, int paintedY)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var data = new byte[res * res * SplatmapTile.LAYERS];
        for (int x = 0; x < res; x++)
            for (int y = 0; y < res; y++)
                data[SplatmapTile.WeightIndex(x, y, 0)] = 255;
        data[SplatmapTile.WeightIndex(paintedX, paintedY, 0)] = 10;
        data[SplatmapTile.WeightIndex(paintedX, paintedY, 3)] = 200;
        return SplatmapTile.DominantLayers(data);
    }

    private static Guid[] Materials()
    {
        var materials = new Guid[SplatmapTile.LAYERS];
        materials[0] = Layer0;
        materials[3] = Layer3;
        return materials;
    }

    [Fact]
    public void DominantMaterial_UsesTheTransposedSplatCoord()
    {
        // SplatmapCoord: x from world Z, y from world X. Cell (sx=128, sy=64) is the world position
        // (x = 64/256*1024 = 256, z = 128/256*1024 = 512) inside tile (0, 0).
        var sampler = new SplatSampler();
        sampler.Add(0, 0, Tile(paintedX: 128, paintedY: 64), Materials());

        Assert.True(sampler.TryGetDominantMaterial(256f, 512f, out Guid painted));
        Assert.Equal(Layer3, painted);

        Assert.True(sampler.TryGetDominantMaterial(700f, 100f, out Guid elsewhere));
        Assert.Equal(Layer0, elsewhere);
    }

    [Fact]
    public void DominantMaterial_NegativeTileCoords()
    {
        var sampler = new SplatSampler();
        sampler.Add(-1, -1, Tile(paintedX: 0, paintedY: 0), Materials());

        // Tile (-1,-1) spans [-1024, 0); its cell (0,0) covers world [-1024, -1020) on each axis.
        Assert.True(sampler.TryGetDominantMaterial(-1022f, -1022f, out Guid corner));
        Assert.Equal(Layer3, corner);
        Assert.True(sampler.TryGetDominantMaterial(-512f, -512f, out Guid middle));
        Assert.Equal(Layer0, middle);
    }

    [Fact]
    public void DominantMaterial_MissingTileOrEmptyGuid_ReturnsFalse()
    {
        var sampler = new SplatSampler();
        Assert.False(sampler.TryGetDominantMaterial(5000f, 5000f, out _)); // no tile there

        var unmapped = new Guid[SplatmapTile.LAYERS]; // dominant layer maps to Guid.Empty
        sampler.Add(0, 0, Tile(1, 1), unmapped);
        Assert.False(sampler.TryGetDominantMaterial(100f, 100f, out _));
        Assert.Equal(1, sampler.TileCount);

        var short_ = new SplatSampler(); // materials list shorter than the dominant layer index
        short_.Add(0, 0, Tile(1, 1), Array.Empty<Guid>());
        Assert.False(short_.TryGetDominantMaterial(100f, 100f, out _));
    }

    [Fact]
    public void DominantLayers_MatchesTheFloatWeightArgmax_IncludingTies()
    {
        // Random weights per texel: the byte argmax must agree with an argmax over byte/255 floats
        // (monotonic), and exact ties must keep the FIRST layer, like the original strict '>' loop.
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var data = new byte[res * res * SplatmapTile.LAYERS];
        var rng = new Random(7);
        rng.NextBytes(data);
        // Force a tie in cell (0,0): layers 2 and 5 equal and maximal -> layer 2 must win.
        for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
            data[SplatmapTile.WeightIndex(0, 0, layer)] = 0;
        data[SplatmapTile.WeightIndex(0, 0, 2)] = 200;
        data[SplatmapTile.WeightIndex(0, 0, 5)] = 200;

        byte[] dominant = SplatmapTile.DominantLayers(data);
        Assert.Equal(2, dominant[0]);

        SplatmapTile reference = SplatmapTile.Parse(data, 0, 0);
        for (int x = 0; x < res; x += 17)
            for (int y = 0; y < res; y += 13)
            {
                int best = 0;
                float bestWeight = -1f;
                for (int layer = 0; layer < SplatmapTile.LAYERS; layer++)
                {
                    float w = reference.WeightAt(x, y, layer);
                    if (w > bestWeight)
                    {
                        bestWeight = w;
                        best = layer;
                    }
                }
                Assert.Equal(best, dominant[(x * res) + y]);
            }
    }

    [Fact]
    public void DominantLayers_RejectsShortData()
    {
        Assert.Throws<System.IO.IOException>(() => SplatmapTile.DominantLayers(new byte[10]));
    }
}
