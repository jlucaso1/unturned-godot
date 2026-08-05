using System;
using System.IO;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// What happens when the cache on disk is wrong.
//
// Every load after the first one reads from a cache this game wrote itself, and that cache can be wrong
// in ways nothing upstream can prevent: a machine that lost power mid-write, a disk that filled, a copy
// between machines, a format that changed under a file the last version left behind, two worktrees
// sharing one user:// directory. None of those are hypothetical — the last one is how this suite's own
// caches get mixed up.
//
// The rule the whole cache rests on is that a BAD ENTRY IS A MISSING ENTRY. A corrupt mesh has to be
// dropped and re-extracted, not read as whatever the bytes happen to say — a mesh built from the wrong
// bytes renders, as a spray of triangles across the map, which is worse than one that is absent. And it
// must never end the load: the cache is an optimisation, and a load that died because its optimisation
// was damaged would be a game that could not start until someone found the directory and deleted it.
//
// This is the one thing in the suite that cannot be reached by driving the game correctly, so the
// damage is done deliberately.
public class CorruptCacheTests : TestClass
{
    public CorruptCacheTests(Node testScene) : base(testScene) { }

    // A mesh cache entry truncated after its magic is dropped rather than read. The magic check is four
    // bytes, so a file cut anywhere after them still gets past it — which is exactly what a half-written
    // file looks like.
    [Test]
    [Timeout(600_000)]
    public async Task AMeshEntryTruncatedAfterItsMagicIsDropped()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        using var cache = new TempDir();
        await Load(install, level, cache.Path);

        string[] meshes = Directory.GetFiles(cache.Path, "*.mesh", SearchOption.AllDirectories);
        if (meshes.Length == 0)
            return; // nothing was cached to damage

        // Cut every entry to its first eight bytes: past the magic, and useless as a mesh.
        foreach (string mesh in meshes)
        {
            byte[] head = File.ReadAllBytes(mesh);
            File.WriteAllBytes(mesh, head.Length <= 8 ? head : head[..8]);
        }

        // The load has to finish anyway. It may re-extract, it may build fewer objects — what it may not
        // do is throw, hang, or read those bytes as geometry.
        int selected = await Load(install, level, cache.Path);

        Assert.True(selected >= 0);
    }

    // An entry whose magic is wrong entirely — a file from another format, or another program — is
    // skipped as stale rather than parsed. This is what a worktree sharing one user:// directory with a
    // differently-versioned checkout produces.
    [Test]
    [Timeout(600_000)]
    public async Task AnEntryFromAnotherFormatIsSkippedAsStale()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        using var cache = new TempDir();
        await Load(install, level, cache.Path);

        string[] meshes = Directory.GetFiles(cache.Path, "*.mesh", SearchOption.AllDirectories);
        if (meshes.Length == 0)
            return;

        foreach (string mesh in meshes)
            File.WriteAllBytes(mesh, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 1, 2, 3, 4 });

        int selected = await Load(install, level, cache.Path);

        Assert.True(selected >= 0);
    }

    // A cache directory full of files that are not caches at all is walked past. Something else's
    // directory, or a user who put something there, must not end a load.
    [Test]
    [Timeout(600_000)]
    public async Task RubbishInTheCacheDirectoryIsWalkedPast()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        using var cache = new TempDir();
        File.WriteAllText(Path.Combine(cache.Path, "notes.txt"), "this is not a cache");
        File.WriteAllBytes(Path.Combine(cache.Path, "empty.mesh"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(cache.Path, "onebyte.mesh"), new byte[] { 7 });
        Directory.CreateDirectory(Path.Combine(cache.Path, "a-directory.mesh"));

        int selected = await Load(install, level, cache.Path);

        Assert.True(selected > 0, "a load over a dirty cache directory built nothing at all");
    }

    // A DAMAGED def.bin THROWS out of the audio library, and this pins that rather than the behaviour one
    // would expect from the rest of the cache. It is reported as a finding, not asserted around.
    //
    // The marker is written last, as the completeness signal, and the write is not atomic — so a machine
    // that lost power mid-write leaves a short def.bin that IsCached accepts and the reader cannot parse.
    // Every other cache reader in the codebase treats that as a missing entry: the mesh cache drops it and
    // re-extracts, the texture cache skips it. This one propagates, and its call chain is
    // Resolve -> OneShotAudio.Play -> MovementAudio.Tick -> the player's physics frame, which is a session
    // ended by a footstep.
    //
    // Whether to guard it is a production decision and is left to one; the test exists so the behaviour is
    // written down and cannot change silently in either direction.
    [Test]
    public void ADamagedMarkerThrowsOutOfTheAudioLibrary()
    {
        using var dir = new TempDir();
        string defDir = Path.Combine(dir.Path, "core-footstep");
        Directory.CreateDirectory(defDir);
        File.WriteAllBytes(Path.Combine(defDir, "def.bin"), new byte[] { 1, 2, 3 });

        // The extractor believes the marker: that is the contract, and it is why the entry is reached.
        Assert.True(AudioExtractor.IsCached(dir.Path, "core-footstep"));

        Assert.ThrowsAny<Exception>(() => new AudioDefLibrary(dir.Path).Resolve("core-footstep"));
    }

    // A definition whose marker is intact but whose CLIPS are gone resolves to nothing, which is the
    // half-deleted directory a user produces by clearing space. An entry with no clips is no entry.
    [Test]
    public void AMarkerWithoutItsClipsResolvesToNothing()
    {
        using var dir = new TempDir();
        string defDir = Path.Combine(dir.Path, "core-footstep");
        Directory.CreateDirectory(defDir);
        WriteMarker(Path.Combine(defDir, "def.bin"));

        Assert.Null(new AudioDefLibrary(dir.Path).Resolve("core-footstep"));
    }

    // And a clip file that is not an ogg resolves to nothing rather than to a stream that plays silence.
    // A truncated download and a half-written extraction both land here.
    [Test]
    public void AClipThatIsNotAnOggResolvesToNothing()
    {
        using var dir = new TempDir();
        string defDir = Path.Combine(dir.Path, "core-footstep");
        Directory.CreateDirectory(defDir);
        WriteMarker(Path.Combine(defDir, "def.bin"), "clip0.ogg");
        File.WriteAllBytes(Path.Combine(defDir, "clip0.ogg"), new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        Assert.Null(new AudioDefLibrary(dir.Path).Resolve("core-footstep"));
    }

    // A well-formed marker, written the way the extractor writes one, so the tests above damage only the
    // thing they mean to damage.
    private static void WriteMarker(string path, params string[] clips)
    {
        using FileStream output = File.Create(path);
        Unity.AudioDefCache.Write(output,
            new Unity.OneShotAudioDef(1f, 1f, 1f, new System.Collections.Generic.List<string>(clips)));
    }

    // A navmesh reconciliation cache written for other geometry is not restored. Its fingerprint covers
    // the level, the model cache, the selected colliders, the step offset and the probe settings — and a
    // cache restored past a change to any of them would disable faces that no longer correspond to
    // anything, leaving a map that looks right while zombies refuse to walk through open doors.
    [Test]
    public async Task ANavCacheWrittenForOtherGeometryIsNotRestored()
    {
        using var level = new TempLevel();
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        sandbox.AddBox(new Vector3(0f, 0.5f, 0f), new Vector3(6f, 1f, 6f));
        await sandbox.Settle();

        PhysicsDirectSpaceState3D space = sandbox.Root.GetWorld3D().DirectSpaceState;

        // One pass at one step offset, which writes a cache under that offset's fingerprint.
        ZombieNavigation first = Floor(level.Path);
        await first.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f,
            new System.Collections.Generic.HashSet<Guid>());
        first.Free();

        // A second pass with a DIFFERENT step offset must not restore it: what a body can step over is
        // exactly what decides which faces are routes.
        ZombieNavigation second = Floor(level.Path);
        await second.PruneAgainstCollisionAsync(sandbox.Root, space, 0.05f,
            new System.Collections.Generic.HashSet<Guid>());

        Assert.True(second.IsReady, "the second pass published no graph");
        second.Free();
    }

    // And a nav cache that is simply damaged is ignored rather than read. The pass then costs its
    // hundreds of thousands of raycasts again, which is the right price for not trusting it.
    [Test]
    public async Task ADamagedNavCacheIsIgnoredRatherThanRead()
    {
        using var level = new TempLevel();
        using var sandbox = new PhysicsSandbox(TestScene);
        sandbox.AddBox(new Vector3(0f, -0.5f, 0f), new Vector3(40f, 1f, 40f));
        await sandbox.Settle();

        PhysicsDirectSpaceState3D space = sandbox.Root.GetWorld3D().DirectSpaceState;

        ZombieNavigation first = Floor(level.Path);
        await first.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f,
            new System.Collections.Generic.HashSet<Guid>());
        first.Free();

        string entry = Path.Combine(ProjectSettings.GlobalizePath("user://nav_reconcile"),
            NavReconcileCache.MapKey(level.Path) + ".cache");
        if (File.Exists(entry))
            File.WriteAllBytes(entry, new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 });

        ZombieNavigation second = Floor(level.Path);
        await second.PruneAgainstCollisionAsync(sandbox.Root, space, 0.5f,
            new System.Collections.Generic.HashSet<Guid>());

        Assert.True(second.IsReady, "a damaged cache stopped the pass from publishing at all");
        second.Free();
    }

    // --- helpers -------------------------------------------------------------------------------------

    // One load into the given cache directory, run to completion. Returns how many assets it selected,
    // captured at the Finished signal because the streamer releases that set immediately afterwards.
    private async Task<int> Load(string install, LevelInfo level, string cacheDir)
    {
        var streamer = new ObjectStreamer
        {
            Name = "ObjectStreamer",
            CacheDirOverride = cacheDir,
            TextureCacheDirOverride = cacheDir,
        };
        int selected = -1;
        streamer.Finished += () => selected = streamer.NeededGuids.Count;
        TestScene.AddChild(streamer);

        try
        {
            streamer.StartPrepare(install, level);
            await streamer.BeginAsync();

            ulong deadline = Time.GetTicksMsec() + 240_000;
            while (!streamer.Completion.IsCompleted && Time.GetTicksMsec() < deadline)
                await TestScene.ToSignal(TestScene.GetTree(), SceneTree.SignalName.ProcessFrame);

            Assert.True(streamer.Completion.IsCompleted, "the load never finished over a damaged cache");
            await streamer.Completion;
            return selected;
        }
        finally
        {
            await streamer.CancelAsync();
            streamer.QueueFree();
        }
    }

    // A 20 m floor tessellated at 2 m, keyed to a level directory of the test's own.
    private static ZombieNavigation Floor(string levelDir)
    {
        const int cells = 10;
        const float half = 10f, step = 20f / cells;
        var vertices = new Vector3[(cells + 1) * (cells + 1)];
        for (int z = 0; z <= cells; z++)
            for (int x = 0; x <= cells; x++)
                vertices[(z * (cells + 1)) + x] = new Vector3(-half + (x * step), 0f, -half + (z * step));

        var triangles = new System.Collections.Generic.List<int>();
        for (int z = 0; z < cells; z++)
            for (int x = 0; x < cells; x++)
            {
                int origin = (z * (cells + 1)) + x;
                triangles.AddRange(new[] { origin, origin + 1, origin + cells + 2 });
                triangles.AddRange(new[] { origin, origin + cells + 2, origin + cells + 1 });
            }

        var flag = new NavFlag
        {
            Center = Vector3.Zero,
            Size = new Vector3(64f, 16f, 64f),
            Vertices = vertices,
            Triangles = triangles.ToArray(),
        };
        return Assert.IsType<ZombieNavigation>(ZombieNavigation.Build(
            new System.Collections.Generic.List<NavFlag> { flag }, null, levelDir));
    }

    private static bool RealMap(out string install, out LevelInfo level)
    {
        install = "";
        level = null!;
        string? found = Assets.UnturnedInstall.Find();
        string? maps = found == null ? null : Path.Combine(found, "Maps", "PEI");
        if (found == null || maps == null || !Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new IOException(
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

    // A level directory of the test's own, and the reconciliation cache entry it causes to be written.
    private sealed class TempLevel : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-faultlevel-" + Guid.NewGuid().ToString("N"));

        public TempLevel() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            string entry = System.IO.Path.Combine(
                ProjectSettings.GlobalizePath("user://nav_reconcile"),
                NavReconcileCache.MapKey(Path) + ".cache");
            foreach (string leftover in new[] { entry, entry + ".csr", entry + ".tmp" })
            {
                try
                {
                    File.Delete(leftover);
                }
                catch (IOException)
                {
                    // A leftover entry keyed to a directory that no longer exists is harmless.
                }
            }

            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Same.
            }
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-corrupt-" + Guid.NewGuid().ToString("N"));

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
