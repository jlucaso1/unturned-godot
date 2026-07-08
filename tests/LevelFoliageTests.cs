using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelFoliageTests
{
    // Builds a synthetic Foliage.blob: a header (version, tile table, asset GUID list) followed by each
    // tile's instances. Matrices are 16 floats (column-major) trailed by the clearWhenBaked flag.
    private static byte[] Build(int version, Guid asset, params (int x, int y, float[][] instances)[] tiles)
    {
        using var body = new MemoryStream();
        var offsets = new long[tiles.Length];
        using (var bw = new BinaryWriter(body, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            for (int t = 0; t < tiles.Length; t++)
            {
                offsets[t] = body.Position;
                bw.Write(tiles[t].instances.Length); // instanceCount
                foreach (float[] m in tiles[t].instances)
                {
                    if (version >= 2) bw.Write(0);          // assetIndex
                    else bw.Write(asset.ToByteArray());     // per-instance GUID
                    bw.Write(1);                            // matrixCount
                    foreach (float f in m) bw.Write(f);
                    bw.Write(false);                        // clearWhenBaked
                }
            }
        }

        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(version);
            w.Write(tiles.Length);
            for (int t = 0; t < tiles.Length; t++)
            {
                w.Write(tiles[t].x);
                w.Write(tiles[t].y);
                w.Write(offsets[t]);
            }
            if (version >= 2)
            {
                w.Write(1);                    // asset count
                w.Write(asset.ToByteArray());
            }
            w.Write(body.ToArray());
        }
        return ms.ToArray();
    }

    // Column-major TRS with only a translation (identity basis).
    private static float[] Translation(float x, float y, float z) =>
        new[] { 1f, 0, 0, 0, 0, 1f, 0, 0, 0, 0, 1f, 0, x, y, z, 1f };

    [Fact]
    public void Parse_ReadsTilesAssetsAndTransforms()
    {
        var guid = new Guid("c928fb99bae9434795563319a64f6461");
        byte[] blob = Build(2, guid, (3, 5, new[] { Translation(10f, 20f, 30f) }));

        LevelFoliage foliage = LevelFoliage.Parse(blob);

        Assert.Equal(2, foliage.Version);
        Assert.Equal(guid, Assert.Single(foliage.AssetGuids));
        FoliageTile tile = Assert.Single(foliage.Tiles);
        Assert.Equal((3, 5), (tile.X, tile.Y));
        FoliageInstances inst = Assert.Single(tile.Instances);
        Assert.Equal(guid, inst.Asset);
        // Unity (10,20,30) -> Godot negates Z.
        Assert.Equal(new Vector3(10f, 20f, -30f), Assert.Single(inst.Transforms).Origin);
    }

    [Fact]
    public void Parse_Version1_ReadsPerInstanceGuid()
    {
        var guid = new Guid("aefaa04d7d3d4a7a85d2b1419a6a2ff4");
        byte[] blob = Build(1, guid, (0, 0, new[] { Translation(1f, 2f, 3f) }));

        LevelFoliage foliage = LevelFoliage.Parse(blob);

        Assert.Equal(1, foliage.Version);
        Assert.Empty(foliage.AssetGuids); // v1 has no asset list header
        Assert.Equal(guid, Assert.Single(Assert.Single(foliage.Tiles).Instances).Asset);
    }

    [Fact]
    public void Parse_Handedness_NegatesBasisZTerms()
    {
        // A basis with cross Z terms; the Unity->Godot flip negates the off-diagonal Z entries.
        float[] m = { 1, 0, 2, 0, 0, 1, 3, 0, 4, 5, 1, 0, 0, 0, 0, 1 };
        byte[] blob = Build(2, Guid.NewGuid(), (0, 0, new[] { m }));

        Transform3D t = Assert.Single(Assert.Single(Assert.Single(LevelFoliage.Parse(blob).Tiles).Instances).Transforms);
        Assert.Equal(new Vector3(1, 0, -2), t.Basis.X);   // column 0: z-row negated
        Assert.Equal(new Vector3(0, 1, -3), t.Basis.Y);   // column 1: z-row negated
        Assert.Equal(new Vector3(-4, -5, 1), t.Basis.Z);  // column 2: x/y rows negated
    }

    [Fact]
    public void Parse_InvalidAssetIndex_AndZeroMatrixInstance()
    {
        var guid = new Guid("c928fb99bae9434795563319a64f6461");
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(2);                    // version
            w.Write(1);                    // tile count
            w.Write(0); w.Write(0); w.Write(0L); // tile (0,0) at offset 0
            w.Write(1);                    // asset count
            w.Write(guid.ToByteArray());
            // tile body (offset 0): three instances
            w.Write(3);                    // instanceCount
            w.Write(99); w.Write(1);       // instance A: out-of-range asset index (>= count), 1 matrix
            foreach (float f in Translation(0, 0, 0)) w.Write(f);
            w.Write(false);
            w.Write(-1); w.Write(1);       // instance B: negative asset index, 1 matrix
            foreach (float f in Translation(0, 0, 0)) w.Write(f);
            w.Write(false);
            w.Write(0); w.Write(0);        // instance C: valid asset, 0 matrices (dropped)
        }

        FoliageTile tile = Assert.Single(LevelFoliage.Parse(ms.ToArray()).Tiles);
        Assert.Equal(2, tile.Instances.Count);                 // A and B survive; C is dropped
        Assert.All(tile.Instances, i => Assert.Equal(Guid.Empty, i.Asset)); // both indices resolve to none
    }

    [Fact]
    public void Parse_EmptyTile_IsOmitted()
    {
        byte[] blob = Build(2, Guid.NewGuid(), (0, 0, Array.Empty<float[]>()));
        Assert.Empty(LevelFoliage.Parse(blob).Tiles);
    }

    [Fact]
    public void Parse_UnsupportedVersion_Throws() =>
        Assert.Throws<NotSupportedException>(() => LevelFoliage.Parse(new byte[] { 9, 0, 0, 0, 0, 0, 0, 0 }));

    [Fact]
    public void Load_MissingFile_ReturnsNull() =>
        Assert.Null(LevelFoliage.Load(Path.Combine(Path.GetTempPath(), "no-such-foliage.blob")));

    [Fact]
    public void Load_ReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"foliage-{Guid.NewGuid():N}.blob");
        File.WriteAllBytes(path, Build(2, Guid.NewGuid(), (1, 1, new[] { Translation(0, 0, 0) })));
        try
        {
            Assert.Equal(2, LevelFoliage.Load(path)!.Version);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
