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

    // ---- Spawning ----------------------------------------------------------------------------

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
        Assert.All(system.Zombies, z => Assert.InRange(z.Move, 0, 3));
        Assert.All(system.Zombies, z => Assert.InRange(z.Idle, 0, 2));
        Assert.True(system.Zombies.Select(z => z.Move).Distinct().Count() > 1); // actually rolled
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

    // ---- Detection ---------------------------------------------------------------------------

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
        zombie.Yaw = -90f; // facing +X: (-sin, -cos) = (1, 0)
        Vector3 playerPos = new(-3, 5, 0);

        system.Tick(new[] { Player(1, playerPos, UnturnedGodot.Player.EPlayerStance.Crouch) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State); // sneaking behind: safe

        system.Tick(new[] { Player(1, playerPos, UnturnedGodot.Player.EPlayerStance.Sprint) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State); // sprinting is never sneaking
    }

    [Fact]
    public void BlockedLineOfSight_PreventsDetection()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        Vector3 rayFrom = default;
        system.VisionBlocked = (from, to) =>
        {
            rayFrom = from;
            return true; // a wall between every zombie and everyone
        };
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(zombie.Position + Vector3.Up, rayFrom); // the ray leaves from the zombie's eyes

        system.VisionBlocked = (from, to) => false; // wall gone: the alert lands
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
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

    // ---- Approach paths ------------------------------------------------------------------------

    [Fact]
    public void ApproachPaths_SpreadTheHordeByAgro()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 24).Select(i => At(i - 3, 0)).ToList(), new Random(5));
        foreach (ZombieInstance z in system.Zombies)
        {
            z.Speciality = EZombieSpeciality.Normal;
            z.Yaw = 0f;
        }

        system.Tick(new[] { Player(1, new Vector3(0, 5, 0)) }, 0.1f);

        List<ZombieInstance> hunting = system.Zombies.Where(z => z.TargetPlayer == 1).ToList();
        Assert.True(hunting.Count >= 3);
        // player.agro % 3: every third alerted zombie rushes; the rest drift to a side.
        Assert.Contains(hunting, z => z.Path == EZombiePath.Rush);
        Assert.Contains(hunting, z => z.Path is EZombiePath.Left or EZombiePath.Right);
        int rushes = hunting.Count(z => z.Path == EZombiePath.Rush);
        Assert.InRange(rushes, hunting.Count / 3, (hunting.Count / 3) + 1);
    }

    [Fact]
    public void MegaZombie_AlwaysRushes()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, table: Table(mega: true));
        zombie.Speciality = EZombieSpeciality.Mega;
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombiePath.Rush, zombie.Path);
    }

    // ---- Chasing -------------------------------------------------------------------------------

    [Fact]
    public void Chase_MovesStraightAtTheTargetAtSpeed()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f); // first zombie on the player: agro 0 -> RUSH path

        float before = zombie.Position.DistanceTo(player.Position);
        system.Tick(new[] { player }, 0.1f);
        float after = zombie.Position.DistanceTo(player.Position);

        Assert.Equal(0.55f, before - after, 2); // normal zombie: 5.5 m/s x 0.1 s
        Assert.Equal(0f, zombie.Position.Z, 3); // dead straight along the X axis
    }

    [Fact]
    public void Turning_IsRateLimitedTo720DegreesPerSecond()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = 90f; // facing away; the target direction sits at yaw -90
        // Sprinting: never sneaking, so facing away cannot shield the player from detection.
        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        system.Tick(new[] { player }, 0.05f); // below the detection cadence: still idle
        system.Tick(new[] { player }, 0.05f); // 0.1 s accumulated: alert + first move step
        // One 0.05 s step turns at most 36°: nowhere near the 180° flip yet.
        Assert.True(MathF.Abs(Mathf.Wrap(zombie.Yaw - -90f, -180f, 180f)) > 30f);
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { player }, 0.05f);
        Assert.Equal(-90f, Mathf.Wrap(zombie.Yaw, -180f, 180f), 1); // converged on the target
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

    [Theory]
    [InlineData(EZombieSpeciality.Normal, 1f, 2.1f)]  // dedicated NORMAL: half range
    [InlineData(EZombieSpeciality.Crawler, 2f, 2.1f)]
    [InlineData(EZombieSpeciality.Sprinter, 2f, 2.1f)]
    [InlineData(EZombieSpeciality.Mega, 4f, 3.15f)]   // x2 horizontal, x1.5 vertical
    public void AttackRanges_MatchZombieCs(EZombieSpeciality speciality, float horizontal, float vertical)
    {
        var zombie = new ZombieInstance { Speciality = speciality };
        Assert.Equal(horizontal, zombie.AttackRange, 4);
        Assert.Equal(vertical, zombie.VerticalAttackRange, 4);
    }

    [Theory]
    [InlineData(EZombieSpeciality.Normal, 0.4f)]
    [InlineData(EZombieSpeciality.Crawler, 0.4f)]
    [InlineData(EZombieSpeciality.Mega, 0.75f)]
    public void CapsuleRadii_MatchTheCharacterController(EZombieSpeciality speciality, float radius)
    {
        Assert.Equal(radius, new ZombieInstance { Speciality = speciality }.Radius);
    }

    [Fact]
    public void SidePathZombie_VeersInsteadOfTakingTheStraightLine()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f); // alert inside the 12 m radius
        Assert.Equal(EZombieState.Chase, zombie.State);
        zombie.Path = EZombiePath.Left; // force the side-approach branch

        var player = Player(1, new Vector3(20, 5, 0)); // target ahead along the X axis
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { player }, 0.1f);
        Assert.NotEqual(0f, zombie.Position.Z); // drifts off the straight X-axis line

        ZombieSystem rightSystem = SpawnOne(out ZombieInstance rightZombie, seed: 3);
        rightSystem.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        rightZombie.Path = EZombiePath.Right;
        for (int i = 0; i < 5; i++)
            rightSystem.Tick(new[] { player }, 0.1f);
        Assert.NotEqual(0f, rightZombie.Position.Z);
        // The two side paths drift to opposite sides of the rush line.
        Assert.True(zombie.Position.Z * rightZombie.Position.Z < 0f);
    }

    [Fact]
    public void WorldColliders_BlockAndSlideTheStep()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // A wall at x = 0.25: the host's resolver clamps any step that would cross it (the
        // CharacterController slide), so the chase can never pass through world geometry.
        system.MoveResolver = (from, to, radius) =>
        {
            Assert.Equal(0.4f, radius); // the zombie's capsule radius reaches the resolver
            return to.X > 0.25f ? new Vector3(0.25f, to.Y, to.Z) : to;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 30; i++)
            system.Tick(new[] { player }, 0.1f);
        Assert.True(zombie.Position.X <= 0.25f + 0.001f, $"walked through the wall: {zombie.Position.X}");
    }

    [Fact]
    public void BlockedHeadOn_TheZombieHugsAroundTheObstacle()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // A tree trunk: a solid circle of radius 1 at (5, 0). This resolver does NOT slide (the
        // worst case: a dead stop at the surface), so only the detour can get the zombie past it.
        Vector3 trunk = new(5, 5, 0);
        system.MoveResolver = (from, to, radius) =>
        {
            float dx = to.X - trunk.X;
            float dz = to.Z - trunk.Z;
            float dist = MathF.Sqrt((dx * dx) + (dz * dz));
            float minDist = 1f + radius;
            if (dist >= minDist)
                return to;
            return from; // dead stop, no slide at all
        };

        var player = Player(1, new Vector3(10, 5, 0)); // straight behind the trunk
        for (int i = 0; i < 80; i++)
            system.Tick(new[] { player }, 0.1f);

        // The sticky tangent detour walked it around the trunk and back on course.
        Assert.True(zombie.Position.X > 6.5f, $"stuck at {zombie.Position}");
    }

    [Fact]
    public void FullyWalledIn_TheZombieStaysPutWithoutJitter()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        // A dozen zombies so the sticky detour side gets rolled both ways at least once.
        system.Spawn(Enumerable.Range(0, 48).Select(i => At((i % 8) * 3, i / 8 * 3)).ToList(), new Random(6));
        foreach (ZombieInstance z in system.Zombies)
        {
            z.Speciality = EZombieSpeciality.Normal;
            z.Yaw = 0f;
        }
        system.MoveResolver = (from, to, radius) => from; // sealed in: nothing moves, ever

        var player = Player(1, new Vector3(11, 5, 11));
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { player }, 0.1f);

        List<ZombieInstance> blocked = system.Zombies.Where(z => z.DetourSide != 0).ToList();
        Assert.True(blocked.Count >= 2);
        Assert.Contains(blocked, z => z.DetourSide == -1); // both sides get rolled
        Assert.Contains(blocked, z => z.DetourSide == 1);
        // The failed tangent never teleports anyone: everyone is exactly where they spawned.
        Assert.All(system.Zombies, z => Assert.Equal(z.Home, z.Position));
    }

    [Fact]
    public void ZombiesQueue_InsteadOfStackingInsideEachOther()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        // Eight points around the same spot roll ceil(8 x 0.25) = 2 zombies.
        system.Spawn(Enumerable.Range(0, 8).Select(i => At(4 + (i * 0.01f), 0)).ToList(), new Random(2));
        Assert.Equal(2, system.Zombies.Count);
        ZombieInstance mover = system.Zombies[0];
        ZombieInstance blocker = system.Zombies[1];
        mover.Speciality = EZombieSpeciality.Normal;
        mover.Yaw = 0f;
        mover.Position = new Vector3(8, 5, 0);
        blocker.Speciality = EZombieSpeciality.Normal;
        blocker.Yaw = 0f;
        blocker.Position = new Vector3(1.5f, 5, 0); // parked between the mover and the player

        var player = Player(1, new Vector3(0, 5, 0));
        for (int i = 0; i < 60; i++)
            system.Tick(new[] { player }, 0.1f);

        // CharacterController semantics: the mover is pushed out of the blocker's capsule, so the
        // two keep at least 0.4 + 0.4 m between centres — a queue, not a stack.
        float separation = new Vector2(
            mover.Position.X - blocker.Position.X, mover.Position.Z - blocker.Position.Z).Length();
        Assert.True(separation >= 0.8f - 0.01f, $"zombies overlapped: {separation}");
    }

    // ---- Navmesh pathfinding (the Seeker port) ----------------------------------------------------

    [Fact]
    public void PathQuery_FollowsWaypointsAroundACorner()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // An L-shaped route: the navmesh sends the zombie 6 m sideways before turning to the
        // target. Like a real navmesh, the corner only appears while the zombie is still on the
        // first leg — a repath from beyond it routes straight.
        var corner = new Vector3(0, 5, 6);
        int queries = 0;
        system.PathQuery = (from, to, path) =>
        {
            queries++;
            path.Add(from);
            if (new Vector2(from.X - corner.X, from.Z - corner.Z).Length() > 1.5f && from.X < 2f)
                path.Add(corner);
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f); // alert + first move (paths immediately)
        Assert.Equal(1, queries);

        for (int i = 0; i < 3; i++)
            system.Tick(new[] { player }, 0.1f);
        Assert.Equal(1, queries); // 0.3 s in: still inside the repath window
        for (int i = 0; i < 3; i++)
            system.Tick(new[] { player }, 0.1f);
        // Past the 0.5 s repathRate: exactly one recalculation, not one per tick.
        Assert.Equal(2, queries);

        // Following the waypoint means walking TOWARD +Z (the corner), not straight down the X axis.
        Assert.True(zombie.Position.Z > 1.5f, $"ignored the waypoint: {zombie.Position}");

        // Long run: passes the corner, then heads to the player and reaches attack range.
        for (int i = 0; i < 40; i++)
            system.Tick(new[] { player }, 0.1f);
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    [Fact]
    public void PartialRoute_HoldsAtItsEndInsteadOfBeeliningThroughWalls()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // The target is unreachable (a raised porch): the navmesh route ends at the doorway,
        // 5 m short of the player. The zombie must hold at the route's end — walking straight at
        // the raw destination from there means pushing into the wall.
        var doorway = new Vector3(5, 5, 0);
        system.PathQuery = (from, to, path) =>
        {
            path.Add(from);
            path.Add(doorway);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 40; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State); // never in attack range
        Assert.True(zombie.Position.X <= doorway.X + 0.05f,
            $"beelined past the route's end: {zombie.Position}");
        Assert.True(zombie.Position.X > 4f, "never reached the route's end");
    }

    [Fact]
    public void SidePathZombies_QueryTheRawTarget_NeverTheOffsetPoint()
    {
        // Offsetting the pathfinding DESTINATION made its navmesh snap flip between the two sides
        // of a wall on every repath (zombies entering houses oscillated back out). The drift must
        // live in the steering only: every query goes to the target's exact position.
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path) =>
        {
            destinations.Add(to);
            path.Add(from);
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        zombie.Path = EZombiePath.Left; // force the drifting approach
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.True(destinations.Count >= 2);
        Assert.All(destinations, d => Assert.Equal(player.Position, d)); // raw, never ±1 m
        // And the drift is visible in the motion: the zombie leaves the straight X-axis line.
        Assert.NotEqual(0f, zombie.Position.Z);
    }

    [Fact]
    public void CarrotFollowing_HoldsTheCorridorThroughTheCorner()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // An L-route with the corner at (6, 0, 6): proper polyline following passes NEAR the
        // corner instead of cutting the diagonal through the "wall".
        var corner = new Vector3(6, 5, 6);
        system.PathQuery = (from, to, path) =>
        {
            path.Add(from);
            if (new Vector2(from.X - corner.X, from.Z - corner.Z).Length() > 1.2f && from.Z < 5f)
                path.Add(corner);
            path.Add(to);
            return true;
        };

        // Sprinting: inside the 20 m radius and never shielded by the sneak-behind rule.
        var player = Player(1, new Vector3(0, 5, 6), UnturnedGodot.Player.EPlayerStance.Sprint);
        zombie.Position = new Vector3(6, 5, 0); // on the first leg of the L
        zombie.Home = zombie.Position;
        float closestToCorner = float.MaxValue;
        for (int i = 0; i < 60; i++)
        {
            system.Tick(new[] { player }, 0.1f);
            closestToCorner = MathF.Min(closestToCorner,
                new Vector2(zombie.Position.X - corner.X, zombie.Position.Z - corner.Z).Length());
        }
        Assert.True(closestToCorner < 1.5f, $"cut the corner: nearest pass {closestToCorner:F2}m");
        Assert.Equal(EZombieState.Attack, zombie.State); // and still reached the target
    }

    [Fact]
    public void PathQuery_WithNoRoute_FallsBackToTheStraightSeek()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.PathQuery = (from, to, path) => false; // nothing reachable on the navmesh

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        float before = zombie.Position.DistanceTo(player.Position);
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(0.55f, before - zombie.Position.DistanceTo(player.Position), 2); // straight seek
    }

    [Fact]
    public void Retargeting_RequestsAFreshPath()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path) =>
        {
            destinations.Add(to);
            path.Add(to);
            return true;
        };

        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Single(destinations);

        // A closer player steals the target: the path resets instead of waiting out the 0.5 s.
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)), Player(2, new Vector3(4, 5, 0)) }, 0.1f);
        Assert.Equal(2, destinations.Count);
        Assert.Equal(4f, destinations[1].X, 1f);
    }

    // ---- Navmesh spawn filtering (checkNavigation) ------------------------------------------------

    private static List<NavFlag> NavmeshBoxes() => new()
    {
        // The non-expanded box of bound 0: 64 m smaller, like the real files.
        new NavFlag { Center = new Vector3(0, 140, 0), Size = new Vector3(136, 236, 136) },
    };

    [Fact]
    public void Spawnpoints_OutsideTheNavmeshBox_AreDropped()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround, NavmeshBoxes());
        system.Spawn(new[]
        {
            At(0, 0),   // on the navmesh: kept
            At(90, 0),  // inside the expanded bound (100) but outside the navmesh box (68): dropped
            At(80, 0),
            At(85, 0),
        }, new Random(1));
        Assert.Single(system.Zombies); // ceil(1 * 0.25): only the on-mesh point was eligible
        Assert.Equal(0f, system.Zombies[0].Position.X);
    }

    [Fact]
    public void RetreatValidation_UsesTheNavmeshBoxes()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround, NavmeshBoxes());
        system.Spawn(new[] { At(0, 0) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Yaw = 0f;
        zombie.Position = new Vector3(60, 5, 0); // 8 m shy of the navmesh edge at 68

        system.Tick(new[] { Player(1, new Vector3(50, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
        system.Tick(new[] { Player(1, new Vector3(400, 5, 0)) }, 0.1f); // escapes: leave(true)

        // Retreating away from the escapee (+X, off the mesh) must flip inward instead.
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.True(zombie.LeaveTo.X < zombie.Position.X);
        Assert.True(MathF.Abs(zombie.LeaveTo.X) <= 68f && MathF.Abs(zombie.LeaveTo.Z) <= 68f);
    }

    // ---- Attacking -----------------------------------------------------------------------------

    [Fact]
    public void WithinAttackRange_SwingsOnTheOneSecondCadence()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<(ushort Zombie, byte Player, byte Damage)>();
        system.OnAttack += (z, player, damage) => hits.Add((z.Id, player, damage));
        var player = Player(1, new Vector3(0.8f, 5, 0)); // inside the dedicated NORMAL 1 m range

        system.Tick(new[] { player }, 0.1f); // alert + the first swing starts
        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.Empty(hits); // the hit lands attackTime/2 = 0.25 s after the swing starts

        for (int i = 0; i < 3; i++)
            system.Tick(new[] { player }, 0.1f); // 0.3 s: the swing connects
        Assert.Equal((zombie.Id, (byte)1, (byte)10), Assert.Single(hits));

        for (int i = 0; i < 7; i++)
            system.Tick(new[] { player }, 0.1f); // the 1 s cadence: next swing barely starting
        Assert.Single(hits);
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { player }, 0.1f); // and its hit lands attackTime/2 later
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void CrawlerAndSprinter_ApplyTheirDamageMultipliers()
    {
        foreach ((EZombieSpeciality speciality, byte expected) in new[]
                 { (EZombieSpeciality.Crawler, (byte)20), (EZombieSpeciality.Sprinter, (byte)7) })
        {
            ZombieSystem system = SpawnOne(out ZombieInstance zombie);
            zombie.Speciality = speciality;
            var hits = new List<byte>();
            system.OnAttack += (z, player, damage) => hits.Add(damage);

            var player = Player(1, new Vector3(1.5f, 5, 0)); // inside their 2 m range
            for (int i = 0; i < 5; i++)
                system.Tick(new[] { player }, 0.1f);
            Assert.Equal(expected, Assert.Single(hits)); // 10 x2 crawler / 10 x0.75 sprinter
        }
    }

    [Fact]
    public void HyperTerritory_Amplifies_Damage()
    {
        List<NavBound> bounds = TwoBounds();
        bounds[0].HyperAgro = true;
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, bounds: bounds);
        var hits = new List<byte>();
        system.OnAttack += (z, p, damage) => hits.Add(damage);

        var player = Player(1, new Vector3(0.8f, 5, 0));
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { player }, 0.1f);
        Assert.Equal(15, Assert.Single(hits)); // 10 x 1.5 hyper
    }

    [Fact]
    public void PlayerAboveTheVerticalRange_IsSafe()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<byte>();
        system.OnAttack += (z, p, damage) => hits.Add(damage);
        var rooftop = Player(1, new Vector3(0.5f, 8.5f, 0)); // 3.5 m overhead: beyond the 2.1 m reach
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { rooftop }, 0.1f);
        Assert.Empty(hits);
        Assert.NotEqual(EZombieState.Attack, zombie.State);
    }

    [Fact]
    public void TargetSteppingOutOfRange_ResumesTheChase()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(0.8f, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Attack, zombie.State);

        system.Tick(new[] { Player(1, new Vector3(8, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
    }

    // ---- Giving up (Zombie.leave) ---------------------------------------------------------------

    [Fact]
    public void TargetBeyond64Metres_MakesTheZombieGiveUp()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);

        // Still inside the bound, but beyond the 64 m hunt range.
        system.Tick(new[] { Player(1, new Vector3(90, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(byte.MaxValue, zombie.TargetPlayer);
        Assert.InRange(zombie.LeaveDelay, 3f, 6f); // leave(false): stand 3-6 s before retreating
    }

    [Fact]
    public void TargetLeavingEveryBound_MakesTheZombieGiveUpQuickly()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);

        system.Tick(new[] { Player(1, new Vector3(400, 5, 0)) }, 0.1f); // nav == 255
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.InRange(zombie.LeaveDelay, 0.5f, 1f); // leave(true)
    }

    [Fact]
    public void AfterTheLeaveDelay_TheZombieRetreatsAndSettles()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        for (int i = 0; i < 6; i++)
            system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f); // drag it off its spawn

        system.Tick(new[] { Player(1, new Vector3(400, 5, 0)) }, 0.1f); // target escapes the bounds
        Assert.Equal(EZombieState.Idle, zombie.State);
        Vector3 retreat = zombie.LeaveTo;
        // The retreat point lies ~16 m away from the escapee (+-8 m scatter), inside the territory.
        Assert.Equal(0, LevelNavigationData.TryGetBound(TwoBounds(), retreat));

        for (int i = 0; i < 200; i++)
            system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f); // delay elapses, the walk completes
        Assert.Equal(EZombieState.Idle, zombie.State);
        // Settled at the retreat point (the isMoving threshold), NOT back at its spawn home.
        float toRetreat = new Vector2(zombie.Position.X - retreat.X, zombie.Position.Z - retreat.Z).Length();
        Assert.True(toRetreat <= MathF.Sqrt(ZombieSystem.ArriveDistanceSquared) + 0.01f);
    }

    [Fact]
    public void RetreatPointOutsideTheTerritory_FlipsTowardTheTarget()
    {
        // The zombie hunts from x≈95, 5 m shy of its bound's edge at x=100, with the target deeper
        // inside: retreating 16 m AWAY from the target always exits the territory (103..119), so
        // the retreat flips toward the target instead.
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Position = new Vector3(95, 5, 0);
        system.Tick(new[] { Player(1, new Vector3(85, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);

        // The target blinks 75 m away (still in the bound): the 64 m rule fires leave(false).
        system.Tick(new[] { Player(1, new Vector3(20, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(0, LevelNavigationData.TryGetBound(TwoBounds(), zombie.LeaveTo));
        Assert.True(zombie.LeaveTo.X < zombie.Position.X); // flipped inward, toward the target
    }

    [Fact]
    public void RetreatWithNoRoomAtAll_StaysPut()
    {
        // A territory too small for any 16 m retreat: the zombie gives up in place.
        var bounds = new List<NavBound>
        {
            new() { Center = new Vector3(0, 140, 0), Size = new Vector3(8, 300, 8) },
        };
        ZombieSystem system = SpawnOne(out ZombieInstance zombie, bounds: bounds);
        system.Tick(new[] { Player(1, new Vector3(2, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);

        system.Tick(new[] { Player(1, new Vector3(400, 5, 0)) }, 0.1f);
        Assert.Equal(zombie.Position, zombie.LeaveTo);
    }

    [Fact]
    public void TargetDisconnecting_MakesTheZombieGiveUp()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);

        system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.InRange(zombie.LeaveDelay, 3f, 6f); // leave(false)
    }

    [Fact]
    public void ALeavingZombie_CanBeReAlerted()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f); // gives up, stands in the leave delay
        Assert.True(zombie.LeaveDelay > 0f);

        system.Tick(new[] { Player(2, new Vector3(8, 5, 0)) }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.Equal(2, zombie.TargetPlayer);
        Assert.Equal(0f, zombie.LeaveDelay); // isLeaving = false
    }

    [Fact]
    public void ReturningZombie_ArrivingStops()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.State = EZombieState.Return;
        zombie.LeaveTo = zombie.Position; // already there
        float yaw = zombie.Yaw;
        system.Tick(Array.Empty<ZombiePlayerView>(), 0.1f);
        Assert.Equal(EZombieState.Idle, zombie.State);
        Assert.Equal(zombie.LeaveTo, zombie.Position);
        Assert.Equal(yaw, zombie.Yaw); // a degenerate direction must not spin the zombie
    }

    [Fact]
    public void GroundSnap_OverridesTheHeightfieldWithTheRealSurface()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        Vector3 sampledAt = default;
        system.GroundSnap = (Vector3 position, out float y) =>
        {
            sampledAt = position; // receives the full position: stacked floors need the height
            y = 9.25f;            // a sidewalk top, above the flat heightfield at 5
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(9.25f, zombie.Position.Y);
        Assert.NotEqual(default, sampledAt);

        // When physics finds nothing (a hole), the sampler's own fallback decides; a false return
        // keeps the previous height rather than teleporting anywhere.
        system.GroundSnap = (Vector3 position, out float y) =>
        {
            y = 0f;
            return false;
        };
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(9.25f, zombie.Position.Y);
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

    // ---- Detection radii -------------------------------------------------------------------------

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
