using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// State that survives the trip to disk and back, in full. A field that is captured but not restored is
// a field the replay makes up, and that is the difference between "cannot reproduce" and a fix.
public class ReproStateTests
{
    private static ZombieInstance Populated() => new()
    {
        Id = 12,
        Bound = 0,
        Type = 0,
        Speciality = EZombieSpeciality.Sprinter,
        IsHyper = true,
        Shirt = 3,
        Pants = 4,
        Hat = 5,
        Gear = 6,
        Move = 2,
        Idle = 1,
        Home = new Vector3(1f, 2f, 3f),
        Position = new Vector3(-616.22f, 35.02f, -66.58f),
        Yaw = 241.5f,
        State = EZombieState.Chase,
        TargetPlayer = 7,
        Path = EZombiePath.Left,
        SinceSwing = 0.32f,
        PendingHit = 0.1f,
        LeaveDelay = 2.5f,
        LeaveTo = new Vector3(9f, 0f, 9f),
        RepathTimer = 0.25f,
        BlockedRouteTime = 0.5f,
        CurrentWaypointIndex = 2,
        TargetReached = true,
        PathIsPartial = true,
        RouteServedAnotherTarget = true,
        SteerDirection = new Vector3(0.5f, 0f, -0.5f),
    };

    [Fact]
    public void EveryFieldOfAZombieRoundTrips()
    {
        ZombieInstance original = Populated();
        original.PathPoints.AddRange(new[] { new Vector3(1f, 0f, 1f), new Vector3(2f, 0f, 2f) });

        ZombieInstance restored = ReproZombieState.FromRecord(ReproZombieState.ToRecord(original));

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Bound, restored.Bound);
        Assert.Equal(original.Speciality, restored.Speciality);
        Assert.Equal(original.IsHyper, restored.IsHyper);
        Assert.Equal(original.Shirt, restored.Shirt);
        Assert.Equal(original.Pants, restored.Pants);
        Assert.Equal(original.Hat, restored.Hat);
        Assert.Equal(original.Gear, restored.Gear);
        Assert.Equal(original.Move, restored.Move);
        Assert.Equal(original.Idle, restored.Idle);
        Assert.Equal(original.Home, restored.Home);
        Assert.Equal(original.Position, restored.Position);
        Assert.Equal(original.Yaw, restored.Yaw);
        Assert.Equal(original.State, restored.State);
        Assert.Equal(original.TargetPlayer, restored.TargetPlayer);
        Assert.Equal(original.Path, restored.Path);
        Assert.Equal(original.SinceSwing, restored.SinceSwing);
        Assert.Equal(original.PendingHit, restored.PendingHit);
        Assert.Equal(original.LeaveDelay, restored.LeaveDelay);
        Assert.Equal(original.LeaveTo, restored.LeaveTo);
        Assert.Equal(original.RepathTimer, restored.RepathTimer);
        Assert.Equal(original.BlockedRouteTime, restored.BlockedRouteTime);
        Assert.Equal(original.CurrentWaypointIndex, restored.CurrentWaypointIndex);
        Assert.Equal(original.TargetReached, restored.TargetReached);
        Assert.Equal(original.PathIsPartial, restored.PathIsPartial);
        Assert.Equal(original.RouteServedAnotherTarget, restored.RouteServedAnotherTarget);
        Assert.Equal(original.SteerDirection, restored.SteerDirection);
        Assert.Equal(original.PathPoints, restored.PathPoints);
    }

    // A fresh zombie's SinceSwing is +infinity, which JSON only carries because the dump asks for
    // named literals. Losing it would make every restored zombie able to swing immediately.
    [Fact]
    public void InfinityInTheSwingTimerSurvivesJson()
    {
        var zombie = new ZombieInstance { Id = 1, SinceSwing = float.PositiveInfinity };
        var dump = new ReproDump
        {
            Zombies = new ReproZombieSection
            {
                State = new ReproSystemState { Zombies = { ReproZombieState.ToRecord(zombie) } },
            },
        };
        ReproDump? parsed = ReproDump.FromJson(dump.ToJson());
        Assert.Equal(float.PositiveInfinity, parsed!.Zombies!.State.Zombies[0].SinceSwing);
    }

    [Fact]
    public void TheSystemStateRoundTripsThroughTheBrain()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, List<NavFlag> flags) = ReproWorlds.Session(geometry);
        for (int i = 0; i < 20; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);

        ZombieSystemState captured = system.CaptureState();
        Assert.True(captured.RandomIsReproducible);
        Assert.NotEmpty(captured.Agro);

        var rebuilt = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 5 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround, flags);
        rebuilt.RestoreState(ReproZombieState.FromRecord(ReproZombieState.ToRecord(captured)));

        Assert.Equal(system.Zombies.Count, rebuilt.Zombies.Count);
        // Positions travel at the dump's own precision (ReproVector.Digits: tenths of a millimetre),
        // which is finer than anything the simulation's own inputs carry.
        Assert.True((system.Zombies[0].Position - rebuilt.Zombies[0].Position).Length() < 1e-3f);
        Assert.Equal(system.AgroOn(1), rebuilt.AgroOn(1));
        Assert.Single(rebuilt.ZombiesInBound(0));

        // The generator carries on from where the session left it, so the next roll matches.
        Assert.Equal(captured.RandomState, rebuilt.CaptureState().RandomState);
    }

    // A session that was not running the reproducible generator still dumps; the replay says so and
    // rolls from a seed instead.
    [Fact]
    public void APlainRandomIsFlaggedRatherThanFaked()
    {
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 5 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5f, 0f, -5f)) }, new Random(1));

        ZombieSystemState state = system.CaptureState();
        Assert.False(state.RandomIsReproducible);
        Assert.Equal(0UL, state.RandomState);

        system.RestoreState(state, randomSeed: 99);
        Assert.True(system.CaptureState().RandomIsReproducible);
    }

    // Restoring into a level with fewer nav regions than the dump came from is a wrong-map mistake,
    // and it says which zombie gave it away rather than throwing an index error.
    [Fact]
    public void ADumpFromAnotherMapIsRefusedWithAReason()
    {
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 5 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround);
        var state = new ZombieSystemState();
        state.Zombies.Add(new ZombieInstance { Id = 4, Bound = 9 });
        ArgumentOutOfRangeException error =
            Assert.Throws<ArgumentOutOfRangeException>(() => system.RestoreState(state));
        Assert.Contains("another map", error.Message, StringComparison.Ordinal);
    }

    // The recorder re-snapshots into buffers it already owns; that reuse must not alias the live
    // zombies, or the "state at the start of the window" would follow them forward.
    [Fact]
    public void SnapshottingIntoABufferCopiesRatherThanAliases()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var buffer = new ZombieSystemState();
        system.CaptureStateInto(buffer);
        Vector3 before = buffer.Zombies[0].Position;

        for (int i = 0; i < 10; i++)
            system.Tick(new[] { ReproWorlds.Player(new Vector3(10f, 0f, 10f)) }, ReproWorlds.TickRate);
        Assert.Equal(before, buffer.Zombies[0].Position);
        Assert.NotEqual(before, system.Zombies[0].Position);

        // Re-snapshotting reuses the same instances and still reflects the new state.
        ZombieInstance reused = buffer.Zombies[0];
        system.CaptureStateInto(buffer);
        Assert.Same(reused, buffer.Zombies[0]);
        Assert.Equal(system.Zombies[0].Position, buffer.Zombies[0].Position);
    }

    // A population that shrank between snapshots leaves no ghosts in the buffer.
    [Fact]
    public void ASmallerPopulationTrimsTheBuffer()
    {
        var geometry = new ReproWorlds.Geometry().Ground();
        (ZombieSystem system, _, _) = ReproWorlds.Session(geometry);
        var buffer = new ZombieSystemState();
        buffer.Zombies.Add(new ZombieInstance { Id = 90 });
        buffer.Zombies.Add(new ZombieInstance { Id = 91 });
        buffer.Zombies.Add(new ZombieInstance { Id = 92 });
        system.CaptureStateInto(buffer);
        Assert.Single(buffer.Zombies);
    }

    [Fact]
    public void NullsAreRefused()
    {
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.ToRecord((ZombieInstance)null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.FromRecord((ReproZombieRecord)null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.ToRecord((ZombieSystemState)null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.FromRecord((ReproSystemState)null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.FromSample(null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.ToBounds(null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.ToTables(null!));
        Assert.Throws<ArgumentNullException>(() => ReproZombieState.ToNavFlags(null!));
        Assert.Throws<ArgumentNullException>(() => ZombieSystemState.Clone(null!));
        Assert.Throws<ArgumentNullException>(() =>
            ZombieSystemState.CopyInto(new ZombieInstance(), null!));

        var system = new ZombieSystem(Array.Empty<ZombieTable>(), new[] { ReproWorlds.Bound() },
            ReproWorlds.FlatGround);
        Assert.Throws<ArgumentNullException>(() => system.RestoreState(null!));
        Assert.Throws<ArgumentNullException>(() => system.CaptureStateInto(null!));
    }

    [Fact]
    public void TheNavWorldRebuildsFromTheDump()
    {
        var world = new ReproWorldData
        {
            HasNavmesh = true,
            Bounds =
            {
                new ReproNavBound
                {
                    Center = new[] { 1f, 2f, 3f },
                    Size = new[] { 10f, 20f, 30f },
                    MaxZombies = 12,
                    SpawnZombies = true,
                    HyperAgro = true,
                },
            },
            Tables = { new ReproZombieTable { Name = "Boss", IsMega = true, Health = 400, Damage = 40 } },
            NavFlags =
            {
                new ReproNavFlag
                {
                    Center = new[] { 0f, 0f, 0f },
                    Size = new[] { 100f, 10f, 100f },
                    Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f },
                    Triangles = new[] { 0, 1, 2 },
                    Sliced = true,
                },
            },
        };

        NavBound bound = ReproZombieState.ToBounds(world)[0];
        Assert.Equal(new Vector3(1f, 2f, 3f), bound.Center);
        Assert.Equal(12, bound.MaxZombies);
        Assert.True(bound.HyperAgro);

        ZombieTable table = ReproZombieState.ToTables(world)[0];
        Assert.True(table.IsMega);
        Assert.Equal(400, table.Health);
        Assert.Equal(40, table.Damage);

        NavFlag flag = ReproZombieState.ToNavFlags(world)[0];
        Assert.Equal(3, flag.Vertices.Length);
        Assert.Equal(3, flag.Triangles.Length);
        Assert.True(flag.ContainsXZ(new Vector3(10f, 0f, 10f)));
    }

    [Fact]
    public void VectorsSurviveTheirRounding()
    {
        Assert.Equal(new[] { 1.2346f, 0f, -3f }, ReproVector.From(new Vector3(1.23456f, 0f, -3f)));
        Assert.Equal(Vector3.Zero, ReproVector.To(null));
        Assert.Equal(Vector3.Zero, ReproVector.To(new[] { 1f, 2f }));
        Assert.Equal(new Vector3(1f, 2f, 3f), ReproVector.To(new[] { 1f, 2f, 3f }));
        Assert.Equal(float.PositiveInfinity, ReproVector.Round(float.PositiveInfinity));
        Assert.Equal("(1.23, 0, -3)", ReproVector.Describe(new[] { 1.2346f, 0f, -3f }));
        Assert.Equal("(none)", ReproVector.Describe(null));

        var points = new List<Vector3>();
        ReproVector.Unflatten(ReproVector.Flatten(new[] { new Vector3(1f, 2f, 3f) }), points);
        Assert.Equal(new Vector3(1f, 2f, 3f), Assert.Single(points));
        ReproVector.Unflatten(null, points);
        Assert.Empty(points);
        Assert.Throws<ArgumentNullException>(() => ReproVector.Flatten(null!));
        Assert.Throws<ArgumentNullException>(() => ReproVector.Unflatten(null, null!));
    }
}
