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
            if (!await Within(TimeSpan.FromMinutes(8), streamer.Completion))
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

            // The cache it filled is what makes the SECOND load warm. An empty directory here means
            // every session would pay this decode again.
            Assert.NotEmpty(System.IO.Directory.GetFileSystemEntries(cache.Path));
        }
        finally
        {
            await streamer.CancelAsync();
            streamer.QueueFree();
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

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

    private static bool RealMap(out string install, out LevelInfo level)
    {
        install = "";
        level = null!;
        string? found = Assets.UnturnedInstall.Find();
        string? maps = found == null ? null : System.IO.Path.Combine(found, "Maps", "PEI");
        if (found == null || maps == null || !System.IO.Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but the PEI map is not present; this run exists to prove these "
                    + "tests execute");
            }

            Log.Print("[runtime-tests] skipping: no PEI map "
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
            catch (System.IO.IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
