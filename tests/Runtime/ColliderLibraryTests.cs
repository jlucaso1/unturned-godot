using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Loading the per-GUID collider caches the extractor wrote, so the object build never touches the
// masterbundle again.
//
// The interesting behaviour here is what happens when a cache file is WRONG. Before it was guarded, an
// unreadable file faulted the whole object build and dumped the player back to the menu — permanently,
// because nothing invalidated the bad file, so the next load hit it again. That is why the failure path,
// not the happy path, is most of what is tested below.
//
// It is in this suite rather than the xUnit one because CachedCollider is built out of Transform3D and
// Vector3, which are Godot types.
public class ColliderLibraryTests : TestClass
{
    public ColliderLibraryTests(Node testScene) : base(testScene) { }

    // A cache directory that does not exist yet is an empty result, not a throw: the first run of a map
    // reaches here before anything has been extracted.
    [Test]
    public void AMissingCacheDirectoryLoadsNothing()
    {
        Assert.Empty(ColliderLibrary.Load(Path.Combine(Path.GetTempPath(), "unturned-godot-no-such-dir")));
    }

    // The whole directory is read when no filter is given, and files that are not GUID-named are ignored
    // rather than tripping over the parse.
    [Test]
    public void TheWholeDirectoryLoadsWhenNothingIsAskedForSpecifically()
    {
        using var dir = new TempDir();
        Guid first = Guid.NewGuid(), second = Guid.NewGuid();
        WriteColliders(dir.Path, first, 1);
        WriteColliders(dir.Path, second, 2);
        File.WriteAllText(Path.Combine(dir.Path, "not-a-guid.collider"), "junk");

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.Equal(2, loaded.Count);
        Assert.Single(loaded[first]);
        Assert.Equal(2, loaded[second].Count);
    }

    // Addressing the cache by name is the point of the filter: the cache is shared between every map, and
    // scanning it would read and build the collision shapes of maps nobody loaded.
    [Test]
    public void AskingForSpecificGuidsSkipsEverythingElse()
    {
        using var dir = new TempDir();
        Guid wanted = Guid.NewGuid(), other = Guid.NewGuid();
        WriteColliders(dir.Path, wanted, 1);
        WriteColliders(dir.Path, other, 1);

        Dictionary<Guid, List<CachedCollider>> loaded =
            ColliderLibrary.Load(dir.Path, new HashSet<Guid> { wanted });

        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey(wanted));
    }

    // An empty GUID is not an asset, and a GUID with no cache file yet is simply absent — neither is an
    // error, because extraction may still be running.
    [Test]
    public void AnEmptyOrUnextractedGuidIsQuietlyAbsent()
    {
        using var dir = new TempDir();
        Guid real = Guid.NewGuid();
        WriteColliders(dir.Path, real, 1);

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(
            dir.Path, new HashSet<Guid> { real, Guid.Empty, Guid.NewGuid() });

        Assert.Single(loaded);
        Assert.True(loaded.ContainsKey(real));
    }

    // The failure this is really about: a corrupt cache file must cost that one GUID its collision for
    // this run, not take the map down. Before the guard, this faulted the whole object build.
    [Test]
    public void ACorruptCacheFileCostsOneGuidItsCollisionAndNotTheMap()
    {
        using var dir = new TempDir();
        Guid good = Guid.NewGuid(), corrupt = Guid.NewGuid();
        WriteColliders(dir.Path, good, 1);
        // Right magic, wrong body: the header check passes and the read is what discovers the damage.
        WriteCorrupt(dir.Path, corrupt);

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.True(loaded.ContainsKey(good), "one bad file took the good ones with it");
        Assert.False(loaded.ContainsKey(corrupt));
    }

    // And it invalidates, which is the other half. A header check cannot see corruption that leaves the
    // length intact, so without this the GUID would be skipped on every future load with nothing ever
    // rewriting it — collision permanently missing, silently.
    [Test]
    public void ACorruptFileIsMarkedForReExtractionRatherThanSkippedForever()
    {
        using var dir = new TempDir();
        Guid corrupt = Guid.NewGuid();
        WriteCorrupt(dir.Path, corrupt);
        string path = Path.Combine(dir.Path, corrupt.ToString("N") + ".collider");
        Assert.True(File.Exists(path));

        ColliderLibrary.Load(dir.Path);

        Assert.False(File.Exists(path),
            "the unreadable cache file survived the load, so nothing would ever re-extract it");
    }

    // Identical collider payloads share one list. Hundreds of placements of the same prefab resolve to
    // the same shapes, and holding a copy per GUID is the difference between one shape and hundreds.
    [Test]
    public void IdenticalCachesShareOneLoadedList()
    {
        using var dir = new TempDir();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        byte[] payload = ColliderBytes(2);
        File.WriteAllBytes(Path.Combine(dir.Path, a.ToString("N") + ".collider"), payload);
        File.WriteAllBytes(Path.Combine(dir.Path, b.ToString("N") + ".collider"), payload);

        Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);

        Assert.Equal(2, loaded.Count);
        Assert.Same(loaded[a], loaded[b]);
    }

    // Deduplication can be switched off, and then each GUID gets its own list. Worth pinning because the
    // sharing above is a real aliasing decision, not an implementation detail — a caller that mutated one
    // list would otherwise be mutating every prefab that happens to match.
    [Test]
    public void DeduplicationCanBeSwitchedOff()
    {
        using var dir = new TempDir();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        byte[] payload = ColliderBytes(2);
        File.WriteAllBytes(Path.Combine(dir.Path, a.ToString("N") + ".collider"), payload);
        File.WriteAllBytes(Path.Combine(dir.Path, b.ToString("N") + ".collider"), payload);

        string previous = System.Environment.GetEnvironmentVariable("UG_DEDUP_COLLIDERS") ?? "";
        System.Environment.SetEnvironmentVariable("UG_DEDUP_COLLIDERS", "0");
        try
        {
            Dictionary<Guid, List<CachedCollider>> loaded = ColliderLibrary.Load(dir.Path);
            Assert.NotSame(loaded[a], loaded[b]);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("UG_DEDUP_COLLIDERS",
                previous.Length == 0 ? null : previous);
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static void WriteColliders(string dir, Guid guid, int count)
    {
        File.WriteAllBytes(Path.Combine(dir, guid.ToString("N") + ".collider"), ColliderBytes(count));
    }

    private static byte[] ColliderBytes(int count)
    {
        var colliders = new List<CachedCollider>(count);
        for (int i = 0; i < count; i++)
            colliders.Add(CachedCollider.Box(Transform3D.Identity, Vector3.Zero, Vector3.One * (i + 1)));
        using var ms = new MemoryStream();
        ColliderCache.Write(ms, colliders);
        return ms.ToArray();
    }

    // A file whose header says one thing and whose body says another: it passes any length or magic
    // check and only fails once something actually parses it, which is the case the guard exists for.
    private static void WriteCorrupt(string dir, Guid guid)
    {
        byte[] sound = ColliderBytes(4);
        // Keep the header, replace the payload with something that cannot decode into four colliders.
        for (int i = 8; i < sound.Length; i++)
            sound[i] = 0xFF;
        File.WriteAllBytes(Path.Combine(dir, guid.ToString("N") + ".collider"), sound);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-colliders-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
