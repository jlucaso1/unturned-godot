using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The bug-report recorder: what a session writes down when something goes wrong in it.
//
// This exists because the interesting failures here are not crashes. A zombie that will not path around a
// fence, a route that oscillates, a population that stops waking — none of them throw, and none of them
// reproduce from a description. So the session carries a recorder that can write the whole simulation
// out: the zombies, their routes, the geometry around the incident, the disabled navmesh faces, and the
// tail of the log.
//
// Two rules make a dump worth having, and both are about NOT LOSING one:
//
//   - A capture never overwrites another. Two headless soak runs can share one directory and capture in
//     the same second, and a restarted run starts its counter over — so the process id is in the name.
//     Overwriting an incident is the one thing a bug report must not do.
//   - A capture never takes the session down. Whatever is wrong when it fires is already wrong; a
//     recorder that threw would replace a reproducible bug with a crash in the reporter.
//
// And one rule about not TRUSTING one: a dump from another map is refused. Its zombie records carry table
// indices, nav bounds and coordinates that mean something else here — at best the restored simulation is
// nonsense, at worst a zombie whose type does not exist indexes past the table the moment it lands a hit.
public class ReproServiceTests : TestClass
{
    public ReproServiceTests(Node testScene) : base(testScene) { }

    // A recorder needs something to record. Passing nothing is refused at the call rather than at the
    // capture, which would be the worst possible moment to discover it.
    [Test]
    public void ARecorderWithNothingToRecordIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => ReproService.Create(null!, null, null));
    }

    // A capture writes a file, and a second capture writes a DIFFERENT file. Two soak runs share a
    // directory and a restarted one starts its counter over, so an overwrite here silently destroys the
    // incident someone is trying to report.
    [Test]
    public async Task EveryCaptureLandsInItsOwnFile()
    {
        using var dir = new TempDir();
        using var repro = new Recorder(TestScene, dir.Path);
        await repro.Ready();

        string? first = repro.Service.Capture("test", Vector3.Zero, "the first incident");
        string? second = repro.Service.Capture("test", Vector3.Zero, "the second incident");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.True(System.IO.File.Exists(first));
        Assert.True(System.IO.File.Exists(second));
    }

    // What it wrote can be read back, and says what it was. A dump that could not be reopened would be a
    // recorder that reported nothing while looking like it worked.
    [Test]
    public async Task WhatIsWrittenCanBeReadBack()
    {
        using var dir = new TempDir();
        using var repro = new Recorder(TestScene, dir.Path);
        await repro.Ready();

        string? path = repro.Service.Capture("a route that would not resolve", Vector3.Zero, "note");
        Assert.NotNull(path);

        ReproDump dump = ReproDump.Read(path!);

        Assert.Equal("a route that would not resolve", dump.Meta.Reason);
        Assert.Equal("PEI", dump.Meta.LevelName);
        Assert.NotEmpty(dump.Describe());
    }

    // Capturing over a directory that cannot be written is quiet. Whatever is wrong when a capture fires
    // is already wrong — a recorder that threw would replace a reproducible bug with a crash in the
    // reporter, on an unattended soak run nobody is watching.
    [Test]
    public async Task ACaptureThatCannotBeWrittenIsQuiet()
    {
        using var repro = new Recorder(TestScene, "/proc/nowhere-writable");
        await repro.Ready();

        Assert.Null(repro.Service.Capture("test", Vector3.Zero, "nowhere to put it"));
    }

    // Loading something that is not a dump says so and carries on. The path comes from a person typing
    // it, so the common failure is a name that is wrong rather than a file that is corrupt.
    [Test]
    public async Task LoadingSomethingThatIsNotADumpIsSurvivable()
    {
        using var dir = new TempDir();
        using var repro = new Recorder(TestScene, dir.Path);
        await repro.Ready();

        repro.Service.Load("/nonexistent-dump.json");

        string garbage = System.IO.Path.Combine(dir.Path, "not-a-dump.json");
        System.IO.File.WriteAllText(garbage, "this is not json");
        repro.Service.Load(garbage);
    }

    // A dump from another map is refused rather than restored. Its zombie records carry table indices,
    // nav bounds and coordinates that mean something else here: at best the simulation is nonsense, at
    // worst a zombie whose type does not exist indexes past the table the moment it lands a hit.
    [Test]
    public async Task ADumpFromAnotherMapIsRefused()
    {
        using var dir = new TempDir();

        string? path;
        using (var washington = new Recorder(TestScene, dir.Path, levelName: "Washington"))
        {
            await washington.Ready();
            path = washington.Service.Capture("test", Vector3.Zero, "captured on another map");
        }

        Assert.NotNull(path);

        using var pei = new Recorder(TestScene, dir.Path, levelName: "PEI");
        await pei.Ready();
        pei.Run(ticks: 20);

        // Positions, not a count. Both sessions are built from the same table, bounds and seed, so a
        // refusal that had silently restored anyway would leave the counts equal and this test green.
        string before = Fingerprint(pei.Zombies);

        pei.Service.Load(path!);

        Assert.Equal(before, Fingerprint(pei.Zombies));
    }

    // A dump from THIS map is restored: the population it recorded replaces the one running now.
    //
    // That is the whole point of the recorder — a bug that took twenty minutes of play to reach becomes a
    // file you load — and it is the half that changes state rather than only reading it, which is why the
    // restore does three things beyond swapping the zombies. Everyone connected is told to resend their
    // regions, because they are still drawing the avatars of the population that was just replaced. The
    // recorder's ring is restarted, because capturing straight after a load would splice a pre-load
    // anchor onto post-load ticks. And the automatic trigger forgets what it was watching, because ids
    // are reused by the restored population and the dump most likely to be loaded is the one whose
    // incident filled those tracks with exactly the churn the trigger looks for.
    [Test]
    public async Task ADumpFromThisMapIsRestored()
    {
        using var dir = new TempDir();
        using var repro = new Recorder(TestScene, dir.Path);
        await repro.Ready();

        repro.Run(ticks: 20);

        int before = repro.Zombies.Zombies.Count;
        Assert.True(before > 0, "nothing was hosted, so there is nothing to restore over");

        string? path = repro.Service.Capture("test", Vector3.Zero, "a population to come back to");
        Assert.NotNull(path);

        string captured = Fingerprint(repro.Zombies);

        // REPLACE the population before loading. Ticking alone will not do it — an idle population with
        // nobody to chase stands still — so a Load that did nothing at all would be indistinguishable
        // from one that restored correctly.
        repro.Respawn(seed: 99);
        Assert.NotEqual(captured, Fingerprint(repro.Zombies));

        repro.Service.Load(path!);

        // Back to the window's first tick: the same population, in the same places.
        Assert.Equal(before, repro.Zombies.Zombies.Count);
        Assert.Equal(captured, Fingerprint(repro.Zombies));
    }

    // A dump whose zombie records name types this map does not have is refused rather than restored. The
    // bound check inside RestoreState only catches the cases where this map happens to have fewer
    // regions; a type index past the table would fault the moment that zombie landed a hit.
    [Test]
    public async Task ADumpThisMapCannotHoldIsRefused()
    {
        using var dir = new TempDir();

        string? path;
        using (var rich = new Recorder(TestScene, dir.Path, bounds: 4))
        {
            await rich.Ready();
            rich.Run(ticks: 20);
            path = rich.Service.Capture("test", Vector3.Zero, "captured on a map with more regions");
        }

        Assert.NotNull(path);

        using var poor = new Recorder(TestScene, dir.Path, bounds: 1);
        await poor.Ready();
        int before = poor.Zombies.Zombies.Count;

        poor.Service.Load(path!);

        // The population is left as it was: RestoreState refuses a record whose bounds this session does
        // not have, and the refusal is reported rather than swallowed into a half-restored world.
        Assert.Equal(before, poor.Zombies.Zombies.Count);
    }

    // Who is alive and where, to the centimetre. A count alone cannot tell a restore from a no-op when
    // both sessions were built from the same table, bounds and seed.
    private static string Fingerprint(ZombieSystem zombies)
    {
        var parts = new List<string>();
        foreach (ZombieInstance zombie in zombies.Zombies)
            parts.Add($"{zombie.Position.X:F2},{zombie.Position.Y:F2},{zombie.Position.Z:F2}");
        parts.Sort(StringComparer.Ordinal);
        return string.Join("|", parts);
    }

    // --- helpers -------------------------------------------------------------------------------------

    // A recorder over a small hosted population, in the tree so _Ready reads its environment.
    private sealed class Recorder : IDisposable
    {
        private readonly Node _testScene;
        private readonly LoopbackServerTransport _transport = new();
        private readonly NetServer _server;
        private bool _disposed;

        public ZombieSystem Zombies { get; }
        public ReproService Service { get; }

        private List<ZombieSpawnpointData> _spawns = new();

        public Recorder(Node testScene, string directory, string levelName = "PEI", int bounds = 1)
        {
            _testScene = testScene;
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Vector3.Zero, levelName);

            var regions = new NavBound[bounds];
            for (int i = 0; i < bounds; i++)
            {
                regions[i] = new NavBound
                {
                    Center = new Vector3(i * 512f, 0f, 0f),
                    Size = new Vector3(256f, 64f, 256f),
                };
            }

            Zombies = new ZombieSystem(
                new List<ZombieTable> { new() { Name = "Normal", Health = 100, Damage = 10 } },
                regions, FlatGround);
            _spawns = new List<ZombieSpawnpointData>();
            for (int region = 0; region < bounds; region++)
                for (int i = 0; i < 16; i++)
                {
                    _spawns.Add(new ZombieSpawnpointData(0,
                        new Vector3((region * 512f) + (i * 3f), 0f, i * 3f)));
                }

            Zombies.Spawn(_spawns, new Random(7));

            // REPRO=0 is the documented way to turn the recorder off, and Create honours it — read at
            // call time, so unlike the navigation capture flag this one can be forced. Without that, a
            // suite run from a shell that disables the recorder fails every test in this file at the
            // assertion below instead of exercising the service.
            _repro = new EnvScope("REPRO", "1");
            _reproDir = new EnvScope("REPRO_DIR", directory);

            var host = new ZombieHost(Zombies, _server);
            ReproService? service = ReproService.Create(Zombies, _server, FlatGround, host);
            Assert.NotNull(service); // REPRO defaults on; a run with it off would cover nothing here
            Service = service!;
            Service.LevelName = levelName;
            Service.Map = levelName;

            testScene.AddChild(Service);
        }

        // _Ready reads REPRO_DIR, so nothing may capture until the node has had a frame in the tree —
        // and a PHYSICS frame at that, because Capture queries DirectSpaceState for the geometry slice
        // and this project runs 3D physics on its own thread. Resuming on an idle frame would either log
        // engine errors or quietly record no collision around the incident.
        public SignalAwaiter Ready() =>
            _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.PhysicsFrame);

        // A different population in the same world: the same spawnpoints drawn with another seed, which
        // is what a session that has been played for a while looks like next to the one a dump holds.
        public void Respawn(int seed) => Zombies.Spawn(_spawns, new Random(seed));

        // Run the session. A dump is a window of TICKS — the recorder's ring is what a capture is built
        // from — so a session that has not ticked has nothing to record, and restoring such a dump would
        // restore an empty population over a live one.
        public void Run(int ticks)
        {
            for (int i = 0; i < ticks; i++)
            {
                _now += ServerSimulation.TickRate;
                _server.Update(_now);
            }
        }

        private double _now = 1000.0;

        private static bool FlatGround(float x, float z, out float y)
        {
            y = 0f;
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _testScene.RemoveChild(Service);
            Service.Free();
            _reproDir?.Dispose();
            _repro?.Dispose();
        }

        private EnvScope? _repro;
        private EnvScope? _reproDir;

        // One environment variable, set for the life of a recorder and put back afterwards. Left set,
        // the next test in the process would run against this one's directory.
        private sealed class EnvScope : IDisposable
        {
            private readonly string _name;
            private readonly string _previous;

            public EnvScope(string name, string value)
            {
                _name = name;
                _previous = OS.GetEnvironment(name);
                OS.SetEnvironment(name, value);
            }

            public void Dispose() => OS.SetEnvironment(_name, _previous);
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-repro-" + Guid.NewGuid().ToString("N"));

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
