using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
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
        Assert.Equal(1, inst.Count);
        Assert.Equal(new Vector3(10f, 20f, -30f), inst.InstanceTransform(0).Origin);
        Assert.Equal(new Vector3(10f, 20f, -30f), inst.Bounds.Min);
        Assert.Equal(inst.Bounds.Min, inst.Bounds.Max);
        Assert.Equal(3f, inst.Bounds.MaxScaleSquared); // Frobenius norm of the identity basis
        Assert.Equal(1, inst.Bounds.Count);
    }

    [Fact]
    public void ReadAssetGuids_V2ReadsTheHeaderWithoutDecodingTileBodies()
    {
        Guid guid = Guid.NewGuid();
        byte[] blob = Build(2, guid); // no tile body is needed for the v2 asset list
        using var dir = new TempDir();
        string path = dir.Write("Foliage.blob", blob);

        Assert.Equal(new[] { guid }, LevelFoliageChunks.ReadAssetGuids(path));
    }

    [Fact]
    public void ReadAssetGuids_V1WalksRunHeadersWithoutBuildingTransforms()
    {
        Guid guid = Guid.NewGuid();
        using var dir = new TempDir();
        string path = dir.Write("Foliage-v1.blob",
            Build(1, guid, (0, 0, new[] { Translation(1f, 2f, 3f) })));

        Assert.Equal(new[] { guid }, LevelFoliageChunks.ReadAssetGuids(path));
    }

    [Fact]
    public void Bounds_CombineMatchesMeasuringTheConcatenatedTransforms()
    {
        float[] a =
        {
            1, 0, 0, -4, 0, 2, 0, 3, 0, 0, 1, 8,
            2, 0, 0, 10, 0, 1, 0, -2, 0, 0, 3, -6,
        };
        float[] b = { 1, 0, 0, 1, 0, 1, 0, 7, 0, 0, 1, 2 };
        float[] all = new float[a.Length + b.Length];
        Array.Copy(a, all, a.Length);
        Array.Copy(b, 0, all, a.Length, b.Length);

        FoliageBounds combined = FoliageBounds.Measure(a).Include(FoliageBounds.Measure(b));
        FoliageBounds direct = FoliageBounds.Measure(all);

        Assert.Equal(direct, combined);
        Assert.Equal(new Vector3(-4, -2, -6), combined.Min);
        Assert.Equal(new Vector3(10, 7, 8), combined.Max);
        Assert.Equal(14f, combined.MaxScaleSquared);
        Assert.Equal(3, combined.Count);
    }

    [Fact]
    public void Bounds_EmptyRunsAreIdentityForCombination()
    {
        FoliageBounds value = FoliageBounds.Measure(new float[]
            { 1, 0, 0, 2, 0, 1, 0, 3, 0, 0, 1, 4 });

        Assert.Equal(value, FoliageBounds.Empty.Include(value));
        Assert.Equal(value, value.Include(FoliageBounds.Empty));
        Assert.Equal(0, FoliageBounds.Measure(Array.Empty<float>()).Count);
    }

    [Fact]
    public void CompactGroups_PreserveFirstSeenAndRunOrder_FilterMissingAndFloorNegativeChunks()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), missing = Guid.NewGuid();
        var a0 = new FoliageInstances(a, Translation(1, 2, 3)[..12]);
        var a1 = new FoliageInstances(a, Translation(4, 5, 6)[..12]);
        var b0 = new FoliageInstances(b, Translation(7, 8, 9)[..12]);
        var nope = new FoliageInstances(missing, Translation(0, 0, 0)[..12]);
        var tiles = new[]
        {
            new FoliageTile(-1, 0, new[] { a0, b0, nope }),
            new FoliageTile(-4, 3, new[] { a1 }),
        };

        FoliageGroups groups = FoliageGroups.Build(tiles, 4, new HashSet<Guid> { a, b });

        Assert.Equal(new[] { new FoliageGroupKey(-1, 0, a), new FoliageGroupKey(-1, 0, b) }, groups.Keys);
        Assert.Equal(new[] { 0, 2, 3 }, groups.Starts);
        Assert.Same(a0, groups.Runs[0]);
        Assert.Same(a1, groups.Runs[1]);
        Assert.Same(b0, groups.Runs[2]);
        Assert.True(groups.StorageBytes > 0);
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

        FoliageInstances inst = Assert.Single(Assert.Single(LevelFoliage.Parse(blob).Tiles).Instances);
        Assert.Equal(1, inst.Count);
        Transform3D t = inst.InstanceTransform(0);
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
    public void DirectLoad_MissingFileReturnsNull_AndUnsupportedVersionThrows()
    {
        Assert.Null(LevelFoliageChunks.Load(Path.Combine(Path.GetTempPath(), "no-such-direct-foliage.blob")));
        string path = Path.Combine(Path.GetTempPath(), $"foliage-version-{Guid.NewGuid():N}.blob");
        File.WriteAllBytes(path, new byte[] { 9, 0, 0, 0, 0, 0, 0, 0 });
        try { Assert.Throws<NotSupportedException>(() => LevelFoliageChunks.Load(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_ReadsFromDisk()
    {
        string path = Path.Combine(Path.GetTempPath(), $"foliage-{Guid.NewGuid():N}.blob");
        byte[] bytes = Build(2, Guid.NewGuid(),
            (1, 1, new[] { Translation(0, 0, 0), Translation(4, 5, 6) }),
            (2, -3, Array.Empty<float[]>()),
            (7, 9, new[] { Translation(-2, 3, 8) }));
        File.WriteAllBytes(path, bytes);
        try
        {
            LevelFoliage expected = LevelFoliage.Parse(bytes);
            LevelFoliage actual = LevelFoliage.Load(path)!;
            Assert.Equal(expected.Version, actual.Version);
            Assert.Equal(expected.AssetGuids, actual.AssetGuids);
            Assert.Equal(expected.Tiles.Count, actual.Tiles.Count);
            for (int tile = 0; tile < expected.Tiles.Count; tile++)
            {
                Assert.Equal((expected.Tiles[tile].X, expected.Tiles[tile].Y),
                    (actual.Tiles[tile].X, actual.Tiles[tile].Y));
                Assert.Equal(expected.Tiles[tile].Instances.Count, actual.Tiles[tile].Instances.Count);
                for (int run = 0; run < expected.Tiles[tile].Instances.Count; run++)
                {
                    Assert.Equal(expected.Tiles[tile].Instances[run].Asset,
                        actual.Tiles[tile].Instances[run].Asset);
                    Assert.Equal(expected.Tiles[tile].Instances[run].Packed,
                        actual.Tiles[tile].Instances[run].Packed);
                    Assert.Equal(expected.Tiles[tile].Instances[run].Bounds,
                        actual.Tiles[tile].Instances[run].Bounds);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CrossesBoundedFileBatchWithoutChangingTileOrder()
    {
        var guid = Guid.NewGuid();
        var tiles = new (int x, int y, float[][] instances)[8200];
        for (int i = 0; i < tiles.Length; i++)
            tiles[i] = (i, -i, Array.Empty<float[]>());
        tiles[0] = (10, 20, new[] { Translation(1, 2, 3) });
        tiles[^1] = (30, 40, new[] { Translation(4, 5, 6) });
        string path = Path.Combine(Path.GetTempPath(), $"foliage-batches-{Guid.NewGuid():N}.blob");
        File.WriteAllBytes(path, Build(2, guid, tiles));
        try
        {
            LevelFoliage loaded = LevelFoliage.Load(path)!;
            Assert.Equal(2, loaded.Tiles.Count);
            Assert.Equal((10, 20), (loaded.Tiles[0].X, loaded.Tiles[0].Y));
            Assert.Equal((30, 40), (loaded.Tiles[1].X, loaded.Tiles[1].Y));
            Assert.Equal(new Vector3(1, 2, -3), loaded.Tiles[0].Instances[0].InstanceTransform(0).Origin);
            Assert.Equal(new Vector3(4, 5, -6), loaded.Tiles[1].Instances[0].InstanceTransform(0).Origin);

            LevelFoliageChunks direct = LevelFoliageChunks.Load(path, 4)!;
            Assert.Equal(2, direct.Chunks.Count);
            Assert.Equal(2, direct.Chunks.Sum(c => c.Count));
            Assert.True(direct.DecodeBatchPeakBytes > 0);
            Assert.Contains(direct.Chunks, c => c.Packed[3] == 1 && c.Packed[7] == 2 && c.Packed[11] == -3);
            Assert.Contains(direct.Chunks, c => c.Packed[3] == 4 && c.Packed[7] == 5 && c.Packed[11] == -6);
        }
        finally
        {
            File.Delete(path);
        }
    }


    [Fact]
    public void DirectChunks_Version1PreservesPerRunGuid()
    {
        Guid asset = Guid.NewGuid();
        string path = Path.Combine(Path.GetTempPath(), $"foliage-direct-v1-{Guid.NewGuid():N}.blob");
        File.WriteAllBytes(path, Build(1, asset, (0, 0, new[] { Translation(2, 3, 4) })));
        try
        {
            LevelFoliageChunks direct = LevelFoliageChunks.Load(path)!;
            Assert.Equal(new[] { asset }, direct.AssetGuids);
            FoliageChunk chunk = Assert.Single(direct.Chunks);
            Assert.Equal(asset, chunk.Key.Asset);
            Assert.Equal(new Vector3(2, 3, -4), chunk.Bounds.Min);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DirectChunks_ParallelAndSequentialRebaseAreBitIdentical()
    {
        Guid asset = Guid.NewGuid();
        string path = Path.Combine(Path.GetTempPath(), $"foliage-rebase-{Guid.NewGuid():N}.blob");
        File.WriteAllBytes(path, Build(2, asset,
            (0, 0, new[] { Translation(-20, 3, 7), Translation(11, -4, 90) }),
            (7, -5, new[] { Translation(500, 25, -600) })));
        try
        {
            LevelFoliageChunks sequential = LevelFoliageChunks.Load(path)!;
            LevelFoliageChunks parallel = LevelFoliageChunks.Load(path)!;
            sequential.RebaseAll(parallel: false);
            parallel.RebaseAll(parallel: true);
            Assert.Equal(sequential.Chunks.Count, parallel.Chunks.Count);
            for (int i = 0; i < sequential.Chunks.Count; i++)
            {
                Assert.Equal(sequential.Chunks[i].Origin, parallel.Chunks[i].Origin);
                Assert.Equal(sequential.Chunks[i].Packed, parallel.Chunks[i].Packed);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DirectChunks_MatchLegacyGroupingAndPreserveWorldTransformsAfterRebase()
    {
        Guid asset = Guid.NewGuid();
        byte[] bytes = Build(2, asset,
            (0, 0, new[] { Translation(1, 2, 3), Translation(4, 5, 6) }),
            (3, 2, new[] { Translation(7, 8, 9) }),
            (4, 0, new[] { Translation(20, 3, 4) }));
        string path = Path.Combine(Path.GetTempPath(), $"foliage-direct-{Guid.NewGuid():N}.blob");
        File.WriteAllBytes(path, bytes);
        try
        {
            LevelFoliage legacy = LevelFoliage.Parse(bytes);
            FoliageGroups expected = FoliageGroups.Build(legacy.Tiles, 4, new HashSet<Guid> { asset });
            LevelFoliageChunks direct = LevelFoliageChunks.Load(path, 4)!;

            Assert.Equal(expected.Count, direct.Chunks.Count);
            Assert.Equal((long)expected.Runs.Sum(run => run.Packed.Length) * sizeof(float), direct.StorageBytes);
            for (int group = 0; group < expected.Count; group++)
            {
                FoliageChunk actual = direct.Chunks[group];
                Assert.Equal(expected.Keys[group], actual.Key);
                var packed = new List<float>();
                for (int i = expected.Starts[group]; i < expected.Starts[group + 1]; i++)
                    packed.AddRange(expected.Runs[i].Packed);
                Assert.Equal(packed, actual.Packed);

                float worldX = actual.Packed[3], worldY = actual.Packed[7], worldZ = actual.Packed[11];
                actual.RebaseInPlace();
                Assert.Equal(worldX, actual.Packed[3] + actual.Origin.X, 5);
                Assert.Equal(worldY, actual.Packed[7] + actual.Origin.Y, 5);
                Assert.Equal(worldZ, actual.Packed[11] + actual.Origin.Z, 5);
                float[] once = (float[])actual.Packed.Clone();
                actual.RebaseInPlace();
                Assert.Equal(once, actual.Packed); // lifecycle call is idempotent
            }
        }
        finally { File.Delete(path); }
    }
}
