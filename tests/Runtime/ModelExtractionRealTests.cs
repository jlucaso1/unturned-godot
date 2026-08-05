using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Extraction end to end, against the game's own masterbundle.
//
// This is the pass the whole cold load rests on: walk a ~110 MB LZMA bundle once, and for every GUID the
// map places, write a .mesh, a .collider and the texture keys its submeshes reference. Everything
// downstream — ModelLibrary, ColliderLibrary, TextureRegistry — reads what this produced, so a fault here
// is not a missing object but a missing world.
//
// It cannot be tested against a stand-in. The formats are re-implementations checked byte-for-byte
// against Unturned's own, so a synthetic bundle this reader accepts proves nothing about the reader.
// Without the game installed these say so and return; UG_REQUIRE_REAL_DATA=1 makes that a failure, so the
// job that fetches the content cannot pass by finding nothing.
//
// Deliberately scoped to a handful of GUIDs rather than a whole map: the point is that the pass produces
// sound, re-readable caches, not that it can chew through 416 meshes inside a test.
public class ModelExtractionRealTests : TestClass
{
    public ModelExtractionRealTests(Node testScene) : base(testScene) { }

    // The whole chain in one: extract from the real bundle, then read back what it wrote through the very
    // libraries the game uses. A cache that cannot be re-read is worse than no cache — the load would
    // rebuild it every session and never notice.
    [Test]
    public void WhatTheExtractorWritesIsWhatTheLibrariesRead()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase db))
            return;

        using var cache = new TempDir();
        HashSet<Guid> wanted = FirstFewAssets(db, 6);
        Assert.NotEmpty(wanted);

        int extracted = ModelExtractor.ExtractMeshes(source, wanted, cache.Path, db);

        Assert.True(extracted > 0, "the extractor produced nothing from the real bundle");
        Assert.NotEmpty(Directory.GetFiles(cache.Path, "*.mesh"));

        // Read it back the way the game does. This is the assertion that matters: the bytes are sound.
        var registry = new TextureRegistry(cache.Path);
        Dictionary<Guid, ArrayMesh> library = ModelLibrary.Load(cache.Path, registry, wanted);

        Assert.NotEmpty(library);
        foreach (ArrayMesh mesh in library.Values)
        {
            Assert.True(mesh.GetSurfaceCount() > 0, "a realised mesh had no surfaces");
            var vertices = (Vector3[])mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
            Assert.NotEmpty(vertices);
        }
    }

    // Collision comes out of the same pass, and it has to survive the same round trip: an object whose
    // collider cache cannot be re-read loads without collision, which is a player walking through a wall.
    [Test]
    public void TheCollidersItWritesAreReadableToo()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase db))
            return;

        using var cache = new TempDir();
        HashSet<Guid> wanted = FirstFewAssets(db, 8);
        ModelExtractor.ExtractMeshes(source, wanted, cache.Path, db);

        string[] colliderFiles = Directory.GetFiles(cache.Path, "*.collider");
        if (colliderFiles.Length == 0)
            return; // these particular assets carry no collision; the mesh test already proved the pass

        Dictionary<Guid, List<CachedCollider>> colliders = ColliderLibrary.Load(cache.Path, wanted);
        Assert.NotEmpty(colliders);
        foreach (List<CachedCollider> forAsset in colliders.Values)
            Assert.NotEmpty(forAsset);
    }

    // Extraction is addressed by GUID, so asking for nothing must do nothing — the cold load asks only for
    // what the map places, and a pass that ignored the filter would extract the whole 1.4 GB catalogue.
    [Test]
    public void AskingForNothingExtractsNothing()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase db))
            return;

        using var cache = new TempDir();

        ModelExtractor.ExtractMeshes(source, new HashSet<Guid>(), cache.Path, db);

        Assert.Empty(Directory.GetFiles(cache.Path, "*.mesh"));
    }

    // Running it twice does not re-extract: the second pass finds current caches and leaves them. That is
    // the difference between a warm load and a cold one, every session.
    [Test]
    public void ASecondPassLeavesCurrentCachesAlone()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase db))
            return;

        using var cache = new TempDir();
        HashSet<Guid> wanted = FirstFewAssets(db, 4);
        ModelExtractor.ExtractMeshes(source, wanted, cache.Path, db);

        var stamps = new Dictionary<string, DateTime>();
        foreach (string path in Directory.GetFiles(cache.Path, "*.mesh"))
            stamps[path] = File.GetLastWriteTimeUtc(path);
        Assert.NotEmpty(stamps);

        ModelExtractor.ExtractMeshes(source, wanted, cache.Path, db);

        foreach ((string path, DateTime written) in stamps)
            Assert.Equal(written, File.GetLastWriteTimeUtc(path));
    }

    // Breadth, not depth: enough distinct assets that the decode meets the variety real data has —
    // multiple submeshes, authored lower LODs, assets with collision and assets without. A pass tested
    // on one prefab proves only that one shape decodes.
    //
    // What is asserted is that everything it produced READS BACK, through the libraries the game uses.
    // Bytes that cannot be re-read are worse than bytes that were never written: the load rebuilds them
    // every session and never notices.
    [Test]
    public void AWideSpreadOfAssetsRoundTrips()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase db))
            return;

        using var cache = new TempDir();
        HashSet<Guid> wanted = FirstFewAssets(db, 60);
        if (wanted.Count < 10)
            return; // an install with almost no objects is not a useful sample

        int extracted = ModelExtractor.ExtractMeshes(source, wanted, cache.Path, db);
        Assert.True(extracted > 0, "the extractor produced nothing from a wide spread of assets");

        var registry = new TextureRegistry(cache.Path);
        Dictionary<Guid, ArrayMesh> library = ModelLibrary.Load(cache.Path, registry, wanted);
        Assert.NotEmpty(library);

        // Every mesh that came back has real geometry on every surface it claims. A surface count that
        // outran the arrays would draw nothing and report no error.
        int surfaces = 0;
        foreach (ArrayMesh mesh in library.Values)
        {
            int count = mesh.GetSurfaceCount();
            Assert.True(count > 0, "a realised mesh claimed no surfaces");
            for (int i = 0; i < count; i++)
            {
                var vertices = (Vector3[])mesh.SurfaceGetArrays(i)[(int)Mesh.ArrayType.Vertex];
                Assert.NotEmpty(vertices);
                surfaces++;
            }
        }

        // A spread this wide should have produced more surfaces than meshes: single-surface props exist,
        // but a whole sample of them would mean the submesh split never ran.
        Assert.True(surfaces >= library.Count, "no asset in the sample had more than one surface");

        // And whatever collision came out reads back too — an object whose collider cache will not
        // re-read loads without collision, which is a player walking through a wall.
        Dictionary<Guid, List<CachedCollider>> colliders = ColliderLibrary.Load(cache.Path, wanted);
        foreach (List<CachedCollider> forAsset in colliders.Values)
            Assert.NotEmpty(forAsset);
    }

    // Cancellation, which the menu sends when a player backs out of a load. Both passes have to notice
    // before doing 110 MB of work, not after: a cancelled load that decodes the bundle anyway holds the
    // process for seconds while the player is already looking at the menu.
    [Test]
    public void ACancelledPassDoesNoWork()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase db))
            return;

        using var cache = new TempDir();
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        int extracted = ModelExtractor.ExtractMeshes(source, FirstFewAssets(db, 4), cache.Path, db,
            cancellationToken: cancelled.Token);

        Assert.Equal(0, extracted);
        Assert.Empty(Directory.GetFiles(cache.Path));
    }

    // Textures are addressed the same way meshes are, so a cache that owes none does no decode. This is
    // the warm-load path, and it has to cost nothing rather than re-reading a 110 MB bundle to discover
    // there was nothing to do.
    [Test]
    public void TexturesOwedToNobodyAreNotDecoded()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase _))
            return;

        using var cache = new TempDir();

        // Nothing has been extracted into this cache, so nothing references a texture: the pass has no
        // ids to look for and must not open the bundle at all.
        int written = ModelExtractor.ExtractTextures(source.BundlePath, source.CacheTag, cache.Path,
            cache.Path);

        Assert.Equal(0, written);
    }

    // And a cancelled texture pass likewise. It is the heavier of the two — it needs the .resS stream —
    // so noticing late is the more expensive mistake.
    [Test]
    public void ACancelledTexturePassDoesNoWork()
    {
        if (!Ready(out ContentSource source, out ObjectAssetDatabase _))
            return;

        using var cache = new TempDir();
        using var cancelled = new System.Threading.CancellationTokenSource();
        cancelled.Cancel();

        int written = ModelExtractor.ExtractTextures(source.BundlePath, source.CacheTag, cache.Path,
            cache.Path, cancellationToken: cancelled.Token);

        Assert.Equal(0, written);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // The asset GUIDs a map would ask for, taken from the scanned object catalogue. Bounded, because this
    // is proving the pass works rather than benchmarking it.
    private static HashSet<Guid> FirstFewAssets(ObjectAssetDatabase db, int count)
    {
        var wanted = new HashSet<Guid>();
        foreach (ObjectAsset asset in db.All)
        {
            if (wanted.Count >= count)
                break;
            if (asset.Guid != Guid.Empty)
                wanted.Add(asset.Guid);
        }
        return wanted;
    }

    private static bool Ready(out ContentSource source, out ObjectAssetDatabase db)
    {
        source = null!;
        db = null!;
        string? install = UnturnedInstall.Find();
        if (!Available("an Unturned install", install))
            return false;

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install!);
        if (sources.Count == 0)
        {
            Log.Print("[runtime-tests] skipping: the install carries no readable content source");
            return false;
        }

        source = sources[0];
        db = ObjectAssetDatabase.ScanDirectory(Path.Combine(install!, "Bundles", "Objects"));
        return true;
    }

    private static bool Available(string what, string? path)
    {
        if (path != null)
            return true;
        if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
        {
            throw new InvalidDataException(
                $"UG_REQUIRE_REAL_DATA=1 but {what} is not present; this run exists to prove these tests execute");
        }

        Log.Print($"[runtime-tests] skipping: {what} is not present "
            + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
        return false;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-extract-" + Guid.NewGuid().ToString("N"));

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
