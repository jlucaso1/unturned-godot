using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// The measurement seam that separates the zombie brain from the netcode it runs inside.
//
// Worth its own tests because of what it is for. The host answers NetServer.Update's OnTick, so before
// this existed the whole simulation — detection, steering, the move resolver, the ground snap and the
// pathfinding — was reported as time spent on the wire. A seam that silently reported nothing would put
// the tick's most expensive work back into the wrong column while looking like a measurement, which is
// worse than having no counter at all; and one that reported the brain and the query as two separate
// costs when the second is inside the first would double-count the tick.
public class ZombieCostReportingTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 5f;
        return true;
    }

    private static List<NavBound> Bounds() => new()
    {
        new NavBound { Center = new Vector3(0, 140, 0), Size = new Vector3(200, 300, 200) },
    };

    private static ZombieSystem SpawnOne(out ZombieInstance zombie)
    {
        var system = new ZombieSystem(
            new[] { new ZombieTable { Name = "Civilian", Health = 100, Damage = 10 } },
            Bounds(), FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(0, 5f, 0)) }, new Random(1));
        zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Yaw = -90f; // face the player, so the chase advances from the first tick
        return system;
    }

    private static ZombiePlayerView Player() =>
        new(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Stand, false);

    // The production default. Nothing installed means nothing reported and nothing measured — the whole
    // point of leaving the field null rather than pointing it at a no-op sink.
    [Fact]
    public void WithNoSink_TheTickRunsAndReportsNothing()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };

        for (int i = 0; i < 4; i++)
            system.Tick(new[] { Player() }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State); // it really did run the work being measured
    }

    // One Brain report per tick, whatever the population is doing. A counter that fired per zombie would
    // make the mean per call describe a zombie while the report's name says it describes a tick.
    [Fact]
    public void TheBrainIsReportedOncePerTick()
    {
        ZombieSystem system = SpawnOne(out _);
        int brain = 0;
        system.Costs = (cost, _) =>
        {
            if (cost == EZombieCost.Brain)
                brain++;
        };

        for (int i = 0; i < 5; i++)
            system.Tick(new[] { Player() }, 0.1f);

        Assert.Equal(5, brain);
    }

    // The query is reported per granted repath, and it is reported even when it fails: a query that
    // returns no route has still spent the search, and hiding that would make an unroutable horde — the
    // expensive case — read as free.
    [Fact]
    public void AFailedQueryIsStillReported()
    {
        ZombieSystem system = SpawnOne(out _);
        int queries = 0, reported = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            return false;
        };
        system.Costs = (cost, _) =>
        {
            if (cost == EZombieCost.PathQuery)
                reported++;
        };

        for (int i = 0; i < 10; i++)
            system.Tick(new[] { Player() }, 0.1f);

        Assert.True(queries > 0, "the setup never reached a repath at all");
        Assert.Equal(queries, reported);
    }

    // The query runs inside the tick, so its interval has to be inside the tick's. This is what says the
    // two are nested rather than two independent readings that could be added together — reading the
    // report as Brain + PathQuery would then count the search twice.
    [Fact]
    public void TheQueryIsMeasuredInsideTheBrainNotBesideIt()
    {
        ZombieSystem system = SpawnOne(out _);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };

        long brain = 0, query = 0;
        bool sawQuery = false;
        system.Costs = (cost, ticks) =>
        {
            Assert.True(ticks >= 0, $"{cost} reported a negative interval");
            if (cost == EZombieCost.PathQuery)
            {
                query += ticks;
                sawQuery = true;
            }
            else
            {
                brain += ticks;
            }
        };

        system.Tick(new[] { Player() }, 0.1f); // the first tick alerts and paths immediately

        Assert.True(sawQuery, "the tick never issued a query");
        Assert.True(query <= brain, $"the query ({query}) outlasted the tick containing it ({brain})");
    }

    // Installing a sink partway through a session must not report the ticks that ran before it, and
    // removing one must stop the reporting rather than leave a dangling interval behind.
    [Fact]
    public void TheSinkCanBeInstalledAndRemovedMidSession()
    {
        ZombieSystem system = SpawnOne(out _);
        int reports = 0;

        system.Tick(new[] { Player() }, 0.1f);
        Assert.Equal(0, reports);

        system.Costs = (_, _) => reports++;
        system.Tick(new[] { Player() }, 0.1f);
        Assert.Equal(1, reports);

        system.Costs = null;
        system.Tick(new[] { Player() }, 0.1f);
        Assert.Equal(1, reports);
    }
}
