using System;
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

    // Making no progress on a route for longer than the brain's own patience with it.
    [Fact]
    public void AZombieWedgedAgainstItsRouteIsNoticed()
    {
        ZombieSystem system = Idle(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;
        zombie.BlockedRouteTime = ZombieSystem.BlockedRouteTimeout;

        var watcher = new ReproWatcher();
        bool fired = false;
        string reason = "";
        for (int i = 0; i < 60 && !fired; i++)
            fired = watcher.Poll(system, Dt, out reason, out _);
        Assert.True(fired);
        Assert.Contains("auto:stuck", reason, StringComparison.Ordinal);
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
