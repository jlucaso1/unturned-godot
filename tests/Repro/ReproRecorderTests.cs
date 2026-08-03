using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The recorder is what is running while nothing is wrong, so what it does when nothing is wrong — how
// much it keeps, what it costs, what it does at the edges of its ring — is its whole specification.
public class ReproRecorderTests
{
    private static ZombieSystem Bare(out List<Vector3> asked)
    {
        var log = new List<Vector3>();
        asked = log;
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 1 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround)
        {
            MoveResolver = (from, to, radius) =>
            {
                log.Add(to);
                return to;
            },
            GroundSnap = (Vector3 position, out float y) =>
            {
                y = 0f;
                return true;
            },
            VisionBlocked = (from, to) => false,
            PathReady = () => true,
        };
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5f, 0f, -5f)) },
            new ReproRandom(1));
        return system;
    }

    [Fact]
    public void AttachAndDetach_LeaveTheSystemAsItWas()
    {
        ZombieSystem system = Bare(out _);
        ZombieMoveResolver? original = system.MoveResolver;
        var recorder = new ReproRecorder(system);

        recorder.Attach();
        Assert.NotSame(original, system.MoveResolver);
        recorder.Attach(); // idempotent: a second attach must not wrap the wrapper
        recorder.Detach();
        Assert.Same(original, system.MoveResolver);
        recorder.Detach();
        Assert.Same(original, system.MoveResolver);
        Assert.Null(system.Observer);
    }

    // Recording must not change the simulation it is recording.
    [Fact]
    public void RecordingDoesNotChangeTheOutcome()
    {
        var geometry = new ReproWorlds.Geometry().Ground()
            .Box("pole", new Vector3(12f, 1.5f, 12f), new Vector3(0.25f, 1.5f, 0.25f));
        (ZombieSystem plain, _, _) = ReproWorlds.Session(geometry);
        (ZombieSystem recorded, _, _) = ReproWorlds.Session(geometry);
        new ReproRecorder(recorded).Attach();

        for (int i = 0; i < 30; i++)
        {
            var player = new[] { ReproWorlds.Player(new Vector3(16f + (i * 0.1f), 0f, 16f)) };
            plain.Tick(player, ReproWorlds.TickRate);
            recorded.Tick(player, ReproWorlds.TickRate);
        }
        Assert.Equal(plain.Zombies[0].Position, recorded.Zombies[0].Position);
        Assert.Equal(plain.Zombies[0].Yaw, recorded.Zombies[0].Yaw);
    }

    // The window is at least half the ring however long the session has been running, because the
    // anchors alternate. A capture that could land on a one-tick window would be useless exactly when
    // someone reaches for the key.
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(40)]
    [InlineData(200)]
    public void TheWindowIsNeverShorterThanHalfTheRing(int ticks)
    {
        ZombieSystem system = Bare(out _);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 16 });
        recorder.Attach();
        for (int i = 0; i < ticks; i++)
            system.Tick(Array.Empty<ZombiePlayerView>(), ReproWorlds.TickRate);

        recorder.Build(out _, out int tickCount);
        Assert.InRange(tickCount, Math.Min(ticks, 8), 16);
    }

    [Fact]
    public void WithNoTicksAtAll_TheWindowIsEmptyAndSaysSo()
    {
        ZombieSystem system = Bare(out _);
        var recorder = new ReproRecorder(system);
        recorder.Attach();
        ReproZombieSection section = recorder.Build(out uint start, out int count);
        Assert.Equal(0u, start);
        Assert.Equal(0, count);
        Assert.Empty(section.Frames);
        Assert.Empty(section.State.Zombies);

        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(new ReproWorlds.Geometry(), Vector3.Zero), system, recorder,
            ReproWorlds.FlatGround);
        Assert.Contains(dump.Meta.Warnings, warning => warning.Contains("window is empty",
            StringComparison.Ordinal));
    }

    // The per-tick ceiling bounds memory, and what it drops is counted rather than quietly missing.
    [Fact]
    public void TheCallCeilingIsReportedRatherThanHidden()
    {
        ZombieSystem system = Bare(out _);
        var recorder = new ReproRecorder(system,
            new ReproRecorderOptions { WindowTicks = 4, MaxCallsPerTick = 1 });
        recorder.Attach();
        for (int i = 0; i < 20; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        Assert.True(recorder.DroppedCalls > 0);

        ReproDump dump = ReproCapture.Build(
            ReproWorlds.Request(new ReproWorlds.Geometry(), Vector3.Zero), system, recorder,
            ReproWorlds.FlatGround);
        Assert.Contains(dump.Meta.Warnings,
            warning => warning.Contains("per-tick recording ceiling", StringComparison.Ordinal));
    }

    // Frames carry the server's own tick numbers when the host supplies them, so a dump lines up with
    // the log lines around it.
    [Fact]
    public void TheServerTickTravelsWithTheWindow()
    {
        ZombieSystem system = Bare(out _);
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 8 });
        recorder.Attach();
        for (int i = 0; i < 8; i++)
        {
            system.AuthoritativeTick = (uint)(1000 + i);
            system.Tick(Array.Empty<ZombiePlayerView>(), ReproWorlds.TickRate);
        }
        ReproZombieSection section = recorder.Build(out _, out _);
        Assert.Equal(1000u, section.Frames[0].Tick);
        Assert.Equal(1000u, recorder.ServerTickOfWindowStart);
        Assert.Equal(1007u, section.Frames[^1].Tick);
        Assert.Equal(8, recorder.RecordedTicks);
    }

    // A recorder wrapping a system with no world delegates at all still records the run.
    [Fact]
    public void ASystemWithNoWorldDelegatesStillRecords()
    {
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 1 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5f, 0f, -5f)) },
            new ReproRandom(1));
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 4 });
        recorder.Attach();
        for (int i = 0; i < 6; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        ReproZombieSection section = recorder.Build(out _, out int ticks);
        Assert.True(ticks > 0);
        Assert.Equal(0, section.Oracle.MoveCount);
        Assert.NotEmpty(section.Frames);
    }

    // Whatever was already observing the tick keeps observing it.
    [Fact]
    public void AnExistingObserverIsChained()
    {
        ZombieSystem system = Bare(out _);
        var counter = new CountingObserver();
        system.Observer = counter;
        var recorder = new ReproRecorder(system);
        recorder.Attach();
        system.Tick(Array.Empty<ZombiePlayerView>(), ReproWorlds.TickRate);
        Assert.Equal(1, counter.Begins);
        Assert.Equal(1, counter.Ends);
        recorder.Detach();
        Assert.Same(counter, system.Observer);
    }

    [Fact]
    public void ConstructionRejectsAMissingSystem() =>
        Assert.Throws<ArgumentNullException>(() => new ReproRecorder(null!));

    // A zombie that settles during the window has to be in the frame it settles on. Without that
    // sample there is no recorded idle to disagree with, and a build that wrongly keeps chasing
    // replays as an exact reproduction.
    [Fact]
    public void TheTickAZombieSettlesOnIsRecorded()
    {
        ZombieSystem system = Bare(out _);
        ZombieInstance zombie = system.Zombies[0];
        var recorder = new ReproRecorder(system, new ReproRecorderOptions { WindowTicks = 8 });
        recorder.Attach();

        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;
        for (int i = 0; i < 2; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        // The target leaves the roster: Hunt gives up and the zombie settles on this tick.
        system.Tick(Array.Empty<ZombiePlayerView>(), ReproWorlds.TickRate);

        // The frames have to show that happening rather than the zombie simply vanishing from them.
        ReproZombieSection section = recorder.Build(out _, out _);
        var states = new List<EZombieState>();
        foreach (ReproFrame frame in section.Frames)
            foreach (ReproMotionSample sample in frame.Zombies)
                states.Add(sample.State);
        Assert.Contains(EZombieState.Idle, states);

        // ...and once it has settled it stops being sampled every tick again.
        int settled = section.Frames.Count;
        for (int i = 0; i < 4; i++)
            system.Tick(Array.Empty<ZombiePlayerView>(), ReproWorlds.TickRate);
        ReproZombieSection later = recorder.Build(out _, out _);
        int empty = 0;
        foreach (ReproFrame frame in later.Frames)
            if (frame.Zombies.Count == 0)
                empty++;
        Assert.True(empty > 0, $"every one of {settled} frames still carried a settled zombie");
    }

    private sealed class CountingObserver : IZombieTickObserver
    {
        public int Begins { get; private set; }

        public int Ends { get; private set; }

        public void BeginTick(ZombieSystem system, IReadOnlyList<ZombiePlayerView> players, float dt) =>
            Begins++;

        public void EndTick(ZombieSystem system) => Ends++;
    }
}
