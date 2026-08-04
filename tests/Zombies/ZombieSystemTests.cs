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

    // The one above proves a scan happens; it cannot see the RATE, because it only ever fires once.
    // The rate is what a player feels, and at the server's real tick it was wrong: 0.08 does not divide
    // 0.1, so the timer arrives at 0.16 and zeroing it discarded the 0.06 overshoot every single time.
    // A 0.1 s cadence became 0.16 s — 62 scans where the game runs 100, a third of a second of extra
    // grace on every approach.
    //
    // Counted through VisionBlocked, which Detect calls once per (player, zombie) pair: it observes the
    // scan without touching anything the brain reads, so the measurement cannot perturb what it measures.
    [Fact]
    public void DetectionHoldsItsCadenceAtTheServerTickRate()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        int scans = 0;
        system.VisionBlocked = (from, to) => { scans++; return false; };
        system.MoveResolver = (from, to, radius) => from; // keep Hunt outside its separate 2 m LOS check

        var players = new[] { Player(1, new Vector3(3, 5, 0)) };
        const float dt = UnturnedGodot.Net.ServerSimulation.TickRate; // 0.08, what ZombieHost passes
        const int seconds = 10;
        for (int i = 0; i < (int)(seconds / dt); i++)
        {
            zombie.State = EZombieState.Idle; // stay a candidate: Detect skips a zombie already on us
            zombie.TargetPlayer = byte.MaxValue;
            system.Tick(players, dt);
        }

        int expected = (int)(seconds / ZombieDetection.DetectInterval);
        Assert.InRange(scans, expected - 2, expected + 2); // 99..100 in practice; 62 before the fix
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

    // Stealing a target hands the old one's tally back. This is the ordinary case and it already worked.
    [Fact]
    public void StealingATarget_ReleasesTheOldPlayersAgro()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var far = Player(1, new Vector3(10, 5, 0));
        var near = Player(2, new Vector3(4, 5, 0));

        system.Tick(new[] { far }, 0.1f);
        Assert.Equal(1, system.AgroOn(1));

        system.Tick(new[] { far, near }, 0.1f);
        Assert.Equal(2, zombie.TargetPlayer);
        Assert.Equal(0, system.AgroOn(1));
        Assert.Equal(1, system.AgroOn(2));
    }

    // The case that leaked. Detect runs BEFORE the Behave loop, so in the tick a player disconnects a
    // zombie can be re-alerted onto someone else before Hunt would have noticed the target was gone and
    // called Leave — and Leave was the only path that gave the tally back. The departed player's count
    // stayed one too high for the rest of the session, and player ids are recycled, so the next player
    // to be handed that id inherited the skew.
    //
    // It is a small thing: agro only shapes RollPath's every-third-rushes spread. But it is a counter
    // that is supposed to mean "how many zombies are hunting you", and it was wrong.
    [Fact]
    public void AVanishedTargetReleasesItsAgro_WhenTheZombieIsReAlertedTheSameTick()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var leaving = Player(1, new Vector3(4, 5, 0));
        var arriving = Player(2, new Vector3(3, 5, 0));

        system.Tick(new[] { leaving }, 0.1f);
        Assert.Equal(1, zombie.TargetPlayer);
        Assert.Equal(1, system.AgroOn(1));

        // Player 1 is gone from the roster this tick, and player 2 is in range: Alert re-targets before
        // Hunt ever runs.
        system.Tick(new[] { arriving }, 0.1f);

        Assert.Equal(2, zombie.TargetPlayer);
        Assert.Equal(0, system.AgroOn(1)); // was 1: nothing ever gave it back
        Assert.Equal(1, system.AgroOn(2));
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
    public void Chase_ClosesOnTheTargetAtSpeed()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f; // already facing the target: full forward speed from the first step
        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f); // first zombie on the player: agro 0 -> RUSH path

        float before = zombie.Position.DistanceTo(player.Position);
        system.Tick(new[] { player }, 0.1f);
        float after = zombie.Position.DistanceTo(player.Position);

        // RUSH aims one metre short along the zombie's own forward, so the closing rate matches
        // the speciality speed (5.5 m/s x 0.1 s) once aligned.
        Assert.Equal(0.55f, before - after, 1);
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
        // Converged toward the target (RUSH aims a metre short along its own forward, so the
        // final heading sits within a few degrees of the straight line).
        Assert.True(MathF.Abs(Mathf.Wrap(zombie.Yaw - -90f, -180f, 180f)) < 8f, $"yaw={zombie.Yaw}");
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

    // GetHorizontalAttackRangeSquared returns the SQUARED threshold (normal 1 m, crawler and
    // sprinter ~1.41 m, mega 2 m of actual reach on a dedicated server).
    [Theory]
    [InlineData(EZombieSpeciality.Normal, 1f, 2.1f)]
    [InlineData(EZombieSpeciality.Crawler, 2f, 2.1f)]
    [InlineData(EZombieSpeciality.Sprinter, 2f, 2.1f)]
    [InlineData(EZombieSpeciality.Mega, 4f, 3.15f)]
    public void AttackRanges_MatchZombieCs(EZombieSpeciality speciality, float horizontalSquared, float vertical)
    {
        var zombie = new ZombieInstance { Speciality = speciality };
        Assert.Equal(horizontalSquared, zombie.AttackRangeSquared, 4);
        Assert.Equal(vertical, zombie.VerticalAttackRange, 4);
    }

    [Fact]
    public void HyperZombies_ReachHigher()
    {
        Assert.Equal(3.5f, new ZombieInstance { IsHyper = true }.VerticalAttackRange, 4);
        Assert.Equal(5.25f, new ZombieInstance
        {
            IsHyper = true,
            Speciality = EZombieSpeciality.Mega,
        }.VerticalAttackRange, 4);
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
    public void OpenGroundSideApproach_StaysABoundedOffsetInsteadOfOrbiting()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };
        var initialPlayer = Player(1, new Vector3(10, 5, 0),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        system.Tick(new[] { initialPlayer }, 0.1f);
        zombie.Path = EZombiePath.Left;

        var player = Player(1, new Vector3(20, 5, 0),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        Vector3 previous = zombie.Position;
        float walked = 0f, maxSide = 0f;
        for (int tick = 0; tick < 60; tick++)
        {
            system.Tick(new[] { player }, 0.1f);
            walked += previous.DistanceTo(zombie.Position);
            maxSide = MathF.Max(maxSide, MathF.Abs(zombie.Position.Z));
            previous = zombie.Position;
        }

        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.InRange(maxSide, 0.1f, 2f);
        Assert.InRange(walked, 18f, 23f); // lateral spread, never a circular lap
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
    public void PathfinderStillBuilding_UsesDirectFallbackThenSwitchesToRoutes()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f; // face the player so the direct fallback advances immediately
        bool ready = false;
        int queries = 0;
        system.PathReady = () => ready;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        Vector3 spawn = zombie.Position;
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State); // it did acquire aggro
        Assert.True(zombie.Position.X > spawn.X + 1f, $"froze while graph built: {zombie.Position}");
        Assert.Equal(0, queries); // an unavailable engine map must not be queried or log errors

        ready = true;
        for (int i = 0; i < 6; i++)
            system.Tick(new[] { player }, 0.1f);
        Assert.True(queries > 0); // automatically adopted the graph when publication completed
        Assert.True(zombie.Position.X > spawn.X + 2f);
    }

    [Fact]
    public void ReadyPathfinderWithNoRoute_StillStopsInsteadOfWalkingThroughWalls()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.PathReady = () => true;
        system.PathQuery = (from, to, path, radius) => false;

        Vector3 spawn = zombie.Position;
        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.Equal(spawn, zombie.Position);
    }

    [Fact]
    public void PathQuery_FollowsWaypointsAroundACorner()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // An L-shaped route: the navmesh sends the zombie 6 m sideways before turning to the
        // target. Like a real navmesh, the corner only appears while the zombie is still on the
        // first leg — a repath from beyond it routes straight.
        var corner = new Vector3(0, 5, 6);
        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
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
    public void CloseTargetBehindWall_StillTurnsIntoTheDetour()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = 180f; // face +Z: straight at the nearby player and the wall between them
        var player = Player(1, new Vector3(0f, 5f, 1.3f)); // inside 2 m, outside normal attack range

        // Acquire the player while visible. The wall then closes the vision ray, which is the exact
        // state of a zombie already hunting a player pressed against the opposite face of a wall.
        system.MoveResolver = (from, to, radius) =>
        {
            // Thin wall across z=0.5, ending at x=-2.5. A direct push at the player is clamped;
            // the nav route first turns away from the wall and then goes around its left end.
            if (from.Z < 0.5f && to.Z >= 0.5f && from.X > -2.5f)
                return new Vector3(to.X, to.Y, 0.49f);
            return to;
        };
        system.VisionBlocked = (_, _) => false;
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(EZombieState.Chase, zombie.State);

        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(new Vector3(-3f, 5f, -1f));
            path.Add(new Vector3(-3f, 5f, 2f));
            path.Add(to);
            return true;
        };
        system.VisionBlocked = (_, _) => true;

        float leftmost = zombie.Position.X;
        for (int i = 0; i < 30; i++)
        {
            system.Tick(new[] { player }, 0.1f);
            leftmost = MathF.Min(leftmost, zombie.Position.X);
        }

        Assert.True(leftmost < -2.5f,
            $"kept pushing straight into the wall instead of reaching its end: leftmost={leftmost}, final={zombie.Position}");
        Assert.True(zombie.Position.Z > 0.5f,
            $"reached the wall end but never crossed to the player's side: {zombie.Position}");
        Assert.NotEqual(180f, zombie.Yaw);
    }

    [Fact]
    public void RepathBudget_SaturatedHorde_IsServedFairly()
    {
        // 60 hunters against a budget of 8/tick: demand (60 x 2 repaths/s = 120/s) exceeds capacity
        // (100/s), so the queue stays saturated. The round-robin tokens must serve everyone evenly —
        // and only the tokens rotate: Behave order (movement, collision) stays fixed. A frozen
        // MoveResolver pins every zombie to its spawn cell so the query's `from` identifies it.
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        var points = new List<ZombieSpawnpointData>();
        for (int i = 0; i < 240; i++)
            points.Add(At((i % 16) * 4 - 32, (i / 16) * 4 - 30));
        system.Spawn(points, new Random(5));
        Assert.Equal(60, system.Zombies.Count);
        system.MoveResolver = (from, to, radius) => from; // freeze in place

        var perZombie = new Dictionary<(float, float), int>();
        system.PathQuery = (from, to, path, radius) =>
        {
            (float, float) key = (from.X, from.Z);
            perZombie.TryGetValue(key, out int n);
            perZombie[key] = n + 1;
            path.Add(from);
            path.Add(to);
            return true;
        };

        var player = new[] { Player(1, new Vector3(10, 5, 0)) }; // within 64 m of every grid cell
        foreach (ZombieInstance z in system.Zombies)
        {
            z.State = EZombieState.Chase; // the whole pack hunts at once
            z.TargetPlayer = 1;
            z.RepathTimer = 0f;
        }

        // First wave: exactly the budget per tick, and after ceil(60/8) ticks everyone has a route.
        system.Tick(player, 0.08f);
        Assert.Equal(ZombieSystem.MaxRepathsPerTick, perZombie.Values.Sum());
        for (int t = 0; t < 7; t++)
            system.Tick(player, 0.08f);
        Assert.Equal(60, perZombie.Count); // every hunter got its first route within the first wave

        // Sustained saturation over many repath cycles: counts stay even (fairness), the budget is
        // never exceeded, and nobody is starved.
        for (int t = 0; t < 52; t++)
            system.Tick(player, 0.08f);
        Assert.Equal(60 * ZombieSystem.MaxRepathsPerTick, perZombie.Values.Sum()); // 8 per tick, every tick
        Assert.True(perZombie.Values.Max() - perZombie.Values.Min() <= 1,
            $"unfair budget: min {perZombie.Values.Min()}, max {perZombie.Values.Max()}");
    }


    [Fact]
    public void PathQueries_CarryTheOfficialApproachOffsets()
    {
        // Zombie.tick offsets the pathfinding DESTINATION: LEFT/RIGHT one metre to the zombie's
        // own side, RUSH one metre short along its own forward — every query lands within a metre
        // of the player, never exactly on them (until inside 2 m, where steering goes direct).
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path, radius) =>
        {
            destinations.Add(to);
            path.Add(from);
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        zombie.Path = EZombiePath.Left;
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.True(destinations.Count >= 2);
        Assert.All(destinations, d =>
        {
            float offset = new Vector2(d.X - player.Position.X, d.Z - player.Position.Z).Length();
            Assert.InRange(offset, 0.9f, 1.1f); // exactly the one-metre approach shaping
        });
    }

    [Fact]
    public void CarrotFollowing_HoldsTheCorridorThroughTheCorner()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        // An L-route with the corner at (6, 0, 6): proper polyline following passes NEAR the
        // corner instead of cutting the diagonal through the "wall".
        var corner = new Vector3(6, 5, 6);
        system.PathQuery = (from, to, path, radius) =>
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
        // forwardLook (4 m) rounds the corner like the original: the cut is bounded by the
        // lookahead (~2.8 m worst case), far tighter than the 4.2 m straight diagonal.
        Assert.True(closestToCorner < 2.5f, $"cut the corner: nearest pass {closestToCorner:F2}m");
        Assert.Equal(EZombieState.Attack, zombie.State); // and still reached the target
    }

    [Fact]
    public void FailedRepath_KeepsTheCurrentRoute()
    {
        // OnPathComplete ignores errored paths: a failing repath must NOT strand the zombie —
        // it keeps following the route it already has.
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int calls = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            calls++;
            if (calls > 1)
                return false; // every repath after the first fails
            path.Add(from);
            path.Add(new Vector3(5, 5, 0));
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 15; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.True(calls >= 2); // repaths kept firing and failing
        Assert.True(zombie.Position.X > 3f, $"stranded by the failed repath: {zombie.Position}");
    }

    [Fact]
    public void PartialRouteThenCollisionAwareFallback_ReachesAcrossAGraphSeam()
    {
        // Godot returns the route to the closest reachable point when the target occupies another
        // navmesh island. Throwing that useful prefix away made Chase animate at the random spawn yaw
        // without any movement. It should advance as far as the graph safely allows.
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(new Vector3(4, 5, 0)); // connected island ends 6 m short of the target
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        Vector3 spawn = zombie.Position;
        for (int i = 0; i < 35; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.True(zombie.Position.X > spawn.X + 8f, $"stranded at graph seam: {zombie.Position}");
        Assert.True(zombie.PathIsPartial);
        Assert.InRange(MathF.Abs(Mathf.Wrap(zombie.Yaw - -90f, -180f, 180f)), 0f, 5f);
    }

    [Fact]
    public void FirstPartialRouteMayDetourAwayFromANearbyTargetToUseADoor()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int queries = 0;
        system.PathQuery = (from, _, path, _) =>
        {
            queries++;
            path.Add(from);
            if (queries == 1)
            {
                path.Add(new Vector3(0f, 5f, 6f));
                // The graph ends just inside the door. This is farther from the target than the spawn
                // is, but it is the only executable prefix; collision-aware fallback can finish here.
                path.Add(new Vector3(2f, 5f, 6f));
            }
            else
            {
                // From the doorway the opposite navmesh island looks geometrically closer, but adopting
                // this partial prefix would reverse back outside and park at the window again.
                path.Add(new Vector3(0f, 5f, 0f));
            }
            return true;
        };
        system.MoveResolver = (from, to, _) =>
        {
            // Wall at x=1.5, with its door open at z>=5.5.
            float dx = to.X - from.X;
            if (MathF.Abs(dx) > 1e-6f)
            {
                float crossing = (1.5f - from.X) / dx;
                if (crossing >= 0f && crossing <= 1f)
                {
                    float z = from.Z + ((to.Z - from.Z) * crossing);
                    if (z < 5.5f)
                        return new Vector3(MathF.Min(to.X, 1.49f), to.Y, to.Z); // slide along it
                }
            }
            return to;
        };

        var player = Player(1, new Vector3(3f, 5f, 0f));
        float farthestZ = 0f;
        for (int tick = 0; tick < 240; tick++)
        {
            system.Tick(new[] { player }, 0.1f);
            farthestZ = MathF.Max(farthestZ, zombie.Position.Z);
        }

        Assert.True(farthestZ >= 5.5f, $"never used the door: {zombie.Position}");
        Assert.True(queries > 1, "the route was never challenged by a later partial repath");
        Assert.True(zombie.Position.X > 2f, $"never entered through the door: {zombie.Position}");
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    [Fact]
    public void PartialRouteFallback_StillCannotCrossARealWall()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            path.Add(new Vector3(4, 5, 0));
            return true;
        };
        // The nav island ends at the same place as a real collider. The post-route direct leg may push
        // against it, but every authoritative step is still resolved by physics and must remain outside.
        system.MoveResolver = (from, to, radius) => new Vector3(MathF.Min(to.X, 4f), to.Y, to.Z);

        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 80; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.InRange(zombie.Position.X, 3.8f, 4.001f);
        Assert.True(zombie.Position.DistanceTo(player.Position) > 5f);
        Assert.InRange(queries, 2, 30); // retries, but never one query per simulation tick
    }

    [Fact]
    public void PartialRepathDoesNotReplaceABetterExistingRoute()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int query = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(new Vector3(query++ == 0 ? 7f : 3f, 5, 0));
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 12; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.True(query >= 2);
        Assert.Equal(7f, zombie.PathPoints[^1].X); // the later, worse partial endpoint was ignored
        Assert.True(zombie.Position.X > 4f);
    }

    [Fact]
    public void EndReached_StopsTheBodyShortOfTheTarget()
    {
        // endReachedDistance (0.75): the follower parks the zombie at the route's end instead of
        // grinding into the target — and inside 2 m the final segment goes directly at the raw
        // player, so the stop lands inside the 1 m attack reach.
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(6, 5, 0));
        for (int i = 0; i < 30; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Attack, zombie.State);
        float dist = new Vector2(zombie.Position.X - 6f, zombie.Position.Z).Length();
        // Parked by endReached inside attack reach. (At the 12.5 Hz tick the last step lands a
        // little deeper than the original's per-frame movement would; still short of contact.)
        Assert.InRange(dist, 0.35f, 1.05f);
        Assert.True(zombie.TargetReached);
    }

    [Fact]
    public void DegenerateRoutesAndZeroDelta_AreHarmless()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        Vector3 here = zombie.Position;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(here); // a route collapsed onto the zombie itself (degenerate segment)
            path.Add(here);
            return true;
        };
        var player = Player(1, new Vector3(3, 5, 0)); // inside 2 m? no: 3 m — offset branch on
        system.Tick(new[] { player }, 0.1f);
        system.Tick(new[] { player }, 0f); // a zero-delta tick must not divide by zero
        Assert.Equal(here, zombie.Position); // never moved, never NaN'd
        Assert.False(float.IsNaN(zombie.Yaw));
    }

    [Fact]
    public void RouteShrinkingBetweenRepaths_ClampsTheWaypointIndex()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int calls = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            calls++;
            path.Add(from);
            if (calls == 1)
            {
                // long first route with several close waypoints to advance past
                path.Add(from + new Vector3(0.3f, 0, 0));
                path.Add(from + new Vector3(0.6f, 0, 0));
                path.Add(from + new Vector3(3f, 0, 0));
            }
            path.Add(to);
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        for (int i = 0; i < 12; i++)
            system.Tick(new[] { player }, 0.1f); // second repath shrinks the route under the index
        Assert.True(zombie.Position.X > 2f); // still following after the clamp
    }

    // Unturned has no sidestep logic anywhere: a blocked zombie keeps pushing, and its
    // CharacterController.Move slides it free because THAT resolve is iterative. So the escape
    // belongs to the move resolver, and this system must not invent one of its own.
    [Fact]
    public void DeadOnBlocks_AreSkirtedByTheResolversSlide_NotByTheBrain()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };

        // A trunk of radius 1 at (5,0) dead on the route, with a resolver that slides along it the
        // way a CharacterController does — tangentially, never straight through.
        Vector3 trunk = new(5, 5, 0);
        system.MoveResolver = (from, to, radius) =>
        {
            float dx = to.X - trunk.X, dz = to.Z - trunk.Z;
            float dist = MathF.Sqrt((dx * dx) + (dz * dz));
            float minimum = 1f + radius;
            if (dist >= minimum)
                return to;
            if (dist < 1e-4f)
                return from;
            // Push the destination back out to the surface: the slide component of the motion.
            return new Vector3(trunk.X + (dx / dist * minimum), to.Y, trunk.Z + (dz / dist * minimum));
        };

        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int i = 0; i < 120; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.True(zombie.Position.X > 6.5f, $"deadlocked at {zombie.Position}");
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    [Fact]
    public void FullyWalledIn_HoldsStillWithoutTeleporting()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) => from; // sealed in: nothing ever moves
        Vector3 spawn = zombie.Position;

        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int i = 0; i < 10; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(spawn, zombie.Position);
    }

    [Fact]
    public void PersistentlyBlockedCompleteRoute_IsDiscardedSoDoorDetourCanReplaceIt()
    {
        (List<Vector3> trace, int queries, EZombieState state) = RunWindowRecovery();

        Assert.True(queries >= 3);
        Assert.Contains(trace, p => p.Z >= 2f);
        Assert.True(trace[^1].X > 8f, $"never escaped the window route: {trace[^1]}");
        Assert.True(state == EZombieState.Attack,
            $"window recovery ended in {state}; last={trace[^1]}, queries={queries}");
    }

    [Fact]
    public void WindowRecovery_IsDeterministicAcrossIndependentSimulations()
    {
        var first = RunWindowRecovery();
        var second = RunWindowRecovery();

        Assert.Equal(first.Queries, second.Queries);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.Trace, second.Trace);
    }

    // PersistentlyBlockedCompleteRoute covers the case where the REPLACEMENT is rejected — a partial
    // route with a worse endpoint loses to the stale one, so nothing touches the blocked-route
    // evidence and the timeout fires. The case that stayed broken is the opposite one: a graph that
    // has not been reconciled against the collision world answers every query with the same
    // geometrically perfect line, so the replacement is ACCEPTED. Resetting the evidence on accept
    // restarted the counter every RepathRate, which is shorter than BlockedRouteTimeout, so the
    // counter could never reach the timeout and the zombie re-adopted the wall route forever.
    [Fact]
    public void AnImpassableRoute_IsInvalidatedEvenWhenEveryRepathReadoptsItVerbatim()
    {
        ImpassableRun run = RunAgainstAnImpassableCompleteRoute();

        Assert.True(run.InvalidatedAtTick > 0, "the impassable route was never invalidated");
        // Derived from the constants, not from observation: the counter must run uninterrupted from
        // the first blocked tick to the timeout. FirstBlockedTick already carries one tick of
        // evidence, so the remaining distance is one tick shorter than the whole timeout.
        int ticksToTimeout = (int)MathF.Ceiling(ZombieSystem.BlockedRouteTimeout / ImpassableDt);
        Assert.Equal(ticksToTimeout - 1, run.InvalidatedAtTick - run.FirstBlockedTick);
        // ...and at least one repath happened inside that window, re-adopting the same route. Without
        // it the run would prove nothing: a window with no repath in it never exercised the reset.
        Assert.True(ZombieSystem.RepathRate < ZombieSystem.BlockedRouteTimeout);
        Assert.True(run.QueriesAtInvalidation > run.QueriesAtFirstBlock,
            $"no repath inside the blocked window: {run.QueriesAtFirstBlock} -> {run.QueriesAtInvalidation}");
        // Invalidation asks for a replacement immediately, and drops the state that described the
        // route it just threw away.
        Assert.Equal(0f, run.RepathTimerAfter);
        Assert.Equal(0, run.WaypointIndexAfter);
        Assert.False(run.PartialAfter);
    }

    // The same rule against the target the game actually has: one that MOVES. The route to a runner
    // is re-derived every repath with a final waypoint that has travelled metres, while the body stays
    // pinned against the same obstruction. Any route identity that reads the destination as part of the
    // plan therefore calls every reissue a NEW route and hands it a clean record — and because
    // RepathRate (0.5 s) is shorter than BlockedRouteTimeout (0.75 s), that clears the evidence
    // strictly faster than it can accumulate. The wall route then survives forever: the exact
    // disarmament AnImpassableRoute_IsInvalidatedEvenWhenEveryRepathReadoptsItVerbatim exists to
    // prevent, reintroduced for the only case that matters. A stationary target cannot catch this.
    [Fact]
    public void AnImpassableRoute_IsInvalidatedWhileTheTargetKeepsMoving()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to); // ends exactly on the target: always "complete", so always accepted
            return true;
        };
        system.MoveResolver = (from, to, radius) => from; // and always refused by the physics world

        // 0.6 m/tick is a sprint: ~3 m of destination travel between repaths, far beyond any tolerance
        // a waypoint comparison could sanely use, and the zombie never leaves its spot.
        const float MetresPerTick = 0.6f;
        int budget = (int)MathF.Ceiling(ZombieSystem.BlockedRouteTimeout / ImpassableDt) * 6;
        float worstEvidence = 0f;
        for (int tick = 1; tick <= budget; tick++)
        {
            var player = Player(1, new Vector3(10f, 5f, tick * MetresPerTick),
                UnturnedGodot.Player.EPlayerStance.Sprint, moving: true);
            system.Tick(new[] { player }, ImpassableDt);
            worstEvidence = MathF.Max(worstEvidence, zombie.BlockedRouteTime);
            if (zombie.State == EZombieState.Chase && zombie.PathPoints.Count == 0 && worstEvidence > 0f)
                return; // invalidated: the physics world got its say, as it does for a still target
        }

        Assert.Fail("the impassable route was never invalidated while the target moved; evidence "
            + $"peaked at {worstEvidence:0.###} s against a {ZombieSystem.BlockedRouteTimeout} s timeout");
    }

    [Fact]
    public void ImpassableRouteInvalidation_IsDeterministicAcrossIndependentSimulations()
    {
        ImpassableRun first = RunAgainstAnImpassableCompleteRoute();
        ImpassableRun second = RunAgainstAnImpassableCompleteRoute();

        Assert.Equal(first, second);
    }

    [Fact]
    public void CollisionRecovery_TargetsThePlayerInsteadOfAFormationOffsetInsideTheObstacle()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        var destinations = new List<(Vector3 Point, bool Recovering)>();
        system.PathQuery = (from, to, path, radius) =>
        {
            destinations.Add((to, zombie.RouteEscapingCollision));
            path.Add(from);
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) => from;

        Vector3 playerPosition = new(10f, 5f, 0f);
        var player = Player(1, playerPosition, UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 40; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        Assert.Contains(destinations, sample => !sample.Recovering
            && Horizontal(sample.Point, playerPosition) > 0.5f);
        Assert.Contains(destinations, sample => sample.Recovering
            && Horizontal(sample.Point, playerPosition) < 0.001f);

        static float Horizontal(Vector3 a, Vector3 b) =>
            new Vector2(a.X - b.X, a.Z - b.Z).Length();
    }

    [Fact]
    public void CollisionRecovery_EndsAtRangeWhenLineOfSightReturns()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);

        system.VisionBlocked = (_, _) => false;
        system.Tick(new[] { player }, 0.1f); // acquire the target first

        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path, radius) =>
        {
            destinations.Add(to);
            path.Add(from);
            path.Add(to);
            return true;
        };
        zombie.Path = EZombiePath.Left;
        zombie.RouteEscapingCollision = true;
        zombie.RepathTimer = -1f;

        system.Tick(new[] { player }, 0.1f);

        Assert.False(zombie.RouteEscapingCollision);
        Vector3 destination = Assert.Single(destinations);
        Assert.True(new Vector2(destination.X - player.Position.X,
            destination.Z - player.Position.Z).Length() > 0.5f,
            $"formation offset remained suppressed after sight returned: {destination}");
    }

    [Fact]
    public void CollisionRecovery_DoesNotEndOnClearVisionThroughAWorldObstacle()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        system.VisionBlocked = (_, _) => false; // a Resource/terrain collider has no VisionBlocker bit
        system.PhysicalLineBlocked = (_, _) => true;
        system.Tick(new[] { player }, 0.1f); // acquire first

        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path, radius) =>
        {
            destinations.Add(to);
            path.Add(from);
            path.Add(to);
            return true;
        };
        zombie.Path = EZombiePath.Left;
        zombie.RouteEscapingCollision = true;
        zombie.RepathTimer = -1f;

        system.Tick(new[] { player }, 0.1f);

        Assert.True(zombie.RouteEscapingCollision);
        Vector3 destination = Assert.Single(destinations);
        Assert.True(new Vector2(destination.X - player.Position.X,
            destination.Z - player.Position.Z).Length() < 0.001f,
            $"physical recovery was replaced by a formation offset: {destination}");
    }

    [Fact]
    public void CollisionRecovery_DoesNotEndWhileTheCapsuleIsStillBehindALowObstacle()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        system.VisionBlocked = (_, _) => false;
        system.PhysicalLineBlocked = (_, _) => false; // the obstacle is below both eye points
        system.Tick(new[] { player }, 0.1f); // acquire first

        zombie.Position = new Vector3(0f, 5f, 0f);
        zombie.PathPoints.Clear();
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(new Vector3(0f, 5f, 3f));
        zombie.PathPoints.Add(new Vector3(10f, 5f, 3f));
        zombie.PathPoints.Add(player.Position);
        zombie.CurrentWaypointIndex = 1;
        zombie.TargetReached = false;
        zombie.PathIsPartial = false;
        zombie.RouteEscapingCollision = true;
        zombie.EscapeRouteHasProgress = true;
        zombie.RepathTimer = 10f;
        system.PathQuery = (_, _, _, _) => true; // enable the installed route follower

        bool lowObstaclePresent = true;
        system.MoveResolver = (from, to, _) => lowObstaclePresent
            && MathF.Abs(to.Z) < 0.01f
            && to.X > 5f
                ? from
                : to;

        for (int tick = 0; tick < 3; tick++)
        {
            system.Tick(new[] { player }, 0.1f);
            Assert.True(zombie.RouteEscapingCollision,
                "an eye ray cannot prove that the movement capsule has cleared a low obstacle");
        }
        Assert.True(zombie.Position.Z > 0f,
            $"the valid detour should continue making progress: {zombie.Position}");

        lowObstaclePresent = false;
        system.Tick(new[] { player }, 0.1f);

        Assert.False(zombie.RouteEscapingCollision,
            "recovery should end once the same capsule resolver can reach the target");
    }

    [Fact]
    public void CollisionEscape_IsNotReplacedByTheShortWallRouteOnTheNextRepath()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        bool escapeIssued = false;
        int shortcutsAfterEscape = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            if (zombie.RouteEscapingCollision && !escapeIssued)
            {
                escapeIssued = true;
                path.Add(new Vector3(3f, 5f, 3f));
                path.Add(new Vector3(5f, 5f, 3f));
            }
            else if (escapeIssued)
            {
                shortcutsAfterEscape++;
            }
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) =>
        {
            // An infinite-in-Y wall at x=4, open outside |z|<2. The baked shortcut crosses its
            // middle; the recovery's two corners pass around the end.
            float dx = to.X - from.X;
            if (MathF.Abs(dx) <= 1e-6f)
                return to;
            float crossing = (4f - from.X) / dx;
            if (crossing >= 0f && crossing <= 1f)
            {
                float z = from.Z + ((to.Z - from.Z) * crossing);
                if (MathF.Abs(z) < 2f)
                    return from;
            }
            return to;
        };

        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 200; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        Assert.True(escapeIssued, "the blocked shortcut never armed collision recovery");
        Assert.True(shortcutsAfterEscape > 0, "no later repath offered the lying shorter route");
        Assert.True(zombie.Position.X > 8f, $"the shortcut pulled it back into the wall: {zombie.Position}");
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    [Fact]
    public void CollisionRecoveryPhysicsBudget_IsSharedByTheWholeHordeTick()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 40).Select(i => At(0f, -40f + (i * 2f))).ToList(),
            new Random(7));
        Assert.True(system.Zombies.Count > 1);
        var player = Player(1, new Vector3(30f, 5f, 0f));
        system.PathQuery = (_, _, _, _) => false; // routes below remain installed during this tick
        int moves = 0, grounds = 0;
        system.MoveResolver = (from, to, _) =>
        {
            moves++;
            const float wallX = 15f;
            return (from.X - wallX) * (to.X - wallX) <= 0f ? from : to;
        };
        system.GroundSnap = (Vector3 point, out float y) =>
        {
            grounds++;
            y = 5f;
            return true;
        };
        foreach (ZombieInstance zombie in system.Zombies)
        {
            zombie.Position = new Vector3(14.7f, 5f, zombie.Position.Z);
            zombie.State = EZombieState.Chase;
            zombie.TargetPlayer = player.Id;
            zombie.Yaw = -90f;
            zombie.RepathTimer = 10f;
            zombie.BlockedRouteTime = ZombieSystem.BlockedRouteTimeout;
            zombie.PathPoints.Clear();
            zombie.PathPoints.Add(zombie.Position);
            zombie.PathPoints.Add(player.Position);
            zombie.CurrentWaypointIndex = 1;
        }

        system.Tick(new[] { player }, ImpassableDt);

        int ordinaryQueries = system.Zombies.Count * 2; // one movement and one ground snap per body
        Assert.InRange(moves + grounds, 1,
            ZombieSystem.MaxCollisionDetourPhysicsQueriesPerTick + ordinaryQueries);
        Assert.True(moves + grounds > ZombieCollisionDetour.MaxPolarEdgeProbes,
            $"the fixture spent only {moves} move + {grounds} ground queries");

        // The winner clears its failed route; every saturated waiter then becomes the oldest on a
        // later tick instead of fixed list order giving the same zombie the whole budget forever.
        for (int tick = 1; tick < system.Zombies.Count * 3; tick++)
            system.Tick(new[] { player }, ImpassableDt);
        Assert.All(system.Zombies, zombie =>
            Assert.Equal(EZombieCollisionDetourResult.SearchFailed,
                zombie.LastCollisionDetourResult));
    }

    [Fact]
    public void CollisionRecoveryAllowsPhysicallyCheckedBoundaryLegsOffTheAuthoredMesh()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10f, 5f, 0f));
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = player.Id;
        zombie.Path = EZombiePath.Rush;
        zombie.Yaw = -90f;
        zombie.RepathTimer = 10f;
        zombie.BlockedRouteTime = ZombieSystem.BlockedRouteTimeout;
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(player.Position);
        zombie.CurrentWaypointIndex = 1;
        system.PathQuery = (_, _, _, _) => false;
        system.NavmeshProject = (Vector3 _, out Vector3 projected) =>
        {
            projected = default;
            return false; // both the object floor and target are outside enabled baked faces
        };
        system.NavmeshSupportsSegment = (_, _, _) => false;
        system.MoveResolver = (from, to, _) =>
            new Vector2(to.X - from.X, to.Z - from.Z).Length() < 1f ? from : to;
        system.GroundSnap = (Vector3 _, out float y) =>
        {
            y = 5f;
            return true;
        };

        system.Tick(new[] { player }, ImpassableDt);

        Assert.True(zombie.CollisionEscapeRoute);
        Assert.Equal(EZombieCollisionDetourResult.Installed, zombie.LastCollisionDetourResult);
        Assert.Equal(2, zombie.PathPoints.Count); // physically swept start -> target boundary leg
    }

    [Fact]
    public void CollisionRecoveryRejectsAnOpenSweepWhoseGroundHasAHole()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10f, 5f, 0f));
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = player.Id;
        zombie.Path = EZombiePath.Rush;
        zombie.Yaw = -90f;
        zombie.RepathTimer = 10f;
        zombie.BlockedRouteTime = ZombieSystem.BlockedRouteTimeout;
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(player.Position);
        zombie.CurrentWaypointIndex = 1;
        system.PathQuery = (_, _, _, _) => false;
        system.NavmeshProject = (Vector3 point, out Vector3 projected) =>
        {
            projected = point;
            return true;
        };
        system.NavmeshSupportsSegment = (_, _, _) => true;
        // Ordinary sub-metre movement is pinned to arm recovery; its long validation sweep is clear.
        system.MoveResolver = (from, to, _) =>
            new Vector2(to.X - from.X, to.Z - from.Z).Length() < 1f ? from : to;
        system.GroundSnap = (Vector3 point, out float y) =>
        {
            y = 5f;
            return point.X is < 4f or > 6f;
        };

        system.Tick(new[] { player }, ImpassableDt);

        Assert.False(zombie.CollisionEscapeRoute);
        Assert.True(zombie.PathPoints.Count == 0,
            $"route remained with blocked={zombie.BlockedRouteTime:F2}, state={zombie.State}, "
            + $"position={zombie.Position}: {string.Join(" ", zombie.PathPoints)}");
        Assert.True(zombie.RouteEscapingCollision);
    }

    [Fact]
    public void CollisionRecoveryRejectsCandidatesOutsideEnabledNavmeshFaces()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        ZombiePlayerView player = ArmCollisionRecovery(zombie);
        system.PathQuery = (_, _, _, _) => false;
        system.MoveResolver = (from, _, _) => from;
        system.NavmeshProject = (Vector3 _, out Vector3 projected) =>
        {
            projected = default;
            return false;
        };

        system.Tick(new[] { player }, ImpassableDt);

        Assert.Equal(EZombieCollisionDetourResult.SearchFailed, zombie.LastCollisionDetourResult);
        Assert.False(zombie.CollisionEscapeRoute);
    }

    [Fact]
    public void CollisionRecoveryRejectsCandidatesWithoutPhysicalGround()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        ZombiePlayerView player = ArmCollisionRecovery(zombie);
        system.PathQuery = (_, _, _, _) => false;
        system.MoveResolver = (from, _, _) => from;
        system.NavmeshProject = (Vector3 point, out Vector3 projected) =>
        {
            projected = point;
            return true;
        };
        system.GroundSnap = (Vector3 _, out float y) =>
        {
            y = 0f;
            return false;
        };

        system.Tick(new[] { player }, ImpassableDt);

        Assert.Equal(EZombieCollisionDetourResult.SearchFailed, zombie.LastCollisionDetourResult);
        Assert.False(zombie.CollisionEscapeRoute);
    }

    [Fact]
    public void CollisionRecoveryRejectsADirectRouteWhenTerrainSupportIsUnknown()
    {
        bool groundKnown = true;
        bool Ground(float _x, float _z, out float y)
        {
            y = 5f;
            return groundKnown;
        }
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), Ground);
        system.Spawn(new[] { At(0f, 0f) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        ZombiePlayerView player = ArmCollisionRecovery(zombie);
        system.PathQuery = (_, _, _, _) => false;
        system.MoveResolver = (from, to, _) =>
            new Vector2(to.X - from.X, to.Z - from.Z).Length() < 1f ? from : to;
        groundKnown = false;

        system.Tick(new[] { player }, ImpassableDt);

        Assert.Equal(EZombieCollisionDetourResult.GroundRejected, zombie.LastCollisionDetourResult);
        Assert.False(zombie.CollisionEscapeRoute);
    }

    private static ZombiePlayerView ArmCollisionRecovery(ZombieInstance zombie)
    {
        var player = Player(1, new Vector3(10f, 5f, 0f));
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = player.Id;
        zombie.Path = EZombiePath.Rush;
        zombie.Yaw = -90f;
        zombie.RepathTimer = 10f;
        zombie.BlockedRouteTime = ZombieSystem.BlockedRouteTimeout;
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(player.Position);
        zombie.CurrentWaypointIndex = 1;
        return player;
    }

    [Fact]
    public void TheFirstUnprovenRecoveryRoute_CannotVetoTheLaterWorkingDetour()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int recoveryQueries = 0;
        bool detourIssued = false;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            if (zombie.RouteEscapingCollision && recoveryQueries++ == 1)
            {
                detourIssued = true;
                path.Add(new Vector3(3f, 5f, 3f));
                path.Add(new Vector3(5f, 5f, 3f));
            }
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) =>
        {
            float dx = to.X - from.X;
            if (MathF.Abs(dx) <= 1e-6f)
                return to;
            float crossing = (4f - from.X) / dx;
            if (crossing >= 0f && crossing <= 1f)
            {
                float z = from.Z + ((to.Z - from.Z) * crossing);
                if (MathF.Abs(z) < 2f)
                    return from;
            }
            return to;
        };

        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 200; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        Assert.True(recoveryQueries >= 2, "the post-timeout shortcut was not followed by another query");
        Assert.True(detourIssued, "the working detour was never offered");
        Assert.True(zombie.Position.X > 8f,
            $"the unproven shortcut vetoed the detour: {zombie.Position}");
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARepeatedBlockedGraphRoute_UsesTheBoundedPhysicalDetour(bool partial)
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, _) =>
        {
            // The baked graph sees either a same-face direct answer or only the near-side prefix and
            // returns it forever. The finite wall lives inside that face, so no indexed portal can
            // teach the graph about it.
            path.Add(from);
            path.Add(partial ? new Vector3(2f, 5f, 0f) : to);
            return true;
        };
        system.MoveResolver = (from, to, radius) => SegmentClear(from, to, radius) ? to : from;
        system.PhysicalLineBlocked = (from, to) => !SegmentClear(from, to, zombie.Radius);
        system.GroundSnap = (Vector3 point, out float y) => { y = 5f; return true; };

        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 240; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        Assert.True(zombie.State == EZombieState.Attack,
            $"detour ended at {zombie.Position} in {zombie.State}, route "
            + $"{zombie.CurrentWaypointIndex}/{zombie.PathPoints.Count}, "
            + $"escape={zombie.RouteEscapingCollision}/{zombie.CollisionEscapeRoute}, "
            + $"blocked={zombie.BlockedRouteTime:0.##}: {string.Join(" ", zombie.PathPoints)}");
        Assert.True(zombie.Position.X > 8f, $"never got round the in-face wall: {zombie.Position}");
        Assert.False(zombie.CollisionEscapeRoute,
            "the local route should relinquish control after direct capsule reach returns");

        static bool SegmentClear(Vector3 from, Vector3 to, float radius)
        {
            const float wallX = 4f;
            float dx = to.X - from.X;
            if (MathF.Abs(dx) <= 1e-6f || (from.X - wallX) * (to.X - wallX) > 0f)
                return true;
            float along = (wallX - from.X) / dx;
            if (along is < 0f or > 1f)
                return true;
            float z = from.Z + ((to.Z - from.Z) * along);
            return MathF.Abs(z) > 4f + radius;
        }
    }

    [Fact]
    public void AFinishedCompleteRouteToAnOldTargetPosition_DoesNotBecomeAPermanentStop()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;
        zombie.PathPoints.Add(zombie.Position);
        zombie.CurrentWaypointIndex = 0;
        zombie.TargetReached = true;
        zombie.RepathTimer = -1f;
        zombie.RepathGranted = true;
        system.PathQuery = (_, _, _, _) => false;
        system.MoveResolver = (_, to, _) => to;

        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 40; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.True(zombie.Position.X > 9f,
            $"the exhausted old endpoint still held the body: {zombie.Position}");
    }

    [Fact]
    public void AFinishedFormationEndpointThatStillCannotAttack_KeepsClosingTheGap()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;
        zombie.PathPoints.Add(zombie.Position);
        zombie.TargetReached = true;
        zombie.RepathTimer = -1f;
        zombie.RepathGranted = true;
        system.PathQuery = (_, _, _, _) => false;
        system.MoveResolver = (_, to, _) => to;

        // 2.5 m is inside the three-metre endpoint tolerance (2 m path completion + 1 m
        // formation offset), but outside a normal zombie's one-metre attack radius.
        var player = Player(1, new Vector3(2.5f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 25; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.True(zombie.Position.X > 1.5f,
            $"the formation endpoint was mistaken for attack arrival: {zombie.Position}");
    }

    // The other side of the same rule: evidence is about DELIVERED motion, so a route that keeps
    // moving the body must never accumulate any, however many times it is repathed.
    [Fact]
    public void ARouteThatKeepsDeliveringMotion_NeverAccumulatesBlockedEvidence()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) => to; // open ground

        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        float worstEvidence = 0f;
        int chaseTicks = 0, routelessTicks = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            system.Tick(new[] { player }, ImpassableDt);
            if (zombie.State != EZombieState.Chase)
                continue;
            chaseTicks++;
            worstEvidence = MathF.Max(worstEvidence, zombie.BlockedRouteTime);
            if (zombie.PathPoints.Count == 0)
                routelessTicks++;
        }

        Assert.True(chaseTicks > (int)MathF.Ceiling(ZombieSystem.RepathRate / ImpassableDt),
            $"the chase was too short to span a repath: {chaseTicks} ticks");
        Assert.Equal(0f, worstEvidence);
        Assert.Equal(0, routelessTicks);
        Assert.True(queries >= 2, "the run never repathed, so it never exercised the accept path");
        Assert.True(zombie.Position.X > 3f, $"never made progress: {zombie.Position}");
    }

    // The boundary from the other direction: stopping one tick short of the timeout must leave the
    // route intact, and the first tick that delivers motion must wipe the evidence. This is what
    // stops the stricter rule from turning ordinary crowd jostling into a route discard.
    [Fact]
    public void EvidenceOneTickShortOfTheTimeout_IsWipedByTheFirstTickTheBodyMoves()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };
        bool blocked = true;
        system.MoveResolver = (from, to, radius) => blocked ? from : to;

        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        // Stop on the last tick from which one more blocked tick would fire the timeout. Driven off
        // the evidence itself, because the pre-aggro ticks also go through the resolver.
        int guard = 0;
        while (zombie.BlockedRouteTime + ImpassableDt + 1e-4f < ZombieSystem.BlockedRouteTimeout)
        {
            system.Tick(new[] { player }, ImpassableDt);
            Assert.True(++guard < 100, "the zombie never started accumulating blocked evidence");
        }

        Assert.NotEmpty(zombie.PathPoints); // one tick short: still trusted
        Assert.True(zombie.BlockedRouteTime > 0f, "the near-miss never accumulated anything");
        blocked = false;

        system.Tick(new[] { player }, ImpassableDt);

        Assert.Equal(0f, zombie.BlockedRouteTime);
        Assert.NotEmpty(zombie.PathPoints);
    }

    // Codex review, P2. Keeping the route across a retarget must not keep the JUDGEMENT of it. The
    // blocked-route evidence says "this body is not delivering motion toward where it was going", and
    // a retarget changes where it is going. Carried over near the timeout, one blocked tick on the
    // replacement discards it before the zombie has even turned — the mid-chase freeze that keeping
    // the route exists to prevent, reintroduced through the back door.
    [Fact]
    public void Retargeting_DropsTheBlockedEvidenceEarnedAgainstTheOldTarget()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };
        bool blocked = true;
        system.MoveResolver = (from, to, radius) => blocked ? from : to;

        // Walk the evidence up to one tick short of the timeout against the first target.
        var first = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        int guard = 0;
        while (zombie.BlockedRouteTime + ImpassableDt + 1e-4f < ZombieSystem.BlockedRouteTimeout)
        {
            system.Tick(new[] { first }, ImpassableDt);
            Assert.True(++guard < 100, "never accumulated blocked evidence");
        }
        Assert.True(zombie.BlockedRouteTime > 0f);
        Assert.NotEmpty(zombie.PathPoints);

        // A closer player steals the target. The evidence belonged to the old destination.
        var second = Player(2, new Vector3(-6, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint, moving: true);
        system.Tick(new[] { first, second }, ImpassableDt);

        Assert.Equal(2, zombie.TargetPlayer);
        // The retargeting tick also moves, and the body is still blocked, so ONE tick of fresh
        // evidence is expected. What must not happen is the old total carrying over: 0.70 + 0.08 is
        // past the timeout, and the replacement would be thrown away on the tick it arrived.
        Assert.True(zombie.BlockedRouteTime <= ImpassableDt + 1e-4f,
            $"evidence carried across the retarget: {zombie.BlockedRouteTime}");
        Assert.NotEmpty(zombie.PathPoints); // and the route itself still survives, as intended
    }

    // Codex review, P1. The kept route is still scored as the incumbent a replacement has to beat, but
    // its endpoint was chosen for someone else. When the new target is only reachable partially, an old
    // endpoint that happens to sit nearer the new player rejects every replacement forever — and if it
    // sits within EndReachedDistance of that player the step goes to zero, so no blocked evidence
    // accumulates and even the timeout cannot rescue it.
    [Fact]
    public void ARouteBuiltForAnotherTarget_DoesNotVetoTheFirstReplacement()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        zombie.Position = new Vector3(0f, 5f, 0f);
        bool retargeted = false;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            // Before the steal: a complete route, ending on the target. After: the new target is only
            // reachable partially, and the best endpoint the graph can offer stops well short of it.
            path.Add(retargeted ? new Vector3(3f, 5f, 0f) : to);
            return true;
        };
        // Frozen: the geometry that decides the scoring must not drift while the test runs.
        system.MoveResolver = (from, to, radius) => from;

        // Target 1 sits just BEYOND target 2, so the route built for it ends close to target 2 — much
        // closer than anything reachable for target 2 itself.
        var first = Player(1, new Vector3(10f, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 2; tick++)
            system.Tick(new[] { first }, ImpassableDt);
        Assert.Equal(1, zombie.TargetPlayer);
        Vector3 stale = Assert.IsType<List<Vector3>>(zombie.PathPoints)[^1];

        // Target 2 is nearer the zombie, so it steals; its own reachable endpoint (2,5,0) is 3 m from
        // it, while the inherited endpoint is under a metre away and would win on score alone.
        retargeted = true;
        var second = Player(2, new Vector3(9f, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint, moving: true);
        for (int tick = 0; tick < 2; tick++)
            system.Tick(new[] { first, second }, ImpassableDt);

        Assert.Equal(2, zombie.TargetPlayer);
        Assert.True(zombie.BlockedRouteTime < ZombieSystem.BlockedRouteTimeout); // not a timeout rescue
        Assert.False(zombie.RouteServedAnotherTarget, "the inherited route was never replaced");
        Assert.NotEqual(stale, zombie.PathPoints[^1]);
        Assert.Equal(3f, zombie.PathPoints[^1].X, 1);
    }

    [Fact]
    public void APartialEscapeRoute_CannotVetoACompleteReplacement()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint, moving: true);
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(1, zombie.TargetPlayer);

        // This route was partial for an earlier destination. The moving target now puts its endpoint
        // inside the completeness tolerance, but the route is still known not to reach its old request.
        zombie.Position = new Vector3(0f, 5f, 0f);
        zombie.Yaw = -90f;
        zombie.Path = EZombiePath.Rush; // destination is one metre short: (9, 5, 0)
        zombie.PathPoints.Clear();
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(new Vector3(9f, 5f, 0f));
        zombie.CurrentWaypointIndex = 1;
        zombie.TargetReached = false;
        zombie.PathIsPartial = true;
        zombie.RouteServedAnotherTarget = false;
        zombie.RouteEscapingCollision = true;
        zombie.EscapeRouteHasProgress = true;
        zombie.RepathTimer = -1f;
        zombie.RepathGranted = true;

        var replacementCorner = new Vector3(4f, 5f, -3f);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(replacementCorner);
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, _, _) => from;
        system.VisionBlocked = (_, _) => false;
        system.PhysicalLineBlocked = (_, _) => true; // recovery is still crossing its obstacle

        system.Tick(new[] { player }, 0.1f);

        Assert.False(zombie.PathIsPartial);
        Assert.Contains(replacementCorner, zombie.PathPoints);
    }

    // CalculateTargetPoint divides by the active segment's length, and the comment above it claimed a
    // degenerate segment could not reach it because "the final segment short-circuits to the
    // destination". That short-circuit is conditional: it only fires when the route's end is within 4 m
    // of the destination. A stale route whose end sits ON the body — Leave falls back to the current
    // position when neither retreat direction navigates, and a query from a point to itself answers with
    // that point — outlives the retarget deliberately (routes survive a target change so a horde does
    // not freeze mid-chase), and the new target is nowhere near it. Then a == b, the divisor is zero,
    // and the NaN reaches Yaw and Position through SteerDirection and the step.
    [Fact]
    public void ARouteThatEndsOnTheBody_DoesNotPoisonItWithNaN()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.MoveResolver = (from, to, radius) => to;
        system.PathQuery = (from, to, path, radius) => false; // no route of its own to interfere

        var near = Player(1, new Vector3(10f, 5f, 0f), UnturnedGodot.Player.EPlayerStance.Sprint);
        int guard = 0;
        while (zombie.State != EZombieState.Chase)
        {
            system.Tick(new[] { near }, 0.1f);
            Assert.True(++guard < 100, "never entered Chase");
        }

        // Still well inside MaxChaseDistanceSquared, so the chase holds, but far past the 4 m the
        // direct-to-destination short-circuit needs.
        var player = Player(1, new Vector3(30f, 5f, 0f), UnturnedGodot.Player.EPlayerStance.Sprint);

        // The stale route: one waypoint, exactly where the body stands, with the target 30 m off.
        zombie.PathPoints.Clear();
        zombie.PathPoints.Add(zombie.Position);
        zombie.CurrentWaypointIndex = 0;
        zombie.TargetReached = false;
        zombie.RepathTimer = 1f; // and this tick is not one it may repath on

        system.Tick(new[] { player }, 0.05f);

        Assert.False(float.IsNaN(zombie.Position.X) || float.IsNaN(zombie.Position.Y)
            || float.IsNaN(zombie.Position.Z), $"position went NaN: {zombie.Position}");
        Assert.False(float.IsNaN(zombie.Yaw), "yaw went NaN");
    }

    [Fact]
    public void ACompleteRouteOnAnotherFloor_CannotVetoTheStairRoute()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        var first = Player(1, new Vector3(10f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        system.Tick(new[] { first }, 0.1f); // acquire player 1

        // The incumbent ends directly below the player's new position. In XZ it looks perfect, but it
        // is on the wrong storey and cannot be allowed to veto the longer route up the stairs.
        zombie.Position = new Vector3(0f, 5f, 0f);
        zombie.PathPoints.Clear();
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(zombie.Position);
        zombie.CurrentWaypointIndex = 1;
        zombie.TargetReached = false;
        zombie.PathIsPartial = false;
        zombie.RouteServedAnotherTarget = false;
        zombie.RepathTimer = -1f;

        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(new Vector3(5f, 5f, 0f));
            path.Add(new Vector3(5f, 8f, 0f));
            path.Add(to);
            return true;
        };
        var upstairs = Player(1, new Vector3(0f, 8f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);

        system.Tick(new[] { upstairs }, 0.1f);

        Assert.Equal(4, zombie.PathPoints.Count);
        Assert.Equal(8f, zombie.PathPoints[^1].Y, 2);
        Assert.True(zombie.Position.X > 0f,
            $"the downstairs route kept vetoing the stairs: {zombie.Position}");
    }

    [Fact]
    public void ACompleteEndpointDiagonallyOutsideTheCompletionSphere_CannotVetoStairs()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;
        zombie.Yaw = -90f;
        zombie.Path = EZombiePath.Rush;
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(zombie.Position); // 2.5 m horizontal and 1.9 m below the player
        zombie.CurrentWaypointIndex = 1;
        zombie.RepathTimer = -1f;
        zombie.RepathGranted = true;

        Vector3 stair = new(5f, 5f, 0f);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(stair);
            path.Add(stair with { Y = 6.9f });
            path.Add(to);
            return true;
        };
        var player = Player(1, new Vector3(2.5f, 6.9f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);

        system.Tick(new[] { player }, ImpassableDt);

        Assert.Contains(stair, zombie.PathPoints);
        Assert.Equal(6.9f, zombie.PathPoints[^1].Y, 2);
    }

    [Fact]
    public void CompleteRouteLength_UsesTheDestinationTheFollowerActuallyWalksTo()
    {
        Vector3 position = Vector3.Zero;
        var route = new List<Vector3>
        {
            position,
            new Vector3(4f, 0f, 0f),
            new Vector3(9f, 0f, 1.5f), // within 2 m: the follower ignores this final endpoint
        };
        Vector3 destination = new(10f, 0f, 0f);

        float length = ZombieSystem.EffectiveRouteLengthFrom(position, route, 1, destination);

        Assert.Equal(10f, length, 3); // 0 -> 4 -> the real destination, not 0 -> 4 -> (9, 1.5)
    }

    [Fact]
    public void IncompleteRouteLength_KeepsItsOwnEndpointOutsideTheDirectApproachRange()
    {
        Vector3 position = Vector3.Zero;
        var route = new List<Vector3>
        {
            position,
            new Vector3(4f, 0f, 0f),
            new Vector3(4f, 0f, 5f),
        };
        Vector3 destination = new(10f, 0f, 0f);

        float length = ZombieSystem.EffectiveRouteLengthFrom(position, route, 1, destination);

        Assert.Equal(9f, length, 3); // 0 -> 4 -> (4, 5), not 0 -> 4 -> the destination
    }

    // Two ways round the same obstacle, of near-equal cost, and the body standing where the choice
    // between them flips. Every repath the graph answers with the other one — both COMPLETE, both
    // ending on the player — and the follower turns to face whichever arrived last.
    //
    // Reproduced from a capture: a chaser turned 13,872 degrees and covered 164 m to move 2 m, while
    // BlockedRouteTime stayed at zero the whole time, because it WAS delivering motion. That is the
    // hole this pins. The blocked-route timeout measures whether the body moves, and a body shuttling
    // between two spots moves beautifully; nothing measured whether it was getting anywhere.
    [Fact]
    public void TwoEqualRoutesArrivingByTurns_DoNotSpinTheBodyInPlace()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(14f, 5f, 0f), UnturnedGodot.Player.EPlayerStance.Sprint);

        // Both reach the player; they differ only in which side they leave by, which is what makes a
        // near-tie flip on sub-metre movement in the real case.
        bool other = false;
        system.PathQuery = (from, to, path, radius) =>
        {
            other = !other;
            path.Add(from);
            path.Add(new Vector3(from.X + 2f, from.Y, from.Z + (other ? 5f : -5f)));
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) => to; // open ground: the body is never blocked

        float turned = 0f, previousYaw = zombie.Yaw;
        for (int tick = 0; tick < 200; tick++)
        {
            system.Tick(new[] { player }, ImpassableDt);
            if (zombie.State != EZombieState.Chase)
                continue;
            turned += MathF.Abs(Mathf.Wrap(zombie.Yaw - previousYaw, -180f, 180f));
            previousYaw = zombie.Yaw;
        }

        float closed = 14f - new Vector2(zombie.Position.X - 14f, zombie.Position.Z).Length();
        Assert.True(closed > 8f,
            $"closed only {closed:F2} m of 14 while turning {turned:F0} degrees");
        // A body that keeps changing its mind spends its distance on rotation. One full turn is
        // generous for a chase that is supposed to run more or less straight at a stationary target.
        Assert.True(turned < 360f, $"turned {turned:F0} degrees to close {closed:F2} m");
    }

    [Fact]
    public void AStationaryRushTarget_DoesNotLookStaleWhenTheZombieTurns()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(0f, 5f, 10f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        zombie.Position = new Vector3(0f, 5f, 0f);
        zombie.Yaw = 0f;
        zombie.State = EZombieState.Chase;
        zombie.TargetPlayer = 1;
        zombie.Path = EZombiePath.Rush;

        // The incumbent was built when the one-metre Rush offset lay on the near side of the player.
        // The body has since turned around, so today's offset lies on the far side: those two synthetic
        // destinations are just over 2 m apart even though the player has not moved at all.
        var incumbentCorner = new Vector3(-5f, 5f, 5f);
        zombie.PathPoints.Add(zombie.Position);
        zombie.PathPoints.Add(incumbentCorner);
        zombie.PathPoints.Add(new Vector3(0.2f, 5f, 9f));
        zombie.CurrentWaypointIndex = 1;
        zombie.RepathTimer = -1f;

        var replacementCorner = new Vector3(5f, 5f, 5f);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(replacementCorner);
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, _, _) => from;

        system.Tick(new[] { player }, ImpassableDt);

        Assert.Contains(incumbentCorner, zombie.PathPoints);
        Assert.DoesNotContain(replacementCorner, zombie.PathPoints);
    }

    // The tie-break above lets a nearly-finished incumbent refuse a much longer replacement. On its
    // own that could let a stale route hold the body forever. It does not, and this pins why: the
    // follower still aims at the destination, into the wall, so it keeps delivering nothing against a
    // non-zero request and BlockedRouteTimeout throws it out. That is load-bearing, not incidental —
    // disarm the timeout and this exact geometry parks the body 2.51 m from its target, in Chase, forever.
    //
    // The target is deliberately stationary and the staleness is injected directly, as the first
    // answer the graph gives. Walking a player behind the wall instead would move the destination with
    // it, and `oldError` would leave its 2 m arming radius on its own — disarming the veto, and with
    // it the whole point of the test.
    [Fact]
    public void AStaleRouteThatStillScoresAsArriving_DoesNotStrandTheBody()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var player = Player(1, new Vector3(10f, 5f, 0f));

        // A wall along x = 7.5 that ends at z = -6, in the PHYSICS as well as the graph. A wall only
        // in the graph lets the body coast the last stretch straight through it, which is how an
        // earlier version of this test passed with the bug still in place.
        const float wallX = 7.5f, wallEnd = -6f;
        system.MoveResolver = (from, to, radius) =>
            from.X < wallX && to.X >= wallX && to.Z > wallEnd
                ? to with { X = wallX - 0.01f }
                : to;

        int queries = 0;
        Vector3 stale = Vector3.Zero;
        var corner = new Vector3(7.2f, 5f, -6.5f);
        bool rounded = false;
        system.PathQuery = (from, to, path, radius) =>
        {
            if (queries++ == 0)
                return false; // the graph is not answering yet: the body stands, and nothing is pinned
            path.Add(from);
            if (queries == 2)
            {
                // The answer built before the target stepped behind the wall, stopping 1.9 m short of
                // the destination the follower asked for -- inside the 2 m that still counts as
                // arriving. Measured against that destination and not the player, because the approach
                // path offsets it a metre and it is the destination completeness is judged against.
                stale = to + ((from - to) with { Y = 0f }).Normalized() * 1.9f;
                path.Add(stale);
                return true;
            }
            // The way that still arrives, round the end of the wall: about 16 m against a route with
            // a short final leg, so it cannot win on length. A corner already turned is dropped, or the
            // fixture would keep dragging the body back to it and be the thing that oscillates.
            if (!rounded)
            {
                rounded = new Vector2(from.X - corner.X, from.Z - corner.Z).Length() < 1.5f;
                if (!rounded)
                {
                    path.Add(corner);
                    path.Add(corner with { X = 8.2f });
                }
            }
            path.Add(to);
            return true;
        };

        system.Tick(new[] { player }, ImpassableDt); // aggro: the approach path is rolled here
        // Pin it to RUSH so the destination offset points back along the body's own forward. LEFT and
        // RIGHT offset sideways, which puts every endpoint that still counts as complete within 2 m of
        // the player -- inside the range where the follower stops steering by the route and turns
        // straight at the target, and no route decision is observable at all.
        zombie.Path = EZombiePath.Rush;

        for (int tick = 0; tick < 600; tick++)
            system.Tick(new[] { player }, ImpassableDt);

        float gap = new Vector2(zombie.Position.X - 10f, zombie.Position.Z).Length();
        Assert.True(rounded, $"never went round the wall: parked {gap:F2} m out in {zombie.State}; "
            + $"escape={zombie.RouteEscapingCollision}/{zombie.CollisionEscapeRoute}, "
            + $"blocked={zombie.BlockedRouteTime:F2}, wp={zombie.CurrentWaypointIndex}: "
            + string.Join(" ", zombie.PathPoints));
        Assert.True(gap < 1f, $"stopped {gap:F2} m short in {zombie.State} on the stale route");
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    private const float ImpassableDt = 0.1f;

    private readonly record struct ImpassableRun(int FirstBlockedTick, int InvalidatedAtTick,
        int QueriesAtFirstBlock, int QueriesAtInvalidation, float RepathTimerAfter,
        int WaypointIndexAfter, bool PartialAfter);

    private static ImpassableRun RunAgainstAnImpassableCompleteRoute()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            path.Add(to); // ends exactly on the target: always "complete", so always accepted
            return true;
        };
        system.MoveResolver = (from, to, radius) => from; // and always refused by the physics world

        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        int firstBlocked = -1, queriesAtBlock = 0;
        // Generous enough that a counter reset by even one repath would run out of ticks here.
        int budget = (int)MathF.Ceiling(ZombieSystem.BlockedRouteTimeout / ImpassableDt) * 6;
        for (int tick = 1; tick <= budget; tick++)
        {
            system.Tick(new[] { player }, ImpassableDt);
            if (firstBlocked < 0 && zombie.PathPoints.Count > 0 && zombie.BlockedRouteTime > 0f)
            {
                firstBlocked = tick;
                queriesAtBlock = queries;
            }
            if (firstBlocked > 0 && zombie.PathPoints.Count == 0)
                return new ImpassableRun(firstBlocked, tick, queriesAtBlock, queries,
                    zombie.RepathTimer, zombie.CurrentWaypointIndex, zombie.PathIsPartial);
        }
        return new ImpassableRun(firstBlocked, -1, queriesAtBlock, queries,
            zombie.RepathTimer, zombie.CurrentWaypointIndex, zombie.PathIsPartial);
    }

    private static (List<Vector3> Trace, int Queries, EZombieState State) RunWindowRecovery()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            if (queries == 1)
            {
                // The stale route ends exactly at the target but crosses the window sill.
                path.Add(to);
            }
            else
            {
                // The reconciled graph can reach the far side through the door, but reports a partial
                // endpoint. Endpoint-only replacement rejects this forever while the stale route remains.
                path.Add(new Vector3(3.5f, 5f, 3f));
                path.Add(new Vector3(7f, 5f, 4f));
            }
            return true;
        };
        system.MoveResolver = (from, to, radius) =>
        {
            // Wall at x=4 with a door for z>=2. The direct route hits its window at z=0.
            if (from.X <= 4f && to.X > 4f && MathF.Max(from.Z, to.Z) < 2f)
                return new Vector3(4f, to.Y, to.Z);
            return to;
        };

        var player = Player(1, new Vector3(10, 5, 0),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        var trace = new List<Vector3>();
        for (int tick = 0; tick < 100; tick++)
        {
            system.Tick(new[] { player }, 0.1f);
            trace.Add(zombie.Position);
        }
        return (trace, queries, zombie.State);
    }

    [Fact]
    public void BriefPhysicalBlock_DoesNotDiscardAHealthyRoute()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        int queries = 0, blockedSteps = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            if (queries > 1)
                return false; // recovery must continue on the original route
            path.Add(from);
            path.Add(to);
            return true;
        };
        system.MoveResolver = (from, to, radius) => blockedSteps++ < 5 ? from : to;

        var player = Player(1, new Vector3(10, 5, 0),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int tick = 0; tick < 50; tick++)
            system.Tick(new[] { player }, 0.1f);

        Assert.True(5 * 0.1f < ZombieSystem.BlockedRouteTimeout);
        Assert.True(zombie.Position.X > 8f, $"brief contact discarded the route: {zombie.Position}");
        Assert.Equal(EZombieState.Attack, zombie.State);
    }

    // The regression behind the window-sill shuffle: before sustained collision has invalidated the
    // route, the follower must hand the same forward step to the resolver every tick and accept the
    // answer. A bounded, physically-verified detour may probe later; this pins that ordinary following
    // still has no per-tick left/right sidestep, which was what made the zombie visibly pace at a sill.
    [Fact]
    public void BlockedZombie_NeverProbesSideways()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };

        var asked = new List<Vector3>();
        system.MoveResolver = (from, to, radius) =>
        {
            asked.Add(to - from);
            return from; // a wall dead ahead
        };

        var player = Player(1, new Vector3(10, 5, 0), UnturnedGodot.Player.EPlayerStance.Sprint);
        int ordinaryFollowerTicks = (int)MathF.Floor(
            ZombieSystem.BlockedRouteTimeout / ImpassableDt) - 1;
        for (int i = 0; i < ordinaryFollowerTicks; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.NotEmpty(asked);
        // Every request is the same forward step: no tangent probes, so no side to alternate.
        foreach (Vector3 motion in asked)
            Assert.True(MathF.Abs(motion.Z) <= MathF.Abs(motion.X) * 0.05f,
                $"brain probed sideways with {motion}");
    }

    [Fact]
    public void Separation_IsReResolvedAgainstTheWorld()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 8).Select(i => At(i * 0.01f, 0)).ToList(), new Random(2));
        Assert.Equal(2, system.Zombies.Count);
        ZombieInstance mover = system.Zombies[0];
        ZombieInstance blocker = system.Zombies[1];
        mover.Speciality = EZombieSpeciality.Normal;
        mover.Yaw = -90f;
        mover.Position = new Vector3(1.0f, 5, 0.1f); // squeezing between the blocker and a wall
        blocker.Position = new Vector3(1.6f, 5, -0.6f);

        var resolves = new List<Vector3>();
        system.MoveResolver = (from, to, radius) =>
        {
            resolves.Add(to);
            return to.Z > 0.5f ? new Vector3(to.X, to.Y, 0.5f) : to; // a wall along z = 0.5
        };

        var player = Player(1, new Vector3(10, 5, 0.2f), UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int i = 0; i < 20; i++)
            system.Tick(new[] { player }, 0.1f);

        // Separation pushed the mover up-Z past the wall line at least once, and the re-resolve
        // clamped it back: the mover never ends a tick beyond the wall.
        Assert.True(mover.Position.Z <= 0.5f + 0.001f, $"pushed through the wall: {mover.Position}");
    }

    [Fact]
    public void ARouteBlockedOnlyByAnotherZombie_IsNotInvalidatedAsAWorldCollision()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 8).Select(i => At(i * 0.01f, 0)).ToList(), new Random(2));
        Assert.Equal(2, system.Zombies.Count);
        ZombieInstance mover = system.Zombies[0];
        ZombieInstance blocker = system.Zombies[1];
        mover.Speciality = blocker.Speciality = EZombieSpeciality.Normal;
        mover.Yaw = blocker.Yaw = -90f;
        mover.Position = new Vector3(0f, 5f, 0f);
        blocker.Position = new Vector3(1f, 5f, 0f);

        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            path.Add(to);
            return true;
        };
        // A narrow straight corridor accepts the route's forward component while preventing the
        // separation response from simply walking around the idle capsule.
        system.MoveResolver = (from, to, radius) => to with { Z = 0f };
        // Only the rear zombie acquires the player; the front body remains a stable idle member of
        // the same crowd instead of walking away and turning this into an open-ground fixture.
        system.VisionBlocked = (from, to) => from.X > 0.5f;

        // The idle front zombie's capsule holds the chaser in place for longer than the
        // route-collision timeout.
        var player = Player(1, new Vector3(1.75f, 5f, 0f),
            UnturnedGodot.Player.EPlayerStance.Sprint);
        int ticks = (int)MathF.Ceiling(ZombieSystem.BlockedRouteTimeout / ImpassableDt) * 3;
        float farthest = mover.Position.X;
        for (int tick = 0; tick < ticks; tick++)
        {
            system.Tick(new[] { player }, ImpassableDt);
            farthest = MathF.Max(farthest, mover.Position.X);
        }

        Assert.True(queries >= 2, "the fixture never crossed a repath boundary");
        Assert.True(farthest < 0.3f, $"the rear zombie was not actually crowd-blocked: x={farthest}; "
            + $"mover={mover.Position}/{mover.State}/{mover.TargetPlayer}, "
            + $"blocker={blocker.Position}/{blocker.State}/{blocker.TargetPlayer}");
        Assert.Equal(0f, mover.BlockedRouteTime);
        Assert.False(mover.RouteEscapingCollision);
        Assert.NotEmpty(mover.PathPoints);
    }

    [Fact]
    public void SinglePointRoutes_AndZeroDeltaTicks_AreHandled()
    {
        // A route of one point (the destination itself) and a zero-delta tick both flow through
        // the follower without dividing by zero or moving anyone anywhere strange.
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = -90f;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(to); // single-point route
            return true;
        };

        var player = Player(1, new Vector3(10, 5, 0));
        system.Tick(new[] { player }, 0.1f);
        system.Tick(new[] { player }, 0f); // zero-delta: the overshoot clamp's dt guard
        for (int i = 0; i < 30; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.False(float.IsNaN(zombie.Position.X));
        Assert.Equal(EZombieState.Attack, zombie.State); // still arrived and attacked
    }

    [Fact]
    public void TargetStandingOnTheZombie_TurnsNobodyToNaN()
    {
        // sqrHorizontal == 0: inside 2 m the steer direction is the zero vector — RotateTowards
        // must ignore it (the original's dir == Vector3.zero check).
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(to);
            return true;
        };
        float yaw = zombie.Yaw;
        var onTop = Player(1, zombie.Position, UnturnedGodot.Player.EPlayerStance.Sprint);
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { onTop }, 0.1f);
        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.Equal(yaw, zombie.Yaw); // a zero direction never spins or NaNs the body
        Assert.False(float.IsNaN(zombie.Yaw));
    }

    [Fact]
    public void TickWithNoZombies_IsANoOp()
    {
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        system.Tick(new[] { Player(1, new Vector3(0, 5, 0)) }, 0.1f); // empty population: no work
        Assert.Empty(system.Zombies);
    }


    [Fact]
    public void Retargeting_RequestsAFreshPath()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path, radius) =>
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

    [Fact]
    public void Retargeting_KeepsTheCurrentRouteUntilTheReplacementArrives()
    {
        // Changing a destination in the original only SCHEDULES a Seeker query: vectorPath is written
        // in OnPathComplete and nowhere else, so LegacyAIPathNoRedist keeps following the route it has
        // while the replacement computes. Dropping the route instead parks the zombie on Move()'s "no
        // route: stand" branch, and the replacement is not free — it waits for one of the
        // MaxRepathsPerTick tokens. A horde re-targets on ONE detect scan, so the queue is longer than
        // the budget and the surplus stands still mid-chase. Clients read move/idle off the replicated
        // motion, so those zombies visibly drop aggro and pick it straight back up.
        const int Horde = 12; // > MaxRepathsPerTick: the budget has to be the binding constraint
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround);
        var spawns = new List<ZombieSpawnpointData>();
        for (int i = 0; i < Horde * 4; i++)
            spawns.Add(At((i % Horde) - (Horde / 2f), 0));
        system.Spawn(spawns, new Random(5));
        Assert.Equal(Horde, system.Zombies.Count);
        foreach (ZombieInstance z in system.Zombies)
        {
            z.Speciality = EZombieSpeciality.Normal;
            z.Yaw = 180f; // facing +Z, toward the first target
        }
        int queries = 0;
        system.PathQuery = (from, to, path, radius) =>
        {
            queries++;
            path.Add(from);
            path.Add(to);
            return true;
        };

        // Everyone locks onto the sprinting player 1 and gets a live route.
        var first = Player(1, new Vector3(0, 5, 20), UnturnedGodot.Player.EPlayerStance.Sprint, moving: true);
        for (int t = 0; t < 8; t++)
            system.Tick(new[] { first }, UnturnedGodot.Net.ServerSimulation.TickRate);
        Assert.All(system.Zombies, z => Assert.Equal(EZombieState.Chase, z.State));
        Assert.All(system.Zombies, z => Assert.NotEmpty(z.PathPoints));

        // Player 2 steps in on the other side, closer than player 1: the whole horde re-targets on the
        // same detect scan, and only MaxRepathsPerTick of them can be served this tick.
        var before = system.Zombies.Select(z => z.Position).ToList();
        queries = 0;
        var second = Player(2, new Vector3(0, 5, -12), UnturnedGodot.Player.EPlayerStance.Sprint, moving: true);
        system.Tick(new[] { first, second }, UnturnedGodot.Net.ServerSimulation.TickRate);

        Assert.All(system.Zombies, z => Assert.Equal(2, z.TargetPlayer));
        Assert.Equal(ZombieSystem.MaxRepathsPerTick, queries); // the queue really did overflow
        int routeless = 0, frozen = 0;
        for (int i = 0; i < system.Zombies.Count; i++)
        {
            if (system.Zombies[i].PathPoints.Count == 0)
                routeless++;
            if (system.Zombies[i].Position == before[i])
                frozen++;
        }
        Assert.Equal(0, routeless); // Horde - MaxRepathsPerTick of them, when the route is discarded
        Assert.Equal(0, frozen);
    }

    private static List<NavFlag> OneTriangle(float halfExtent, float y) => new()
    {
        new()
        {
            Center = new Vector3(0, 140, 0), Size = new Vector3(halfExtent * 2f, 236, halfExtent * 2f),
            Vertices = new[]
            {
                new Vector3(-20, y, -20), new Vector3(20, y, -20), new Vector3(0, y, 20),
            },
            Triangles = new[] { 0, 1, 2 },
        },
    };

    [Fact]
    public void SeekOrigin_IsSnappedToTheNavmeshToo()
    {
        // Both ends of the query are snapped, not just the destination. A zombie nudged off the mesh by
        // collision — or standing on a floor the mesh does not cover — would otherwise have its route
        // START at whatever polygon is nearest in 3D, which can be behind or below it. The route then
        // walks it back there first, and that reads in game as a pointless detour.
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround, OneTriangle(68f, 5.4f));
        system.Spawn(new[] { At(0, 0) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Yaw = 0f;

        var origins = new List<Vector3>();
        system.PathQuery = (from, to, path, radius) =>
        {
            origins.Add(from);
            path.Add(from);
            path.Add(to);
            return true;
        };

        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Vector3 from = Assert.Single(origins);
        Assert.Equal(5.4f, from.Y, 2); // lifted onto the triangle's level, not the zombie's raw ground Y
    }

    [Fact]
    public void SeekDestination_IsSnappedToTheNavmeshBeforeQuerying()
    {
        // The brain owns the navmesh triangles: the query destination must arrive pre-snapped
        // (XZ-first) so the host's 3D closest-point lookup can never flip floors.
        var flags = new List<NavFlag>
        {
            new()
            {
                Center = new Vector3(0, 140, 0), Size = new Vector3(136, 236, 136),
                Vertices = new[]
                {
                    new Vector3(-20, 5.4f, -20), new Vector3(20, 5.4f, -20), new Vector3(0, 5.4f, 20),
                },
                Triangles = new[] { 0, 1, 2 },
            },
        };
        var system = new ZombieSystem(new[] { Table() }, TwoBounds(), FlatGround, flags);
        system.Spawn(new[] { At(0, 0) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Yaw = 0f;

        var destinations = new List<Vector3>();
        system.PathQuery = (from, to, path, radius) =>
        {
            destinations.Add(to);
            path.Add(from);
            path.Add(to);
            return true;
        };

        system.Tick(new[] { Player(1, new Vector3(10, 5, 0)) }, 0.1f);
        Vector3 to = Assert.Single(destinations);
        Assert.Equal(5.4f, to.Y, 2); // lifted onto the triangle's level, not the player's raw 5.0
        // XZ stays within the one-metre approach offset (plus the edge projection where the
        // offset point leaves the triangle).
        Assert.InRange(new Vector2(to.X - 10f, to.Z).Length(), 0.7f, 1.2f);
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
    public void AThinWallInsideAttackRange_BlocksTheSwingAndDamage()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<byte>();
        system.OnAttack += (_, _, damage) => hits.Add(damage);
        system.MoveResolver = (from, _, _) => from;

        // Acquire while visible and just outside normal attack range, then put the same target within
        // reach on the other side of a fence. Distance alone must not start an attack through it.
        system.VisionBlocked = (_, _) => false;
        system.Tick(new[] { Player(1, new Vector3(1.2f, 5f, 0f)) }, 0.1f);
        system.VisionBlocked = (_, _) => true;
        var behindFence = Player(1, new Vector3(0.8f, 5f, 0f));
        for (int i = 0; i < 20; i++)
            system.Tick(new[] { behindFence }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.True(zombie.PendingHit < 0f);
        Assert.Empty(hits);
    }

    [Fact]
    public void FullPhysicalSegment_BlocksAFenceThatPerceptionDoesNotSee()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<byte>();
        system.OnAttack += (_, _, damage) => hits.Add(damage);
        system.MoveResolver = (from, _, _) => from;
        system.VisionBlocked = (_, _) => false;
        int physicalQueries = 0;
        system.PhysicalLineBlocked = (from, to) =>
        {
            physicalQueries++;
            Assert.Equal(5f + UnturnedGodot.Player.PlayerConfig.EyeHeightStand, to.Y, 3);
            return true; // thin fence in the final 5% of the visual segment
        };
        var behindFence = Player(1, new Vector3(0.8f, 5f, 0f));

        for (int i = 0; i < 20; i++)
            system.Tick(new[] { behindFence }, 0.1f);

        Assert.True(physicalQueries > 0);
        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.True(zombie.PendingHit < 0f);
        Assert.Empty(hits);
    }

    [Fact]
    public void ClosePhysicalBarrier_UsesTheDetourBeforeCollisionTimeout()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        zombie.Yaw = 0f; // the detour heads -Z; direct pursuit of the player would turn toward +X
        system.VisionBlocked = (_, _) => false; // final-five-percent perception miss
        system.PhysicalLineBlocked = (_, _) => true;
        system.MoveResolver = (from, _, _) => from;
        system.PathQuery = (from, to, path, radius) =>
        {
            path.Add(from);
            path.Add(from + new Vector3(0f, 0f, -6f));
            path.Add(to);
            return true;
        };

        system.Tick(new[] { Player(1, new Vector3(1.2f, 5f, 0f)) }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.Equal(1, zombie.TargetPlayer);
        Assert.False(zombie.RouteEscapingCollision); // no timeout/recovery flag was needed
        Assert.True(MathF.Abs(zombie.Yaw) < 1f || MathF.Abs(zombie.Yaw - 360f) < 1f,
            $"the close-wall override still faced through the barrier: yaw={zombie.Yaw}");
    }

    [Fact]
    public void AWallAppearingDuringTheSwing_BlocksThePendingHit()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<byte>();
        system.OnAttack += (_, _, damage) => hits.Add(damage);
        system.MoveResolver = (from, _, _) => from;
        var player = Player(1, new Vector3(0.8f, 5f, 0f));

        system.VisionBlocked = (_, _) => false;
        system.Tick(new[] { player }, 0.1f);
        Assert.Equal(EZombieState.Attack, zombie.State);
        Assert.True(zombie.PendingHit >= 0f);

        // The target stepped behind the fence after the animation began but before attackTime/2.
        system.VisionBlocked = (_, _) => true;
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Equal(EZombieState.Chase, zombie.State);
        Assert.Empty(hits);
    }

    [Fact]
    public void FullPhysicalSegment_IsRecheckedAtThePendingHitFrame()
    {
        ZombieSystem system = SpawnOne(out ZombieInstance zombie);
        var hits = new List<byte>();
        system.OnAttack += (_, _, damage) => hits.Add(damage);
        system.MoveResolver = (from, _, _) => from;
        system.VisionBlocked = (_, _) => false;
        bool fence = false;
        system.PhysicalLineBlocked = (_, _) => fence;
        var player = Player(1, new Vector3(0.8f, 5f, 0f));

        system.Tick(new[] { player }, 0.1f);
        Assert.True(zombie.PendingHit >= 0f);
        fence = true;
        for (int i = 0; i < 5; i++)
            system.Tick(new[] { player }, 0.1f);

        Assert.Empty(hits);
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
    // GetStealthDetectionRadius groups CLIMB with CROUCH: a player on a ladder is heard as a croucher.
    [InlineData(UnturnedGodot.Player.EPlayerStance.Climb, false, 6f)]
    [InlineData(UnturnedGodot.Player.EPlayerStance.Climb, true, 6.6f)]
    public void DetectionRadii_MatchPlayerStance(
        UnturnedGodot.Player.EPlayerStance stance, bool moving, float expected)
    {
        Assert.Equal(expected, ZombieDetection.RadiusFor(stance, moving), 4);
    }

    [Fact]
    public void DetectionRadius_ClampsToTheAlertToolFloor()
    {
        // A stance the table has no entry for at all. Not 0: that is Climb, which the game does answer.
        Assert.Equal(1f, ZombieDetection.RadiusFor((UnturnedGodot.Player.EPlayerStance)99, false));
    }
}
