using System;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class SplatSamplerTests
{
    private static readonly Guid Layer0 = Guid.Parse("11111111111111111111111111111111");
    private static readonly Guid Layer3 = Guid.Parse("33333333333333333333333333333333");

    // A tile whose splatmap is layer 0 everywhere except one cell painted layer 3.
    private static SplatmapTile Tile(int coordX, int coordY, int paintedX, int paintedY)
    {
        const int res = Landscape.SPLATMAP_RESOLUTION;
        var data = new byte[res * res * SplatmapTile.LAYERS];
        for (int x = 0; x < res; x++)
            for (int y = 0; y < res; y++)
                data[SplatmapTile.WeightIndex(x, y, 0)] = 255;
        data[SplatmapTile.WeightIndex(paintedX, paintedY, 0)] = 10;
        data[SplatmapTile.WeightIndex(paintedX, paintedY, 3)] = 200;
        return SplatmapTile.Parse(data, coordX, coordY);
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
        sampler.Add(Tile(0, 0, paintedX: 128, paintedY: 64), Materials());

        Assert.True(sampler.TryGetDominantMaterial(256f, 512f, out Guid painted));
        Assert.Equal(Layer3, painted);

        Assert.True(sampler.TryGetDominantMaterial(700f, 100f, out Guid elsewhere));
        Assert.Equal(Layer0, elsewhere);
    }

    [Fact]
    public void DominantMaterial_NegativeTileCoords()
    {
        var sampler = new SplatSampler();
        sampler.Add(Tile(-1, -1, paintedX: 0, paintedY: 0), Materials());

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
        sampler.Add(Tile(0, 0, 1, 1), unmapped);
        Assert.False(sampler.TryGetDominantMaterial(100f, 100f, out _));
        Assert.Equal(1, sampler.TileCount);

        var short_ = new SplatSampler(); // materials list shorter than the dominant layer index
        short_.Add(Tile(0, 0, 1, 1), Array.Empty<Guid>());
        Assert.False(short_.TryGetDominantMaterial(100f, 100f, out _));
    }
}
