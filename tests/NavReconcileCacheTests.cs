using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

public class NavReconcileCacheTests
{
    [Fact]
    public void RoundTrip_PreservesEveryFlagAndSortsAreTransparent()
    {
        var source = new List<HashSet<int>> { new() { 4, 1 }, new(), new() { 0 } };
        int[] triangles = { 6, 2, 1 };
        using var stream = new MemoryStream();
        NavReconcileCache.Write(stream, "world-a", source, triangles);
        stream.Position = 0;

        Assert.True(NavReconcileCache.TryRead(stream, "world-a", triangles, out var loaded));
        Assert.Equal(source.Count, loaded.Count);
        for (int i = 0; i < source.Count; i++)
            Assert.True(source[i].SetEquals(loaded[i]));
    }

    [Fact]
    public void DifferentFingerprintOrTopology_IsACacheMiss()
    {
        using var stream = ValidCache();
        Assert.False(NavReconcileCache.TryRead(stream, "other", new[] { 4 }, out _));
        stream.Position = 0;
        Assert.False(NavReconcileCache.TryRead(stream, "world", new[] { 5 }, out _));
        stream.Position = 0;
        Assert.False(NavReconcileCache.TryRead(stream, "world", new[] { 4, 1 }, out _));
    }

    [Fact]
    public void PartialCheckpointRestoresCompletedFlagsButIsNotACompleteHit()
    {
        var partial = new List<HashSet<int>?> { new() { 1, 3 }, null, new() };
        int[] triangles = { 4, 5, 2 };
        using var stream = new MemoryStream();
        NavReconcileCache.WritePartial(stream, "resume", partial, triangles);
        stream.Position = 0;

        Assert.True(NavReconcileCache.TryReadPartial(stream, "resume", triangles, out var loaded));
        Assert.True(loaded[0]!.SetEquals(new[] { 1, 3 }));
        Assert.Null(loaded[1]);
        Assert.Empty(loaded[2]!);
        stream.Position = 0;
        Assert.False(NavReconcileCache.TryRead(stream, "resume", triangles, out _));
    }

    [Fact]
    public void PartialCheckpointBecomesACompleteHitAfterLastFlag()
    {
        var flags = new List<HashSet<int>?> { new() { 0 }, new() { 1 } };
        using var stream = new MemoryStream();
        NavReconcileCache.WritePartial(stream, "done", flags, new[] { 1, 2 });
        stream.Position = 0;
        Assert.True(NavReconcileCache.TryRead(stream, "done", new[] { 1, 2 }, out var loaded));
        Assert.Equal(2, loaded.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(12)]
    public void TruncatedOrGarbageInput_IsACacheMiss(int bytes)
    {
        byte[] garbage = new byte[bytes];
        new Random(7).NextBytes(garbage);
        Assert.False(NavReconcileCache.TryRead(
            new MemoryStream(garbage), "world", new[] { 4 }, out var result));
        Assert.Empty(result);
    }

    [Fact]
    public void WriteRejectsMismatchedFlagsAndOutOfRangeTriangles()
    {
        Assert.Throws<ArgumentException>(() => NavReconcileCache.Write(new MemoryStream(), "x",
            new List<HashSet<int>>(), new[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => NavReconcileCache.Write(new MemoryStream(), "x",
            new List<HashSet<int>> { new() { 2 } }, new[] { 2 }));
    }

    [Fact]
    public void FingerprintChangesForMapOrColliderChanges_ButNotRootLocation()
    {
        string a = TempDir();
        string b = TempDir();
        try
        {
            string levelA = Directory.CreateDirectory(Path.Combine(a, "level")).FullName;
            string cacheA = Directory.CreateDirectory(Path.Combine(a, "cache")).FullName;
            string levelB = Directory.CreateDirectory(Path.Combine(b, "level")).FullName;
            string cacheB = Directory.CreateDirectory(Path.Combine(b, "cache")).FullName;
            WriteSame(levelA, cacheA);
            WriteSame(levelB, cacheB);

            string original = NavReconcileCache.Fingerprint(levelA, cacheA);
            Assert.Equal(original, NavReconcileCache.Fingerprint(levelB, cacheB));

            File.AppendAllText(Path.Combine(levelA, "Environment", "Navigation_0.dat"), "changed");
            Assert.NotEqual(original, NavReconcileCache.Fingerprint(levelA, cacheA));
            File.WriteAllText(Path.Combine(levelA, "Environment", "Navigation_0.dat"), "nav");
            File.SetLastWriteTimeUtc(Path.Combine(levelA, "Environment", "Navigation_0.dat"),
                DateTime.UtcNow.AddSeconds(2));
            string restoredMap = NavReconcileCache.Fingerprint(levelA, cacheA);
            File.AppendAllText(Path.Combine(cacheA, "a.collider"), "changed");
            Assert.NotEqual(restoredMap, NavReconcileCache.Fingerprint(levelA, cacheA));
        }
        finally
        {
            Directory.Delete(a, true);
            Directory.Delete(b, true);
        }
    }

    [Fact]
    public void SelectedColliderFingerprint_IgnoresUnrelatedMapEntries()
    {
        string root = TempDir();
        try
        {
            string level = Directory.CreateDirectory(Path.Combine(root, "level")).FullName;
            string cache = Directory.CreateDirectory(Path.Combine(root, "cache")).FullName;
            WriteSame(level, cache);
            Guid selected = Guid.NewGuid();
            Guid unrelated = Guid.NewGuid();
            File.WriteAllText(Path.Combine(cache, selected.ToString("N") + ".collider"), "selected");
            File.WriteAllText(Path.Combine(cache, unrelated.ToString("N") + ".collider"), "other");

            string original = NavReconcileCache.Fingerprint(level, cache, new HashSet<Guid> { selected });
            File.AppendAllText(Path.Combine(cache, unrelated.ToString("N") + ".collider"), "changed");
            Assert.Equal(original,
                NavReconcileCache.Fingerprint(level, cache, new HashSet<Guid> { selected }));

            File.AppendAllText(Path.Combine(cache, selected.ToString("N") + ".collider"), "changed");
            Assert.NotEqual(original,
                NavReconcileCache.Fingerprint(level, cache, new HashSet<Guid> { selected }));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MissingRootsStillProduceAStableFingerprintAndMapKeysArePathSpecific()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Assert.Equal(NavReconcileCache.Fingerprint(root, root), NavReconcileCache.Fingerprint(root, root));
        Assert.NotEqual(NavReconcileCache.MapKey(root), NavReconcileCache.MapKey(root + "-other"));
        Assert.NotEqual(NavReconcileCache.WithStepOffset("same", 0.5f),
            NavReconcileCache.WithStepOffset("same", 0.6f));
        Assert.Equal(NavReconcileCache.WithStepOffset("same", 0.5f),
            NavReconcileCache.WithStepOffset("same", 0.5f));
    }

    private static MemoryStream ValidCache()
    {
        var stream = new MemoryStream();
        NavReconcileCache.Write(stream, "world", new List<HashSet<int>> { new() { 1 } }, new[] { 4 });
        stream.Position = 0;
        return stream;
    }

    private static string TempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), "nav-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteSame(string level, string cache)
    {
        string environment = Directory.CreateDirectory(Path.Combine(level, "Environment")).FullName;
        File.WriteAllText(Path.Combine(environment, "Navigation_0.dat"), "nav");
        File.WriteAllText(Path.Combine(cache, "a.collider"), "collider");
        // Fingerprints intentionally include timestamps; align the two synthetic copies exactly.
        DateTime stamp = new(638900000000000000, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(environment, "Navigation_0.dat"), stamp);
        File.SetLastWriteTimeUtc(Path.Combine(cache, "a.collider"), stamp);
    }
}
