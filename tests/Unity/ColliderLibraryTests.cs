using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests.Unity;

// Loading the per-GUID collider caches back into memory, which is what lets an object build its collision
// body without opening the masterbundle again.
//
// Two behaviours carry the weight. The first is addressing by name: a cache is shared between every map
// that has ever been loaded, so a map that reads all of it pays for every other map's shapes. The second
// is what happens to a file that is stale, truncated or unreadable — before this was guarded, one bad
// file faulted the whole object build and dumped the player back to the menu, permanently, because
// nothing invalidated it.
[Collection(ProcessStateCollection.Name)]
public class ColliderLibraryTests
{
    private static readonly Transform3D Pose = Transform3D.Identity;

    private static List<CachedCollider> OneBox() => new()
    {
        CachedCollider.Box(Pose, Vector3.Zero, new Vector3(1, 2, 3)),
    };

    private static Guid Write(TempDir dir, List<CachedCollider> colliders)
    {
        var guid = Guid.NewGuid();
        WriteAs(dir, guid, colliders);
        return guid;
    }

    private static void WriteAs(TempDir dir, Guid guid, List<CachedCollider> colliders)
    {
        using var buffer = new MemoryStream();
        ColliderCache.Write(buffer, colliders);
        dir.Write(guid.ToString("N") + ".collider", buffer.ToArray());
    }

    [Fact]
    public void ADirectoryThatDoesNotExistLoadsNothingRatherThanThrowing()
    {
        Assert.Empty(ColliderLibrary.Load(
            Path.Combine(Path.GetTempPath(), "unturned-godot-no-such-dir-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void EveryCacheFileIsLoadedWhenNothingNarrowsIt()
    {
        using var dir = new TempDir();
        Guid first = Write(dir, OneBox());
        Guid second = Write(dir, OneBox());

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.Equal(2, loaded.Count);
        Assert.Single(loaded[first]);
        Assert.Single(loaded[second]);
    }

    // The whole point of `only`: a cache shared with every other map must be addressed by name, not
    // scanned. A map that asks for one GUID must not read the other's shapes at all.
    [Fact]
    public void OnlyTheAskedForGuidsAreRead()
    {
        using var dir = new TempDir();
        Guid wanted = Write(dir, OneBox());
        Guid other = Write(dir, OneBox());

        Dictionary<Guid, List<CachedCollider>> loaded =
            ColliderLibrary.Load(dir.Path, new HashSet<Guid> { wanted });

        Assert.Equal(new[] { wanted }, loaded.Keys);
        Assert.DoesNotContain(other, loaded.Keys);
    }

    // An empty GUID is not an asset, and a cache file can never be named after one. Asking for it must
    // not build a path and stat it, and must not land in the result.
    [Fact]
    public void TheEmptyGuidIsSkipped()
    {
        using var dir = new TempDir();
        Guid wanted = Write(dir, OneBox());

        Dictionary<Guid, List<CachedCollider>> loaded =
            ColliderLibrary.Load(dir.Path, new HashSet<Guid> { wanted, Guid.Empty });

        Assert.Equal(new[] { wanted }, loaded.Keys);
    }

    // A GUID the map places but the cache has never held is simply absent: it extracts on this run and is
    // there next time. Reporting it as an error would fire on every cold load.
    [Fact]
    public void AGuidWithNoCacheFileIsAbsentRatherThanAnError()
    {
        using var dir = new TempDir();

        Assert.Empty(ColliderLibrary.Load(dir.Path, new HashSet<Guid> { Guid.NewGuid() }));
    }

    // Files in the directory that are not caches, or are named after something that is not a GUID, are
    // not ours. The cache directory holds meshes and textures too.
    [Fact]
    public void FilesThatAreNotColliderCachesAreIgnored()
    {
        using var dir = new TempDir();
        Guid wanted = Write(dir, OneBox());
        dir.Write("notaguid.collider", new byte[] { 1, 2, 3 });
        dir.Write(Guid.NewGuid().ToString("N") + ".mesh", new byte[] { 1, 2, 3 });

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.Equal(new[] { wanted }, loaded.Keys);
    }

    // Two GUIDs whose cache files are byte-identical share ONE parsed list, so the shape pool downstream
    // keys them together instead of building the same bodies twice.
    [Fact]
    public void IdenticalCacheFilesShareOneParsedList()
    {
        using var dir = new TempDir();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        List<CachedCollider> colliders = OneBox();
        WriteAs(dir, first, colliders);
        WriteAs(dir, second, colliders);

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.Equal(2, loaded.Count);
        Assert.Same(loaded[first], loaded[second]);
    }

    // A corrupt file costs its own GUID and nothing else: the rest of the map still gets its collision.
    [Fact]
    public void AnUnreadableCacheFileCostsOnlyItsOwnGuid()
    {
        using var dir = new TempDir();
        Guid good = Write(dir, OneBox());
        var bad = Guid.NewGuid();
        dir.Write(bad.ToString("N") + ".collider", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 });

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.Equal(new[] { good }, loaded.Keys);
    }

    // Reading is the only thing that knows a payload is bad — the completeness check is a header test, so
    // corruption that leaves the length intact still reads as current. So the read is what marks the asset
    // for re-extraction, and without it the GUID would be skipped on every later load with nothing
    // rewriting it.
    [Fact]
    public void AnUnreadableCacheFileIsMarkedForReExtraction()
    {
        using var dir = new TempDir();
        var bad = Guid.NewGuid();
        string path = dir.Write(bad.ToString("N") + ".collider",
            new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0, 0, 0, 0 });

        ColliderLibrary.Load(dir.Path);

        Assert.False(File.Exists(path), "the unreadable cache file was left in place to fail again");
    }

    // ...and it says so. A run that quietly loaded a hundred objects without collision would look like a
    // physics bug rather than a cache one.
    [Fact]
    public void DroppedCacheEntriesAreReportedToTheHost()
    {
        var log = new RecordingHostLog();
        IHostLog previous = HostLog.Sink;
        HostLog.Sink = log;
        try
        {
            using var dir = new TempDir();
            dir.Write(Guid.NewGuid().ToString("N") + ".collider", new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

            ColliderLibrary.Load(dir.Path);
        }
        finally
        {
            HostLog.Sink = previous;
        }

        Assert.Contains(log.Warnings, line => line.Contains("[colliders] dropped 1", StringComparison.Ordinal));
    }

    // Nothing to report when nothing was dropped: a clean load is silent.
    [Fact]
    public void ACleanLoadSaysNothing()
    {
        var log = new RecordingHostLog();
        IHostLog previous = HostLog.Sink;
        HostLog.Sink = log;
        try
        {
            using var dir = new TempDir();
            Write(dir, OneBox());

            ColliderLibrary.Load(dir.Path);
        }
        finally
        {
            HostLog.Sink = previous;
        }

        Assert.Empty(log.Warnings);
    }
}
