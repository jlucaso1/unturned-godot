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
        int before = pei.Zombies.Zombies.Count;

        pei.Service.Load(path!);

        Assert.Equal(before, pei.Zombies.Zombies.Count);
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

        public Recorder(Node testScene, string directory, string levelName = "PEI")
        {
            _testScene = testScene;
            _server = new NetServer(_transport,
                new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Vector3.Zero, levelName);

            var bounds = new[]
            {
                new NavBound { Center = Vector3.Zero, Size = new Vector3(256f, 64f, 256f) },
            };
            Zombies = new ZombieSystem(
                new List<ZombieTable> { new() { Name = "Normal", Health = 100, Damage = 10 } },
                bounds, FlatGround);
            var spawns = new List<ZombieSpawnpointData>();
            for (int i = 0; i < 4; i++)
                spawns.Add(new ZombieSpawnpointData(0, new Vector3(i * 2f, 0f, i * 2f)));
            Zombies.Spawn(spawns, new Random(7));

            var host = new ZombieHost(Zombies, _server);
            ReproService? service = ReproService.Create(Zombies, _server, FlatGround, host);
            Assert.NotNull(service); // REPRO defaults on; a run with it off would cover nothing here
            Service = service!;
            Service.LevelName = levelName;
            Service.Map = levelName;

            OS.SetEnvironment("REPRO_DIR", directory);
            testScene.AddChild(Service);
        }

        // _Ready reads REPRO_DIR, so nothing may capture until the node has had a frame in the tree.
        public SignalAwaiter Ready() =>
            _testScene.ToSignal(_testScene.GetTree(), SceneTree.SignalName.ProcessFrame);

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
