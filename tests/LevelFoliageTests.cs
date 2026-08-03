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

    [Fact]
    public void ResidencyIndex_DecodesEveryChunkIdenticallyToTheAllResidentPath()
    {
        Guid a = Guid.NewGuid();
        byte[] bytes = Build(2, a,
            (-5, -1, new[] { Translation(-20, 3, 7), Translation(11, -4, 90) }),
            (-4, 0, new[] { Translation(5, 6, 7) }),
            (7, 8, new[] { Translation(500, 25, -600), Translation(20, 30, 40) }));
        using var dir = new TempDir();
        string path = dir.Write("Foliage.blob", bytes);
        LevelFoliageChunks expected = LevelFoliageChunks.Load(path, 4)!;
        expected.RebaseAll(parallel: false);

        FoliageResidencyIndex index = FoliageResidencyIndex.Build(path, 4);

        Assert.Equal(expected.AssetGuids, index.AssetGuids);
        Assert.Equal(expected.Chunks.Count, index.Chunks.Count);
        Assert.Equal(expected.StorageBytes, index.DecodedStorageBytes);
        for (int i = 0; i < expected.Chunks.Count; i++)
        {
            FoliageChunk decoded = index.DecodeChunk(i);
            Assert.Equal(expected.Chunks[i].Key, decoded.Key);
            Assert.Equal(expected.Chunks[i].Bounds, decoded.Bounds);
            Assert.Equal(expected.Chunks[i].Origin, decoded.Origin);
            Assert.Equal(expected.Chunks[i].Packed, decoded.Packed);
        }
    }

    [Fact]
    public void ResidencyIndex_CacheRoundTripsAndRejectsSameLengthContentChanges()
    {
        Guid asset = Guid.NewGuid();
        using var dir = new TempDir();
        string source = dir.Write("Foliage.blob",
            Build(2, asset, (0, 0, new[] { Translation(1, 2, 3) })));
        string cache = Path.Combine(dir.Path, "cache", "map.fidx");
        FoliageResidencyIndex built = FoliageResidencyIndex.Build(source, 4);
        built.Write(cache);

        Assert.True(FoliageResidencyIndex.TryRead(cache, source, 4, out FoliageResidencyIndex? loaded));
        Assert.Equal(built.DecodeChunk(0).Packed, loaded!.DecodeChunk(0).Packed);
        Assert.False(FoliageResidencyIndex.TryRead(cache, source, 8, out _));

        DateTime timestamp = File.GetLastWriteTimeUtc(source);
        byte[] changed = File.ReadAllBytes(source);
        changed[^6] ^= 0x40;
        File.WriteAllBytes(source, changed);
        File.SetLastWriteTimeUtc(source, timestamp); // unchanged path, size, and timestamp: hash must catch it
        Assert.False(FoliageResidencyIndex.TryRead(cache, source, 4, out _));
    }

    // The identity hash is the cache's only defence against a source edited behind an unchanged path,
    // length and timestamp, and its width is part of the sidecar layout. A sidecar written by an older
    // build must therefore be rejected on the version alone, before anything reads a differently sized
    // hash and misinterprets the bytes after it.
    [Fact]
    public void ResidencyIndex_SidecarFromAnEarlierCacheVersionIsRejected()
    {
        Guid asset = Guid.NewGuid();
        using var dir = new TempDir();
        string source = dir.Write("Foliage.blob",
            Build(2, asset, (0, 0, new[] { Translation(1, 2, 3) })));
        string cache = Path.Combine(dir.Path, "cache", "map.fidx");
        FoliageResidencyIndex.Build(source, 4).Write(cache);
        Assert.True(FoliageResidencyIndex.TryRead(cache, source, 4, out _));

        // Rewrite only the version field that follows the magic string.
        byte[] sidecar = File.ReadAllBytes(cache);
        int versionAt = 1 + System.Text.Encoding.UTF8.GetByteCount("UGFIDX1");
        sidecar[versionAt] = 1;
        File.WriteAllBytes(cache, sidecar);

        Assert.False(FoliageResidencyIndex.TryRead(cache, source, 4, out _));
        // And the caller still recovers by rebuilding rather than failing the load.
        FoliageResidencyIndex? rebuilt = FoliageResidencyIndex.LoadOrBuild(source, cache, 4, out bool hit);
        Assert.False(hit);
        Assert.Single(rebuilt!.Chunks);
        Assert.True(FoliageResidencyIndex.TryRead(cache, source, 4, out _));
    }

    [Fact]
    public void ResidencyIndex_CorruptOrInterruptedCacheFallsBackToRebuild()
    {
        using var dir = new TempDir();
        string source = dir.Write("Foliage.blob", Build(2, Guid.NewGuid()));
        string cache = dir.Write("map.fidx", new byte[] { 1, 2, 3 });

        Assert.False(FoliageResidencyIndex.TryRead(cache, source, 4, out _));
        FoliageResidencyIndex rebuilt = FoliageResidencyIndex.LoadOrBuild(source, cache, 4,
            out bool cacheHit)!;
        Assert.False(cacheHit);
        Assert.True(FoliageResidencyIndex.TryRead(cache, source, 4, out FoliageResidencyIndex? reused));
        Assert.Empty(rebuilt.Chunks);
        Assert.NotNull(reused);

        // An interrupted atomic replacement can retain a valid header and only part of the body.
        byte[] complete = File.ReadAllBytes(cache);
        File.WriteAllBytes(cache, complete.AsSpan(0, complete.Length / 2).ToArray());
        Assert.False(FoliageResidencyIndex.TryRead(cache, source, 4, out _));
        FoliageResidencyIndex recovered = FoliageResidencyIndex.LoadOrBuild(source, cache, 4,
            out bool truncatedHit)!;
        Assert.False(truncatedHit);
        Assert.Empty(recovered.Chunks);

        // BinaryReader.ReadString throws FormatException for a malformed 7-bit length prefix.
        File.WriteAllBytes(cache, new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80 });
        Assert.False(FoliageResidencyIndex.TryRead(cache, source, 4, out _));
    }

    [Fact]
    public void ResidencyIndex_DecodeObservesCancellationAndBoundsChecks()
    {
        using var dir = new TempDir();
        string source = dir.Write("Foliage.blob",
            Build(2, Guid.NewGuid(), (0, 0, new[] { Translation(1, 2, 3) })));
        FoliageResidencyIndex index = FoliageResidencyIndex.Build(source);
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => index.DecodeChunk(0, cancelled.Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.DecodeChunk(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.DecodeChunk(index.Chunks.Count));
    }

    [Fact]
    public void ResidencyIndex_Version1AndDifferentMapReuseRemainIsolated()
    {
        using var dir = new TempDir();
        Guid firstAsset = Guid.NewGuid(), secondAsset = Guid.NewGuid();
        string first = dir.Write(Path.Combine("first", "Foliage.blob"),
            Build(1, firstAsset, (0, 0, new[] { Translation(1, 2, 3) })));
        string second = dir.Write(Path.Combine("second", "Foliage.blob"),
            Build(2, secondAsset, (8, 8, new[] { Translation(4, 5, 6) })));
        string cache = Path.Combine(dir.Path, "shared.fidx");

        FoliageResidencyIndex firstIndex = FoliageResidencyIndex.LoadOrBuild(first, cache, 4,
            out bool firstHit)!;
        FoliageResidencyIndex secondIndex = FoliageResidencyIndex.LoadOrBuild(second, cache, 4,
            out bool secondHit)!;

        Assert.False(firstHit);
        Assert.False(secondHit); // same cache path cannot cross map/source identity
        Assert.Equal(firstAsset, Assert.Single(firstIndex.AssetGuids));
        Assert.Equal(secondAsset, Assert.Single(secondIndex.AssetGuids));
        Assert.Equal(new Vector3(1, 2, -3), firstIndex.DecodeChunk(0).Origin);
        Assert.Equal(new Vector3(4, 5, -6), secondIndex.DecodeChunk(0).Origin);
    }

    [Fact]
    public void ResidencyPlanner_OrdersNearFirstCapsWorkAndKeepsVisibleChunksMandatory()
    {
        var items = new[]
        {
            new FoliageResidencyItem(0, new Vector3(10, 0, 0), 20),
            new FoliageResidencyItem(1, new Vector3(40, 0, 0), 20),
            new FoliageResidencyItem(2, new Vector3(30, 0, 0), 20),
            new FoliageResidencyItem(3, new Vector3(500, 0, 0), 20),
        };

        FoliageResidencyPlan plan = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int>(), prefetchMargin: 30, unloadHysteresis: 20,
            maximumPrefetch: 1);

        Assert.Equal(new[] { 0 }, plan.VisibleMissing);
        Assert.Equal(new[] { 2 }, plan.Prefetch); // 30 m wins over item 1 at 40 m
        Assert.Empty(plan.Retire);
        Assert.True(plan.PrefetchTruncated);

        // Pending work does not satisfy visibility: the runtime must take its synchronous emergency path
        // rather than allowing an in-flight prefetch to create one frame of pop-in.
        FoliageResidencyPlan pendingVisible = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int> { 0 }, 30, 20, 1);
        Assert.Equal(new[] { 0 }, pendingVisible.VisibleMissing);
    }

    [Fact]
    public void ResidencyPlanner_ExistingPendingWorkConsumesTheTotalPrefetchCapacity()
    {
        var items = new[]
        {
            new FoliageResidencyItem(0, new Vector3(10, 0, 0), 0),
            new FoliageResidencyItem(1, new Vector3(20, 0, 0), 0),
            new FoliageResidencyItem(2, new Vector3(30, 0, 0), 0),
        };

        FoliageResidencyPlan capped = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int> { 0 }, prefetchMargin: 100,
            unloadHysteresis: 20, maximumPrefetch: 2);

        Assert.Equal(new[] { 1 }, capped.Prefetch);
        Assert.True(capped.PrefetchTruncated);
        Assert.Equal(1, capped.PrefetchDeferred);

        // Once the active decode becomes resident, the next refill must recover every candidate that was
        // omitted only because the prior pending work occupied the total cap.
        FoliageResidencyPlan refilled = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int> { 0 }, new HashSet<int>(), prefetchMargin: 100,
            unloadHysteresis: 20, maximumPrefetch: 2);
        Assert.Equal(new[] { 1, 2 }, refilled.Prefetch);
        Assert.False(refilled.PrefetchTruncated);
        Assert.Equal(0, refilled.PrefetchDeferred);

        FoliageResidencyPlan full = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int> { 0, 1 }, prefetchMargin: 100,
            unloadHysteresis: 20, maximumPrefetch: 2);
        Assert.Empty(full.Prefetch);
        Assert.True(full.PrefetchTruncated);
        // Only chunk 2 is a candidate here: 0 and 1 are already pending, and pending work is counted
        // against the bound rather than re-offered, so the shortfall is that one chunk.
        Assert.Equal(1, full.PrefetchDeferred);
    }

    // The deferred count is what sizes UG_FOLIAGE_MAX_PENDING against a map: it must be the exact
    // shortfall behind the bound, and must not count work the bound could still admit.
    [Fact]
    public void ResidencyPlanner_ReportsTheExactPrefetchShortfallBehindTheBound()
    {
        var items = new FoliageResidencyItem[16];
        for (int i = 0; i < items.Length; i++)
            items[i] = new FoliageResidencyItem(i, new Vector3(10 + i, 0, 0), 0);

        FoliageResidencyPlan exact = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int>(), prefetchMargin: 100,
            unloadHysteresis: 20, maximumPrefetch: items.Length);
        Assert.False(exact.PrefetchTruncated);
        Assert.Equal(0, exact.PrefetchDeferred);

        FoliageResidencyPlan starved = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int>(), prefetchMargin: 100,
            unloadHysteresis: 20, maximumPrefetch: 5);
        Assert.Equal(5, starved.Prefetch.Count);
        Assert.Equal(items.Length - 5, starved.PrefetchDeferred);

        // Chunks already resident are not candidates, so they cannot inflate the shortfall.
        FoliageResidencyPlan resident = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int> { 0, 1, 2, 3 }, new HashSet<int>(), prefetchMargin: 100,
            unloadHysteresis: 20, maximumPrefetch: 5);
        Assert.Equal(items.Length - 4 - 5, resident.PrefetchDeferred);
    }

    // The warm pass runs behind the loading screen, so it decodes a whole batch before uploading any of
    // it. That is only safe while a batch's transforms fit the same byte cap the steady loop decodes
    // under: the visible set must come first (it is the set whose absence costs a synchronous decode),
    // and the whole plan must still be covered.
    [Fact]
    public void PrewarmBatches_TakeTheVisibleSetFirstAndStayInsideTheDecodedByteCap()
    {
        var items = new List<FoliageResidencyItem>();
        for (int i = 0; i < 8; i++)
            items.Add(new FoliageResidencyItem(i, new Vector3(i * 10, 0, 0), 25));

        FoliageResidencyPlan plan = FoliageResidencyPlanner.Plan(Vector3.Zero, items,
            new HashSet<int>(), new HashSet<int>(), prefetchMargin: 40, unloadHysteresis: 20,
            maximumPrefetch: 16);
        Assert.Equal(new[] { 0, 1, 2 }, plan.VisibleMissing);
        Assert.Equal(new[] { 3, 4, 5, 6 }, plan.Prefetch);

        IReadOnlyList<int[]> batches = FoliageResidencyPlanner.WarmBatches(plan,
            index => 100, byteLimit: 250);

        Assert.Equal(new[] { new[] { 0, 1 }, new[] { 2, 3 }, new[] { 4, 5 }, new[] { 6 } }, batches);
        Assert.Equal(plan.VisibleMissing.Concat(plan.Prefetch), batches.SelectMany(b => b));
        foreach (int[] batch in batches)
            Assert.True(batch.Length * 100 <= 250);
    }

    [Fact]
    public void PrewarmBatches_KeepAnOversizedChunkRatherThanDroppingIt()
    {
        var plan = new FoliageResidencyPlan(new[] { 0 }, new[] { 1, 2 }, System.Array.Empty<int>(),
            false, 0);
        var bytes = new Dictionary<int, long> { [0] = 10, [1] = 4096, [2] = 10 };

        // A chunk larger than the entire cap gets a batch of its own: refusing to warm it would only hand
        // it back to the synchronous path the warm pass exists to empty.
        Assert.Equal(new[] { new[] { 0 }, new[] { 1 }, new[] { 2 } },
            FoliageResidencyPlanner.WarmBatches(plan, index => bytes[index], byteLimit: 64));

        // One batch when everything fits, and no batches at all when the plan asks for nothing.
        Assert.Equal(new[] { new[] { 0, 1, 2 } },
            FoliageResidencyPlanner.WarmBatches(plan, index => bytes[index], byteLimit: 8192));
        Assert.Empty(FoliageResidencyPlanner.WarmBatches(
            new FoliageResidencyPlan(System.Array.Empty<int>(), System.Array.Empty<int>(),
                System.Array.Empty<int>(), false, 0), index => 1, byteLimit: 8192));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FoliageResidencyPlanner.WarmBatches(plan, index => 1, byteLimit: 0));
    }

    [Fact]
    public void FoliageSettlingTracker_RequiresConsecutiveStableObservationsAndResetsOnWork()
    {
        var tracker = new FoliageSettlingTracker(requiredStableObservations: 3);

        Assert.False(tracker.Observe(foundRenderer: true, allSettled: true));
        Assert.False(tracker.Observe(foundRenderer: true, allSettled: true));
        Assert.False(tracker.Observe(foundRenderer: true, allSettled: false));
        Assert.False(tracker.Observe(foundRenderer: true, allSettled: true));
        Assert.False(tracker.Observe(foundRenderer: true, allSettled: true));
        Assert.True(tracker.Observe(foundRenderer: true, allSettled: true));
    }

    [Fact]
    public void FoliageSettlingTracker_NoRendererIsImmediatelyStableAndInvalidCountsAreRejected()
    {
        Assert.True(new FoliageSettlingTracker(3).Observe(foundRenderer: false, allSettled: false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FoliageSettlingTracker(0));
    }

    [Fact]
    public void ResidencyPlanner_HysteresisPreventsBoundaryChurnAndTeleportRetiresOldRegion()
    {
        var items = new[]
        {
            new FoliageResidencyItem(0, Vector3.Zero, 20),
            new FoliageResidencyItem(1, new Vector3(1000, 0, 0), 20),
        };
        var resident = new HashSet<int> { 0 };

        Assert.Empty(FoliageResidencyPlanner.Plan(new Vector3(55, 0, 0), items, resident,
            new HashSet<int>(), 30, 20, 8).Retire); // load edge is 50; unload edge is 70
        Assert.Equal(new[] { 0 }, FoliageResidencyPlanner.Plan(new Vector3(71, 0, 0), items, resident,
            new HashSet<int>(), 30, 20, 8).Retire);
        FoliageResidencyPlan teleported = FoliageResidencyPlanner.Plan(new Vector3(1000, 0, 0), items,
            resident, new HashSet<int>(), 30, 20, 8);
        Assert.Equal(new[] { 1 }, teleported.VisibleMissing);
        Assert.Equal(new[] { 0 }, teleported.Retire);
    }

    [Fact]
    public void ResidencyPlanner_PrefetchCoversDeterministicHighSpeedTraversalWithoutGrowth()
    {
        var items = new List<FoliageResidencyItem>();
        for (int i = 0; i < 40; i++)
            items.Add(new FoliageResidencyItem(i, new Vector3(i * 128, 0, 0), 200));
        var resident = new HashSet<int>();
        var pending = new HashSet<int>();

        for (int x = 0; x <= 3000; x += 64)
        {
            FoliageResidencyPlan plan = FoliageResidencyPlanner.Plan(new Vector3(x, 0, 0), items,
                resident, pending, prefetchMargin: 256, unloadHysteresis: 128, maximumPrefetch: 40);
            if (x > 0)
                Assert.Empty(plan.VisibleMissing); // prior prefetch covers the next 64 m high-speed step
            resident.UnionWith(plan.VisibleMissing); // runtime's synchronous initial/teleport guarantee
            resident.ExceptWith(plan.Retire);
            resident.UnionWith(plan.Prefetch);
            Assert.InRange(resident.Count, 1, 12); // no unbounded cache of previously visited chunks
        }
    }

    // A workshop map's Foliage.blob is third-party content, so a corrupt count must not become an
    // allocation: `new (int, int, long)[int.MaxValue]` is 32 GB, and OutOfMemoryException is not a
    // failure the load path is prepared for — it reads as a bug in the parser rather than as bad input.
    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000)]
    [InlineData(-1)]
    public void Parse_ImplausibleTileCount_FailsWithoutAllocating(int tileCount)
    {
        byte[] blob = Build(2, Guid.NewGuid(), (0, 0, new[] { Translation(1, 2, 3) }));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), tileCount);

        Assert.Throws<InvalidDataException>(() => LevelFoliage.Parse(blob));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(-1)]
    public void Parse_ImplausibleAssetCount_FailsWithoutAllocating(int assetCount)
    {
        byte[] blob = Build(2, Guid.NewGuid(), (0, 0, new[] { Translation(1, 2, 3) }));
        // version(4) + tileCount(4) + one tile header(16) = 24, then the asset count.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(24), assetCount);

        Assert.Throws<InvalidDataException>(() => LevelFoliage.Parse(blob));
    }

    // The streamed path reads the same counts off a FileStream rather than a byte[], so it needs its own
    // bound — it is the default (UG_FOLIAGE_STREAM_LOAD), so this is the path a real load takes.
    [Fact]
    public void StreamedLoad_ImplausibleTileCount_FailsWithoutAllocating()
    {
        byte[] blob = Build(2, Guid.NewGuid(), (0, 0, new[] { Translation(1, 2, 3) }));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), int.MaxValue);

        using var dir = new TempDir();
        string path = dir.Write("Foliage.blob", blob);

        Assert.Throws<InvalidDataException>(() => LevelFoliage.Load(path));
    }

    [Fact]
    public void StreamedLoad_TileOffsetsPastTheEnd_FailWithADeclaredParseError()
    {
        byte[] blob = Build(2, Guid.NewGuid(),
            (0, 0, new[] { Translation(1, 2, 3) }),
            (1, 0, new[] { Translation(4, 5, 6) }));
        // The first tile's offset (version(4) + tileCount(4) + x(4) + y(4) = 16) points past the body.
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(blob.AsSpan(16), long.MaxValue / 2);

        using var dir = new TempDir();
        string path = dir.Write("Foliage.blob", blob);

        Assert.Throws<InvalidDataException>(() => LevelFoliage.Load(path));
    }
}
