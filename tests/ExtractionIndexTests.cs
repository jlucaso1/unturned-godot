using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

public sealed class ExtractionIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "ugx-" + Guid.NewGuid().ToString("N"));

    public ExtractionIndexTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort temp cleanup */ }
    }

    private string IndexPath => Path.Combine(_dir, "extraction.index");

    // Writes a mesh-cache file whose header is the current format, so IsCurrent accepts it.
    private void WriteCachedMesh(Guid guid)
    {
        using FileStream s = File.Create(Path.Combine(_dir, guid.ToString("N") + ".mesh"));
        MeshCache.Write(s, Array.Empty<Godot.Vector3>(), Array.Empty<Godot.Vector3>(),
            Array.Empty<Godot.Vector2>(), Array.Empty<CachedSubmesh>());
    }

    [Fact]
    public void RoundTripsMissesUnderTheSameStamp()
    {
        var misses = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() };
        ExtractionIndex.Save(IndexPath, stamp: 1234, misses);

        Assert.Equal(misses, ExtractionIndex.Load(IndexPath, stamp: 1234));
    }

    [Fact]
    public void DropsMissesRecordedForADifferentBundle()
    {
        ExtractionIndex.Save(IndexPath, stamp: 1234, new HashSet<Guid> { Guid.NewGuid() });

        // A game update changes the masterbundle's mtime/size: what the old bundle had no mesh for says
        // nothing about the new one, so every GUID must be tried again.
        Assert.Empty(ExtractionIndex.Load(IndexPath, stamp: 5678));
    }

    [Fact]
    public void LoadReturnsEmptyForMissingOrGarbageFile()
    {
        Assert.Empty(ExtractionIndex.Load(IndexPath, stamp: 1));

        File.WriteAllBytes(IndexPath, new byte[] { 1, 2, 3 });
        Assert.Empty(ExtractionIndex.Load(IndexPath, stamp: 1));

        File.WriteAllText(IndexPath, "not an index, but long enough to pass the length check");
        Assert.Empty(ExtractionIndex.Load(IndexPath, stamp: 1));
    }

    [Fact]
    public void LoadIgnoresATruncatedIndexInsteadOfThrowing()
    {
        ExtractionIndex.Save(IndexPath, stamp: 7, new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid() });
        byte[] full = File.ReadAllBytes(IndexPath);
        File.WriteAllBytes(IndexPath, full[..(full.Length - 8)]); // half a GUID short

        Assert.Empty(ExtractionIndex.Load(IndexPath, stamp: 7));
    }

    // Colliders are written in the same pass as the mesh, so a current mesh beside a stale or truncated
    // collider means the extraction was interrupted. Without this the GUID stayed "complete" forever and
    // loaded with no collision, because nothing else ever rewrites that file.
    [Fact]
    public void AStaleColliderMakesItsMeshCountAsMissing()
    {
        Guid guid = Guid.NewGuid();
        WriteCachedMesh(guid);
        Assert.Empty(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>()));

        // The header-less format the old writer produced, and what a kill mid-write leaves behind.
        File.WriteAllBytes(Path.Combine(_dir, guid.ToString("N") + ".collider"), new byte[] { 1, 0, 0, 0 });

        Assert.Single(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>()));
    }

    [Fact]
    public void AMeshWithNoColliderFileAtAllIsStillComplete()
    {
        // Most prefabs have no colliders, so no file is written for them. Absence is not corruption.
        Guid guid = Guid.NewGuid();
        WriteCachedMesh(guid);

        Assert.Empty(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>()));
    }

    [Fact]
    public void AWellFormedColliderKeepsItsMeshComplete()
    {
        Guid guid = Guid.NewGuid();
        WriteCachedMesh(guid);
        using (FileStream s = File.Create(Path.Combine(_dir, guid.ToString("N") + ".collider")))
            ColliderCache.Write(s, new List<CachedCollider>
            {
                CachedCollider.Sphere(Godot.Transform3D.Identity, Godot.Vector3.Zero, 1f),
            });

        Assert.Empty(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>()));
    }

    [Fact]
    public void MissingMeshesReportsOnlyWhatIsNeitherCachedNorAKnownMiss()
    {
        Guid cached = Guid.NewGuid();
        Guid known = Guid.NewGuid();     // tried before: the bundle has no mesh for it
        Guid fresh = Guid.NewGuid();     // a second map's object, never extracted
        WriteCachedMesh(cached);

        HashSet<Guid> missing = ExtractionIndex.MissingMeshes(_dir,
            new[] { cached, known, fresh, Guid.Empty }, new HashSet<Guid> { known });

        Assert.Equal(new HashSet<Guid> { fresh }, missing);
    }

    // The regression this whole index exists for: after extracting map A, opening map B must still report
    // B's own objects as missing even though the shared cache is full of A's meshes.
    [Fact]
    public void ASecondMapIsStillColdAgainstAFullCache()
    {
        var mapA = new HashSet<Guid> { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (Guid guid in mapA)
            WriteCachedMesh(guid);
        ExtractionIndex.Save(IndexPath, stamp: 42, new HashSet<Guid>());

        Assert.Empty(ExtractionIndex.MissingMeshes(_dir, mapA, ExtractionIndex.Load(IndexPath, 42)));

        var mapB = new HashSet<Guid>(mapA) { Guid.NewGuid() }; // shares A's objects, adds one of its own
        Assert.Single(ExtractionIndex.MissingMeshes(_dir, mapB, ExtractionIndex.Load(IndexPath, 42)));
    }

    [Fact]
    public void StaleFormatMeshCountsAsMissing()
    {
        Guid guid = Guid.NewGuid();
        File.WriteAllBytes(Path.Combine(_dir, guid.ToString("N") + ".mesh"),
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 }); // an older cache format's magic

        Assert.Single(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>()));
    }

    [Fact]
    public void SourceStampRejectsACurrentMeshOwnedByAnotherBundle()
    {
        Guid guid = Guid.NewGuid();
        WriteCachedMesh(guid);
        string first = Path.Combine(_dir, "first.masterbundle");
        string second = Path.Combine(_dir, "second.masterbundle");
        File.WriteAllBytes(first, new byte[11]);
        File.WriteAllBytes(second, new byte[22]);
        long firstStamp = ExtractionIndex.StampFor(first);
        long secondStamp = ExtractionIndex.StampFor(second);
        ExtractionIndex.RecordMeshOwner(_dir, guid, first, firstStamp);

        Assert.Empty(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>(),
            first, firstStamp));
        Assert.Single(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>(),
            second, secondStamp));

        ExtractionIndex.RecordMeshOwner(_dir, guid, second, secondStamp);
        Assert.Single(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>(),
            first, firstStamp));
        Assert.Empty(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>(),
            second, secondStamp));
    }

    [Fact]
    public void SourceStampInvalidatesWhenTheOwningBundleChanges()
    {
        Guid guid = Guid.NewGuid();
        WriteCachedMesh(guid);
        string bundle = Path.Combine(_dir, "mod.masterbundle");
        File.WriteAllBytes(bundle, new byte[8]);
        long oldStamp = ExtractionIndex.StampFor(bundle);
        ExtractionIndex.RecordMeshOwner(_dir, guid, bundle, oldStamp);

        File.WriteAllBytes(bundle, new byte[64]);
        long newStamp = ExtractionIndex.StampFor(bundle);

        Assert.Single(ExtractionIndex.MissingMeshes(_dir, new[] { guid }, new HashSet<Guid>(),
            bundle, newStamp));
    }

    [Fact]
    public void RemovingAFailedAssetClearsMeshColliderAndOwner()
    {
        Guid guid = Guid.NewGuid();
        WriteCachedMesh(guid);
        string bundle = Path.Combine(_dir, "mod.masterbundle");
        File.WriteAllBytes(bundle, new byte[8]);
        ExtractionIndex.RecordMeshOwner(_dir, guid, bundle, ExtractionIndex.StampFor(bundle));
        File.WriteAllBytes(Path.Combine(_dir, guid.ToString("N") + ".collider"), new byte[] { 1 });

        ExtractionIndex.RemoveCachedAsset(_dir, guid);

        Assert.False(File.Exists(Path.Combine(_dir, guid.ToString("N") + ".mesh")));
        Assert.False(File.Exists(Path.Combine(_dir, guid.ToString("N") + ".collider")));
        Assert.False(ExtractionIndex.MeshBelongsTo(_dir, guid, bundle, ExtractionIndex.StampFor(bundle)));
    }

    // An unreadable path must not take the caller down: the stamp only decides whether cached misses
    // are still valid, and losing it costs one extra extraction pass, never a crash mid-load.
    [Fact]
    public void StampFor_UnreadablePath_ReturnsZeroInsteadOfThrowing()
    {
        // Longer than any filesystem allows, so the FileInfo lookup fails at the OS level.
        string tooLong = Path.Combine(_dir, new string('x', 500), new string('y', 500));
        Assert.Equal(0, ExtractionIndex.StampFor(tooLong));
    }

    [Fact]
    public void StampChangesWithTheBundleAndIsZeroWhenAbsent()
    {
        string bundle = Path.Combine(_dir, "core.masterbundle");
        Assert.Equal(0, ExtractionIndex.StampFor(bundle));

        File.WriteAllBytes(bundle, new byte[16]);
        long first = ExtractionIndex.StampFor(bundle);

        File.WriteAllBytes(bundle, new byte[32]); // a game update: different size
        Assert.NotEqual(first, ExtractionIndex.StampFor(bundle));
    }
}
