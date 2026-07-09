using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieSystemTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 5f;
        return true;
    }

    private static bool NoGround(float x, float z, out float y)
    {
        y = 0f;
        return false;
    }

    private static ZombieTable Table(bool mega = false, byte damage = 10) => new()
    {
        Name = mega ? "Special" : "Civilian",
        IsMega = mega,
        Health = mega ? (ushort)2999 : (ushort)100,
        Damage = damage,
    };

    // One big bound centered at origin (Godot coords) plus a far-away second bound.
    private static List<NavBound> TwoBounds() => new()
    {
        new NavBound { Center = new Vector3(0, 140, 0), Size = new Vector3(200, 300, 200) },
        new NavBound { Center = new Vector3(1000, 140, 0), Size = new Vector3(200, 300, 200) },
    };

    // Spawnpoints carry UNITY coordinates (straight from Animals.dat), so Z is pre-flipped here
    // to land on the intended Godot position.
    private static ZombieSpawnpointData At(float x, float z, byte type = 0) =>
        new(type, new Vector3(x, 5f, -z));

    private static ZombieSystem SpawnOne(out ZombieInstance zombie,
        List<NavBound>? bounds = null, ZombieTable? table = null, int seed = 1)
    {
        var system = new ZombieSystem(new[] { table ?? Table() }, bounds ?? TwoBounds(), FlatGround);
        system.Spawn(new[] { At(0, 0) }, new Random(seed));
        zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal; // pin the speed for deterministic motion asserts
        zombie.Yaw = 0f; // face +Z so approaches along the X axis never hit the sneak-behind rule
        return system;
    }

    private static ZombiePlayerView Player(byte id, Vector3 pos,
        UnturnedGodot.Player.EPlayerStance stance = UnturnedGodot.Player.EPlayerStance.Stand,
        bool moving = false) => new(id, pos, stance, moving);

    [Fact]
    public void Spawn_TakesSpawnChanceOfEligiblePoints()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        var spawns = new List<ZombieSpawnpointData>();
        for (int i = 0; i < 8; i++)
            spawns.Add(At(i * 5, 0));
        system.Spawn(spawns, new Random(1));

        Assert.Equal(2, system.Zombies.Count); // ceil(8 * 0.25)
        Assert.All(system.Zombies, z => Assert.Equal(0, z.Bound));
        Assert.All(system.Zombies, z => Assert.Equal(5f, z.Position.Y)); // ground-clamped
        Assert.Equal(system.Zombies.Count, system.Zombies.Select(z => z.Position.X).Distinct().Count());
    }

    [Fact]
    public void Spawn_RespectsFlagMaxZombies()
    {
        List<NavBound> bounds = TwoBounds();
        bounds[0].MaxZombies = 1;
        var system = new ZombieSystem(new[] { Table() }, bounds, FlatGround);
        system.Spawn(Enumerable.Range(0, 40).Select(i => At(i, 0)).ToList(), new Random(1));
        Assert.Single(system.Zombies);
    }

    [Fact]
    public void Spawn_SkipsNoSpawnBoundsAndStrayPoints()
    {
        List<NavBound> bounds = TwoBounds();
        bounds[0].SpawnZombies = false;
        var system = new ZombieSystem(new[] { Table() }, bounds, FlatGround);
        system.Spawn(new[]
        {
            At(0, 0),        // in bound 0: spawning disabled there
            At(5000, 5000),  // outside every bound: dropped
        }, new Random(1));
        Assert.Empty(system.Zombies);
    }

    [Fact]
    public void Spawn_MegaTableAlwaysRollsMega()
    {
        var system = new ZombieSystem(new[] { Table(mega: true) }, TwoBounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 12).Select(i => At(i, 0)).ToList(), new Random(7));
        Assert.All(system.Zombies, z => Assert.Equal(EZombieSpeciality.Mega, z.Speciality));
    }

    [Fact]
    public void Spawn_NormalTableRollsAllSpecialities()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        // 200 points x 0.25 = 50 zombies under the default 64 cap: plenty for every roll to appear.
        var spawns = new List<ZombieSpawnpointData>();
        for (int i = 0; i < 200; i++)
            spawns.Add(At((i % 20) * 9 - 90, (i / 20) * 9 - 90));
        system.Spawn(spawns, new Random(42));

        int[] byKind = new int[4];
        foreach (ZombieInstance z in system.Zombies)
            byKind[(int)z.Speciality]++;
        Assert.True(byKind[(int)EZombieSpeciality.Normal] > 0);
        Assert.True(byKind[(int)EZombieSpeciality.Crawler] > 0);
        Assert.True(byKind[(int)EZombieSpeciality.Sprinter] > 0);
        Assert.Equal(0, byKind[(int)EZombieSpeciality.Mega]);
        Assert.True(byKind[(int)EZombieSpeciality.Normal] > byKind[(int)EZombieSpeciality.Crawler]);
    }

    [Fact]
    public void Spawn_ClothingRollsFollowSlotChances()
    {
        ZombieTable table = Table();
        table.Slots.Add((1f, new List<ushort> { 151, 152 })); // shirt: always worn
        table.Slots.Add((0f, new List<ushort> { 153 }));      // pants: never worn
        table.Slots.Add((1f, new List<ushort>()));            // hat: chance up but nothing to wear

        var system = new ZombieSystem(new[] { table }, TwoBounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 12).Select(i => At(i * 3, 0)).ToList(), new Random(3));

        Assert.All(system.Zombies, z =>
        {
            Assert.InRange(z.Shirt, 0, 1);
            Assert.Equal(byte.MaxValue, z.Pants);
            Assert.Equal(byte.MaxValue, z.Hat);
            Assert.Equal(byte.MaxValue, z.Gear); // no fourth slot authored
        });
    }

    [Fact]
    public void Spawn_WithoutGround_KeepsAuthoredHeight()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), NoGround);
        system.Spawn(new[] { At(0, 0) }, new Random(1));
        Assert.Equal(5f, Assert.Single(system.Zombies).Position.Y);
    }

    [Fact]
    public void UnalertedZombie_StaysIdle()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // Standing radius is 12: a player at 15 m stays unnoticed.
        system.Tick(new[] { Player(1, new Vector3(15, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);
    }

    [Fact]
    public void StandingPlayerInsideRadius_TriggersChase()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.Equal(1, zombie.TargetPlayer);
    }

    [Fact]
    public void DetectionRunsAtItsOwnCadence_NotEveryTick()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.05f); // timer below 0.1: no scan yet
        Assert.Equal(EZombieState.Idle, zombie.State);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.06f); // accumulates past 0.1: scan
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    [Fact]
    public void CrouchedPlayer_HasSmallerRadius()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Crouch) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State); // crouch radius is 6
        system.Tick(new[] { Player(1, new Vector3(5, 5, 0), UnturnedGodot.Player.EPlayerStance.Crouch) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    [Fact]
    public void SneakingBehindAZombieFacingAway_StaysUndetected()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // Player 3 m west of the zombie; zombie faces east (away from the player).
        zombie.Yaw = MathF.Atan2(1f, 0f);
        Vector3 playerPos = new(-3, 5, 0);

        system.Tick(new[] { Player(1, playerPos, UnturnedGodot.Player.EPlayerStance.Crouch) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State); // sneaking behind: safe

        system.Tick(new[] { Player(1, playerPos, UnturnedGodot.Player.EPlayerStance.Sprint) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State); // sprinting is never sneaking
    }

    [Fact]
    public void PlayerOutsideEveryBound_NeverAlerts()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(400, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
    }

    [Fact]
    public void PlayerInAnotherBound_DoesNotAlert()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // Inside bound 1, and (impossibly) close — cross-bound alerts must still not happen.
        system.Tick(new[] { Player(1, new Vector3(1000, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
    }

    [Fact]
    public void Alert_KeepsTheNearestTarget()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var far = Player(1, new Vector3(10, 5, 0));
        var near = Player(2, new Vector3(4, 5, 0));

        system.Tick(new[] { far }, 0.1f);
        Assert.Equal(1, zombie.TargetPlayer);
        system.Tick(new[] { far, near }, 0.1f); // the closer player steals the aggro
        Assert.Equal(2, zombie.TargetPlayer);
        system.Tick(new[] { near, far }, 0.1f); // the farther one cannot steal it back
        Assert.Equal(2, zombie.TargetPlayer);
    }

    [Fact]
    public void ReAlertBySamePlayer_IsStable()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(1, zombie.TargetPlayer);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    [Fact]
    public void Chase_MovesStraightAtTheTargetAtSpeed()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);

        float before = zombie.Position.DistanceTo(player.Position);
        system.Tick(new[] { player }, 0.1f);
        float after = zombie.Position.DistanceTo(player.Position);

        Assert.Equal(0.55f, before - after, 2); // normal zombie: 5.5 m/s x 0.1 s
        Assert.Equal(0f, zombie.Position.Z, 3); // dead straight along the X axis
        Assert.Equal(MathF.Atan2(1, 0), zombie.Yaw, 3); // facing the target
    }

    [Theory]
    [InlineData(EZombieSpeciality.Normal, 5.5f)]
    [InlineData(EZombieSpeciality.Crawler, 3f)]
    [InlineData(EZombieSpeciality.Sprinter, 6.5f)]
    [InlineData(EZombieSpeciality.Mega, 6f)]
    public void SpecialitySpeeds_MatchUnturned(EZombieSpeciality speciality, float speed)
    {
        Assert.Equal(speed, new ZombieInstance { Speciality = speciality }.Speed);
    }

    [Fact]
    public void WithinAttackRange_AttacksAtTheAttackCadence()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<(ushort Zombie, byte Player, byte Damage)>();
        system.OnAttack += (z, player, damage) => hits.Add((z.Id, player, damage));
        var player = Player(1, new Vector3(1.5f, 5, 0));

        system.Tick(new[] { player }, 0.1f); // alerted and already in range: first swing
        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.Equal((zombie.Id, (byte)1, (byte)10), Assert.Single(hits));

        for (int i = 0; i < 4; i++)
            system.Tick(new[] { player }, 0.1f); // 0.4 s: still inside the 0.5 s swing
        Assert.Single(hits);

        system.Tick(new[] { player }, 0.1f); // the 0.5 s cadence elapses here or one float-rounding
        system.Tick(new[] { player }, 0.1f); // tick later: exactly one more swing either way
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void TargetSteppingOutOfRange_ResumesTheChase()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(1.5f, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Attack, zombie.State);

        system.Tick(new[] { Player(1, new Vector3(8, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    [Fact]
    public void TargetLeavingTheBound_SendsTheZombieHome()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Vector3 home = zombie.Home;

        // Drag the zombie away from home, then the player escapes the bound entirely.
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.True(zombie.Position.X > 1f);

        system.Tick(new[] { Player(1, new Vector3(400, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Return, zombie.State);
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);

        for (int i = 0; i < 30; i++)
            system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.True(zombie.Position.DistanceTo(home) <= ZombieSystem.ArriveRadius + 0.01f);
    }

    [Fact]
    public void TargetDisconnecting_SendsTheZombieHome()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);

        system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f);
        Assert.Equal(EZombieState.Return, zombie.State);
    }

    [Fact]
    public void ReturningZombie_CanBeReAlerted()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.State = EZombieState.Return;
        zombie.Position = new Vector3(5, 5, 0);
        system.Tick(new[] { Player(1, new Vector3(8, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    [Fact]
    public void MoveTowards_AtTheExactTarget_DoesNotJitter()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.State = EZombieState.Return; // already standing at home
        float yaw = zombie.Yaw;
        system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(zombie.Home, zombie.Position);
        Assert.Equal(yaw, zombie.Yaw); // a degenerate direction must not spin the zombie
    }

    [Fact]
    public void Chase_WithoutGroundSample_KeepsHeight()
    {
        var system = new ZombieSystem(new[] { Table() },
            new List<NavBound> { TwoBounds()[0] }, NoGround);
        system.Spawn(new[] { At(0, 0) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Yaw = 0f;

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(5f, zombie.Position.Y); // authored height sticks when there is no heightfield
    }

    [Theory]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Sprint, false, 20f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Sprint, true, 22f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Stand, false, 12f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Stand, true, 13.2f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Crouch, false, 6f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Crouch, true, 6.6f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Prone, false, 3f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Prone, true, 3.3f)]
    public void DetectionRadii_MatchPlayerStance(
        UnturnedGodot.Player.EPlayerStance stance, bool moving, float expected)
    {
        Assert.Equal(expected, ZombieDetection.RadiusFor(stance, moving), 4);
    }

    [Fact]
    public void DetectionRadius_ClampsToTheAlertToolFloor()
    {
        Assert.Equal(1f, ZombieDetection.RadiusFor((UnturnedGodot.Player.EPlayerStance)0, false));
    }
}
