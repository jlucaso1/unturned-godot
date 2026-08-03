using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Repro;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The watcher decides, without being told what the bug is, that something is worth capturing. Its
// false positives cost a dump nobody needed; its false negatives cost the bug. Both are pinned here.
public class ReproWatcherTests
{
    private const float Dt = 0.08f;

    private static ZombieSystem Idle(out ZombieInstance zombie)
    {
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 1 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(10f, 0f, -10f)) },
            new ReproRandom(1));
        zombie = system.Zombies[0];
        return system;
    }

    // Turning and turning while going nowhere: the shape of the report this was built for.
    [Fact]
    public void AZombieSpinningInPlaceIsNoticed()
    {
        ZombieSystem system = Idle(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;

        var watcher = new ReproWatcher();
        bool fired = false;
        string reason = "";
        Vector3 focus = default;
        for (int i = 0; i < 60 && !fired; i++)
        {
            zombie.Yaw = Mathf.Wrap(zombie.Yaw + 45f, 0f, 360f);
            fired = watcher.Poll(system, Dt, out reason, out focus);
        }
        Assert.True(fired);
        Assert.Contains("auto:spin", reason, StringComparison.Ordinal);
        Assert.Equal(zombie.Position, focus);

        // One capture per incident: the cooldown keeps a stuck zombie from writing a dump per tick.
        for (int i = 0; i < 60; i++)
        {
            zombie.Yaw = Mathf.Wrap(zombie.Yaw + 45f, 0f, 360f);
            Assert.False(watcher.Poll(system, Dt, out _, out _));
        }
    }

    // Making no progress on a route it keeps being handed. Driven through real ticks against a world
    // that refuses to move the body, because the value this rule reads is one the brain ZEROES the
    // instant it reaches its own timeout — a rule written against the timeout itself can never fire in
    // a session, and a test that assigns the field never notices.
    [Fact]
    public void AZombieWedgedAgainstItsRouteIsNoticed()
    {
        var flags = new List<NavFlag> { ReproWorlds.FlatField() };
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "Test", Damage = 1 } },
            new[] { ReproWorlds.Bound() }, ReproWorlds.FlatGround, flags)
        {
            // A route exists and the body cannot follow it: the shape of a zombie in a doorway.
            PathQuery = (from, to, path, radius) =>
            {
                path.Clear();
                path.Add(from);
                path.Add(to);
                return true;
            },
            PathReady = () => true,
            MoveResolver = (from, to, radius) => from,
            GroundSnap = (Vector3 position, out float y) =>
            {
                y = 0f;
                return true;
            },
        };
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(10f, 0f, -10f)) },
            new ReproRandom(1));

        var watcher = new ReproWatcher();
        var player = new[] { ReproWorlds.Player(new Vector3(20f, 0f, 20f)) };
        bool fired = false;
        string reason = "";
        for (int i = 0; i < 120 && !fired; i++)
        {
            system.Tick(player, Dt);
            fired = watcher.Poll(system, Dt, out reason, out _);
        }
        Assert.True(fired, "a zombie that never moved off its route went unnoticed");
        Assert.Contains("auto:stuck", reason, StringComparison.Ordinal);
    }

    // The trap the rule above exists to avoid, pinned so it cannot come back: a threshold set to the
    // brain's own timeout is never reachable, because the brain zeroes the counter on the tick it
    // gets there. A watcher configured that way watches nothing.
    [Fact]
    public void ARuleWrittenAgainstTheTimeoutItselfCanNeverFire()
    {
        var options = new ReproWatcherOptions();
        Assert.True(options.MaxBlockedRouteTime < ZombieSystem.BlockedRouteTimeout,
            "the stuck rule must sit below the value the brain resets at");
        Assert.Equal(ZombieSystem.BlockedRouteTimeout - UnturnedGodot.Net.ServerSimulation.TickRate,
            options.MaxBlockedRouteTime, 4);
    }

    // A zombie that is chasing someone turns a lot and covers ground. That is not a bug.
    [Fact]
    public void AnOrdinaryChaseIsNotABugReport()
    {
        ZombieSystem system = Idle(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;

        var watcher = new ReproWatcher();
        for (int i = 0; i < 200; i++)
        {
            zombie.Yaw = Mathf.Wrap(zombie.Yaw + 20f, 0f, 360f);
            zombie.Position += new Vector3(0.44f, 0f, 0f);
            Assert.False(watcher.Poll(system, Dt, out _, out _));
        }
    }

    [Fact]
    public void SettledZombiesAreNotWatchedAtAll()
    {
        ZombieSystem system = Idle(out ZombieInstance zombie);
        var watcher = new ReproWatcher();
        for (int i = 0; i < 200; i++)
        {
            zombie.Yaw = Mathf.Wrap(zombie.Yaw + 90f, 0f, 360f);
            Assert.False(watcher.Poll(system, Dt, out _, out _));
        }
    }

    // A population that changed under the watcher (a level change) leaves nothing behind.
    [Fact]
    public void TracksForVanishedZombiesAreDropped()
    {
        ZombieSystem system = Idle(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        var watcher = new ReproWatcher(new ReproWatcherOptions { WindowSeconds = 0.1f });
        watcher.Poll(system, Dt, out _, out _);
        watcher.Poll(system, Dt, out _, out _);

        system.RestoreState(new ZombieSystemState());
        Assert.False(watcher.Poll(system, Dt, out _, out _));
    }

    [Fact]
    public void ItRefusesAMissingSystem() =>
        Assert.Throws<ArgumentNullException>(() =>
            new ReproWatcher().Poll(null!, Dt, out _, out _));
}
