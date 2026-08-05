using System;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The cold load, end to end, over the real game's PEI — into a cache directory the test owns.
//
// Everything else in this suite pins one piece. This one runs the whole thing: the bundle decodes on a
// worker, the meshes and colliders come back, the scene is built from them, the textures are paced
// across frames, and the world reports itself finished. It is the only test that proves those pieces
// still fit together, because every seam between them is a handoff between a worker thread, the physics
// thread and the frame loop — and a handoff that broke would leave a load that never ends rather than a
// load that fails.
//
// "Cold" is the point. A warm cache reads meshes back and never opens a bundle, so a run over the
// machine's own cache would exercise almost none of the extraction. Pointing the caches at a fresh
// temporary directory forces the first-time path every player takes exactly once, and keeps the test
// from writing into the caches a real session depends on.
//
// It is slow by nature — a real decode of real content — and it is skipped without the game installed;
// UG_REQUIRE_REAL_DATA=1 turns that skip into a failure, so the job that fetches the content cannot pass
// by finding nothing.
public class ColdLoadTests : TestClass
{
    public ColdLoadTests(Node testScene) : base(testScene) { }

    // A whole map, from bundles nobody has decoded yet to a world that says it is finished.
    //
    // The assertions are deliberately about the SHAPE of the result rather than its contents: which
    // objects PEI places is the map's business and a game update may change it, but that a cold load
    // produces meshes, a damageable world and a completed promise is this code's business and must not
    // change without someone deciding it should.
    // The runner's default per-test timeout is measured in seconds, which is right for every other test
    // here and wrong for this one: a real decode of real content takes minutes on a cold cache.
    [Test]
    [Timeout(600_000)]
    public async Task AColdLoadOfTheRealMapBuildsAWorld()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        using var cache = new TempDir();
        var streamer = new ObjectStreamer
        {
            Name = "ObjectStreamer",
            CacheDirOverride = cache.Path,
            TextureCacheDirOverride = cache.Path,
        };
        // Captured AT the signal, because the streamer releases them immediately afterwards: they are
        // held only so Finished's subscribers can fingerprint the map, and on a large map that set is
        // tens of thousands of entries nobody needs for the rest of the session.
        int neededAtFinish = -1;
        streamer.Finished += () => neededAtFinish = streamer.NeededGuids.Count;
        TestScene.AddChild(streamer);

        try
        {
            streamer.StartPrepare(install, level);

            // Begin only awaits the PLANNING. The decode runs on a worker afterwards and the scene is
            // built on the frame its meshes land, so the load is over when Completion is — and both of
            // those are driven by the frame loop, which is why the wait has to BE the frame loop rather
            // than a sleep.
            await streamer.BeginAsync();
            if (!await Within(ColdLoadBudget, streamer.Completion))
            {
                Assert.Fail("the cold load never finished; a real one has no longer to wait than this");
                return;
            }

            // The terrain build awaits this promise. Left unfinished it hangs the loading screen for
            // good, before anything can report why.
            Assert.True(streamer.LayerTextures.IsCompleted,
                "the terrain build was left waiting on a finished load");

            // A cold load that decoded nothing is a load that silently produced an empty map.
            Assert.True(neededAtFinish > 0,
                $"the load finished having selected {neededAtFinish} assets to build from");

            // And the ledger a punch resolves against exists, because the same pass that built the
            // bodies is the pass that recorded what they break into.
            Assert.NotNull(streamer.Damageable);

            // MESHES, not merely files. An extraction index or a texture entry satisfies "the directory
            // is not empty", and ObjectsBuilder will happily finish a scene out of placeholder boxes when
            // ModelLibrary finds nothing — so the load can report a full set of selected GUIDs, a
            // damage ledger and a populated directory while having decoded no geometry at all.
            Assert.NotEmpty(System.IO.Directory.GetFiles(cache.Path, "*.mesh",
                System.IO.SearchOption.AllDirectories));
        }
        finally
        {
            await streamer.CancelAsync();
            streamer.QueueFree();
        }
    }

    // And the SECOND load over the same cache, which is a different program.
    //
    // A warm load opens no bundle at all: the meshes, colliders and textures are read straight back from
    // the cache the first one wrote, and the scene is built from those. That is the load every session
    // after the first one runs, and it has its own way of going wrong — a cache written in one format and
    // read in another, an entry that is present but unreadable, a build that starts before the read
    // finished. None of it is reachable from a cold run, because a cold run never takes that path.
    //
    // The two are driven back to back deliberately: a warm load over a cache some OTHER run wrote would
    // prove only that the reader agrees with whatever happens to be on that machine.
    [Test]
    [Timeout(600_000)]
    public async Task TheSecondLoadOverTheSameCacheIsWarm()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        using var cache = new TempDir();

        // The cold pass, purely to fill the cache. What it does is asserted above.
        await Load(install, level, cache.Path);

        // And again, over what it left behind.
        int neededAtFinish = await Load(install, level, cache.Path);

        Assert.True(neededAtFinish > 0,
            $"the warm load finished having selected {neededAtFinish} assets to build from");
    }

    // A SECOND map, cold, through the same pipeline.
    //
    // Every asset-shape problem this port has had was map-specific: a mesh whose vertex buffer was
    // streamed while its neighbour's was inline, a prefab with a Viewmodel subtree and one without, a
    // collider kind one map places and another never does. PEI alone proves the pipeline runs; it cannot
    // prove the pipeline COPES, because a map is a sample of the format rather than the format itself.
    //
    // WHICH second map does not matter, and the test does not name one: it takes the smallest map on the
    // machine that is not PEI. Naming one would make this test demand content CI does not fetch —
    // fetch-game-data.sh downloads PEI by design, because a second map is tens of megabytes of cache for
    // every job — and a test that throws on content nobody asked for is a broken pipeline rather than a
    // finding. What is asserted is the same shape as the PEI load: a second sample, not a second set of
    // rules.
    [Test]
    [Timeout(600_000)]
    public async Task AColdLoadOfASecondMapBuildsAWorldToo()
    {
        if (!SecondMap(out string install, out LevelInfo level))
            return;

        using var cache = new TempDir();
        int neededAtFinish = await Load(install, level, cache.Path);

        Assert.True(neededAtFinish > 0,
            $"the second map finished having selected {neededAtFinish} assets to build from");

        // The SAME invariants PEI's load asserts. A second map whose layer-texture promise was left
        // unfinished, or which produced no geometry, is exactly the map-specific regression this test
        // exists for — and a thinner set of assertions here would let it through.
        Assert.NotEmpty(System.IO.Directory.GetFiles(cache.Path, "*.mesh",
            System.IO.SearchOption.AllDirectories));
    }

    // And two maps SHARE a cache without either corrupting the other's entries. Every session after the
    // first reads a cache the previous map filled — a key that collided across maps would hand one map's
    // geometry to another, which renders, as the wrong building in the wrong place.
    [Test]
    [Timeout(1_800_000)]
    public async Task TwoMapsShareOneCacheWithoutCollidingInIt()
    {
        if (!RealMap(out string install, out LevelInfo pei))
            return;
        if (!SecondMap(out _, out LevelInfo other))
            return;

        using var cache = new TempDir();

        Assert.True(await Load(install, pei, cache.Path) > 0);
        Assert.True(await Load(install, other, cache.Path) > 0);

        // And PEI again, now over a cache the other map has also written into. A collision would show up
        // here as a load that built from entries the second map replaced.
        Assert.True(await Load(install, pei, cache.Path) > 0);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // One load into the given cache directory, run to completion. Returns how many assets the build
    // selected, captured at the signal for the reason given above.
    private async Task<int> Load(string install, LevelInfo level, string cacheDir)
    {
        var streamer = new ObjectStreamer
        {
            Name = "ObjectStreamer",
            CacheDirOverride = cacheDir,
            TextureCacheDirOverride = cacheDir,
        };
        int neededAtFinish = -1;
        streamer.Finished += () => neededAtFinish = streamer.NeededGuids.Count;
        TestScene.AddChild(streamer);

        try
        {
            streamer.StartPrepare(install, level);
            await streamer.BeginAsync();
            // The same budget the standalone cold-load test allows. This helper does a full cold decode
            // whenever the cache it is given is empty, so a shorter limit here fails a machine that is
            // still inside the time a cold load is permitted to take.
            if (!await Within(ColdLoadBudget, streamer.Completion))
                Assert.Fail("a load never finished; a real one has no longer to wait than this");

            // Asserted for EVERY load, not only PEI's. The terrain build awaits this promise, so a map
            // that left it unfinished hangs the loading screen for good — and the second-map test says
            // it checks the same invariants as the first.
            Assert.True(streamer.LayerTextures.IsCompleted,
                "the terrain build was left waiting on a finished load");

            return neededAtFinish;
        }
        finally
        {
            await streamer.CancelAsync();
            streamer.QueueFree();
        }
    }

    // What one cold load is allowed to take. Named once because three tests spend it and a combined test
    // spends it three times over — a per-load limit shorter than this turns a slow-but-valid run into a
    // failure, and a harness timeout shorter than the sum turns it into one with no explanation at all.
    private static readonly TimeSpan ColdLoadBudget = TimeSpan.FromMinutes(8);

    // Waits on a task by advancing frames, since the work it is waiting for is driven by them. Returns
    // false on timeout rather than throwing, so the caller can say what the timeout MEANT.
    private async Task<bool> Within(TimeSpan limit, Task work)
    {
        ulong deadline = Time.GetTicksMsec() + (ulong)limit.TotalMilliseconds;
        while (!work.IsCompleted && Time.GetTicksMsec() < deadline)
            await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

        if (!work.IsCompleted)
            return false;

        await work; // surface a failure as itself rather than as a timeout
        return true;
    }

    private static bool RealMap(out string install, out LevelInfo level) =>
        Map("PEI", out install, out level);

    // The smallest installed map that is not PEI, or nothing. Deliberately NOT a throw under
    // UG_REQUIRE_REAL_DATA: that flag exists to prove the fetched content is really being used, and the
    // fetch asks for PEI alone. Demanding a map nobody downloaded would fail the job over a decision
    // taken in fetch-game-data.sh rather than over anything this code does.
    private static bool SecondMap(out string install, out LevelInfo level)
    {
        install = "";
        level = null!;
        string? found = Assets.UnturnedInstall.Find();
        string maps = found == null ? "" : System.IO.Path.Combine(found, "Maps");
        if (maps.Length == 0 || !System.IO.Directory.Exists(maps))
        {
            Log.Print("[runtime-tests] skipping: no maps directory at all");
            return false;
        }

        string? smallest = null;
        long smallestSize = long.MaxValue;
        foreach (string candidate in System.IO.Directory.GetDirectories(maps))
        {
            if (string.Equals(System.IO.Path.GetFileName(candidate), "PEI", StringComparison.Ordinal))
                continue;
            // A map with no level file is a directory, not a map.
            if (!System.IO.File.Exists(System.IO.Path.Combine(candidate, "Level.dat")))
                continue;

            long size = Weigh(candidate);
            if (size < smallestSize)
            {
                smallestSize = size;
                smallest = candidate;
            }
        }

        if (smallest == null)
        {
            Log.Print("[runtime-tests] skipping: only PEI is installed, so there is no second sample "
                + "(fetch-game-data.sh downloads PEI by design; pass --maps to add another)");
            return false;
        }

        install = found!;
        level = new LevelInfo(smallest);
        return true;
    }

    // Shallow on purpose: the top-level files are enough to rank maps by size, and walking a 500 MB tree
    // to pick a fixture would cost more than loading the map does.
    private static long Weigh(string directory)
    {
        long total = 0;
        foreach (string file in System.IO.Directory.GetFiles(directory))
            total += new System.IO.FileInfo(file).Length;
        foreach (string sub in System.IO.Directory.GetDirectories(directory))
            foreach (string file in System.IO.Directory.GetFiles(sub))
                total += new System.IO.FileInfo(file).Length;
        return total;
    }

    private static bool Map(string name, out string install, out LevelInfo level)
    {
        install = "";
        level = null!;
        string? found = Assets.UnturnedInstall.Find();
        string? maps = found == null ? null : System.IO.Path.Combine(found, "Maps", name);
        if (found == null || maps == null || !System.IO.Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    $"UG_REQUIRE_REAL_DATA=1 but the {name} map is not present; this run exists to prove "
                    + "these tests execute");
            }

            Log.Print($"[runtime-tests] skipping: no {name} map "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        install = found;
        level = new LevelInfo(maps);
        return true;
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-coldload-" + Guid.NewGuid().ToString("N"));

        public TempDir() => System.IO.Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                System.IO.Directory.Delete(Path, recursive: true);
            }
            catch (System.Exception e) when (e is System.IO.IOException or System.UnauthorizedAccessException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
