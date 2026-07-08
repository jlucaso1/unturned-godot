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
        Assert.Equal(0x8123 / 65535f, tile.Heights[1, 2], 6);
        Assert.Equal(0f, tile.Heights[0, 0]);
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
        Assert.Equal(1f, splat.Weights[0, 0, 3]);
        Assert.Equal(0f, splat.Weights[0, 0, 0]);
    }

    [Fact]
    public void Splatmap_ShortBuffer_Throws()
    {
        Assert.Throws<IOException>(() => SplatmapTile.Parse(new byte[10], 0, 0));
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
}
