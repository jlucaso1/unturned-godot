using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Net;
using UnturnedGodot.Player;

namespace UnturnedGodot.Zombies;

// EZombieSpeciality, reduced to the kinds PEI's NORMAL difficulty actually rolls at spawn
// (flanker/burner/acid/boss variants come from harder difficulty assets).
public enum EZombieSpeciality : byte
{
    Normal = 0,
    Crawler = 1,
    Sprinter = 2,
    Mega = 3,
}

// The animation-relevant behavior states a zombie replicates. Return covers walking back to the
// retreat point after giving up a hunt (Zombie.leave -> alert(leaveTo)).
public enum EZombieState : byte
{
    Idle = 0,
    Chase = 1,
    Attack = 2,
    Return = 3,
}

// EZombiePath: how an aggroed zombie approaches its target. One third rush head-on; the rest drift
// a metre to their own left or right, which spreads a horde instead of stacking it on one line.
public enum EZombiePath : byte
{
    Rush = 0,
    Left = 1,
    Right = 2,
}

public sealed class ZombieInstance
{
    public ushort Id;
    public byte Bound;
    public byte Type; // zombie table index — picks the clothing/skin theme
    public EZombieSpeciality Speciality;
    public byte Shirt = byte.MaxValue;
    public byte Pants = byte.MaxValue;
    public byte Hat = byte.MaxValue;
    public byte Gear = byte.MaxValue;
    // The animation variant seeds ZombieManager rolls at spawn and replicates with the zombie:
    // clients play Move_{Move}/Idle_{Idle} (specialities override with their fixed variants).
    public byte Move;
    public byte Idle;
    public Vector3 Home;
    public Vector3 Position;
    public float Yaw; // degrees, player yaw convention: the model faces (-sin, 0, -cos)
    public EZombieState State;
    public byte TargetPlayer = byte.MaxValue;
    public EZombiePath Path;
    public float SinceSwing = float.PositiveInfinity; // seconds since the last swing started
    public float PendingHit = -1f;    // counts down from attackTime/2 to the damage landing
    public float LeaveDelay;          // Zombie.leave: stand still this long, then walk to LeaveTo
    public Vector3 LeaveTo;
    public sbyte DetourSide;          // sticky side while skirting a head-on block (see ApplyStep)
    public readonly List<Vector3> PathPoints = new(); // the Seeker's current route over the navmesh
    public int CurrentWaypointIndex;  // LegacyAIPathNoRedist.currentWaypointIndex
    public bool TargetReached;        // LegacyAIPathNoRedist.targetReached
    public Vector3 SteerDirection;    // LegacyAIPathNoRedist.targetDirection
    public float RepathTimer;         // counts down to the next path recalculation

    // ZombieManager.getZombieSpeed with Slow_Movement=false (NORMAL difficulty).
    public float Speed => Speciality switch
    {
        EZombieSpeciality.Crawler => 3f,
        EZombieSpeciality.Sprinter => 6.5f,
        EZombieSpeciality.Mega => 6f,
        _ => 5.5f,
    };

    // The CharacterController capsule (SetCapsuleRadiusAndHeight): megas 0.75, everyone else 0.4.
    public float Radius => Speciality == EZombieSpeciality.Mega ? 0.75f : 0.4f;

    // Zombie.GetHorizontalAttackRangeSquared for a player target on a dedicated server. The
    // official value IS the squared threshold (compared against sqrHorizontalDistanceFromTarget):
    // ATTACK_PLAYER(2) x 0.5 for NORMAL x 2 for megas — so normal 1 m, crawler/sprinter ~1.41 m,
    // mega 2 m of actual reach. Squaring it again made sprinters and megas stop conspicuously far.
    public float AttackRangeSquared => 2f
        * (Speciality == EZombieSpeciality.Normal ? 0.5f : 1f)
        * (Speciality == EZombieSpeciality.Mega ? 2f : 1f);

    // Zombie.GetVerticalAttackRange: hyper regions reach 3.5 m, everyone else 2.1, megas x1.5.
    public bool IsHyper; // the nav flag's hyperAgro, stamped at spawn
    public float VerticalAttackRange =>
        (IsHyper ? 3.5f : 2.1f) * (Speciality == EZombieSpeciality.Mega ? 1.5f : 1f);
}

// One player's zombie-relevant state for a tick, as the server simulation already knows it.
public readonly struct ZombiePlayerView
{
    public readonly byte Id;
    public readonly Vector3 Position;
    public readonly EPlayerStance Stance;
    public readonly bool Moving;

    public ZombiePlayerView(byte id, Vector3 position, EPlayerStance stance, bool moving)
    {
        Id = id;
        Position = position;
        Stance = stance;
        Moving = moving;
    }
}

// Blocks stealth detection when world geometry sits between the zombie's eyes and the alert
// position (AlertTool's BLOCK_VISION raycast). Null means an unobstructed world.
public delegate bool VisionBlocked(Vector3 from, Vector3 to);

// Resolves a zombie's step against the world's colliders the way its Unity CharacterController
// does — sliding along walls, trees and props instead of passing through them. Receives the
// current and desired positions plus the capsule radius; returns where the step actually ends.
// Null means an unobstructed world (the heightfield-only dedicated server).
public delegate Vector3 ZombieMoveResolver(Vector3 from, Vector3 to, float radius);

// Finds a walkable route over the level's pre-baked navmesh (the Seeker's A* + funnel). Fills
// waypoints from start to destination and returns whether any path exists; the caller falls back
// to the straight-line seek when there is none. Null means no navmesh (maps without nav data).
public delegate bool ZombiePathQuery(Vector3 from, Vector3 to, List<Vector3> path);

// Samples the REAL walking surface near a position — object floors, sidewalks, stairs — the way a
// CharacterController's grounding does, using the current height as the reference so stacked floors
// resolve to the right one. Null falls back to the terrain heightfield alone.
public delegate bool ZombieGroundSnap(Vector3 position, out float y);

// The server-side zombie brain: spawning per ZombieManager.generateZombies, aggro per AlertTool,
// hunting per Zombie.cs (approach paths, 64 m give-up, leave retreats, swing cadence). Movement is
// Unturned's NonPathfindingZombieMovementComponent — straight-line seek, 720°/s turning, and the
// CharacterController collision that makes crowds queue instead of stacking — so no navmesh is
// required, matching the game's own fallback movement model.
public sealed class ZombieSystem
{
    // ZombiesConfigData NORMAL difficulty.
    public const float SpawnChance = 0.25f;
    public const float CrawlerChance = 0.15f;
    public const float SprinterChance = 0.15f;

    // LegacyAIPathNoRedist (decompiled from the game assembly) with the values
    // CreateMovementComponentForZombie assigns.
    public const float RepathRate = 0.5f;               // repathRate
    public const float PickNextWaypointDist = 1f;       // pickNextWaypointDist
    public const float ForwardLook = 4f;                // forwardLook
    public const float EndReachedDistance = 0.75f;      // endReachedDistance
    public const float SlowdownDistance = 0.6f;         // slowdownDistance (component default)
    public const float TurningSpeed = 5f;               // turningSpeed (Quaternion.Slerp damping)
    public const float MinMoveScale = 0.05f;            // minMoveScale
    // The A* Pathfinding Project computes paths on a time budget per frame (AstarPath.maxFrameTime),
    // queueing the rest — a horde alerted by the same detect pass never recalculates in one burst.
    // Same shape here: at most this many queries per tick (~0.3-1.6 ms each against PEI's map);
    // zombies over budget keep their expired timer and drain on the following ticks.
    public const int MaxRepathsPerTick = 2;
    public const float MaxChaseDistanceSquared = 4096f; // Zombie.cs: target beyond 64 m -> leave
    public const float SwingInterval = 1f;              // Time.time - lastAttack > 1 starts a swing
    public const float AttackTime = 0.5f;               // dedicated-server Attack_0 fallback length
    public const float ArriveDistanceSquared = 3f;      // isMoving = sqrDistance > 3
    public const float TurnRateDegreesPerSecond = 720f; // NonPathfindingZombieMovementComponent

    private readonly IReadOnlyList<ZombieTable> _tables;
    private readonly IReadOnlyList<NavBound> _bounds;
    private readonly IReadOnlyList<NavFlag>? _navmesh; // pre-baked navmesh flags (null: none shipped)
    private readonly GroundSampler _ground;
    private readonly List<ZombieInstance> _zombies = new();
    private readonly List<ZombieInstance>[] _byBound;
    private readonly Dictionary<byte, int> _agro = new(); // Player.agro: how many zombies hunt each player
    private Random _random = new();
    private float _detectTimer;
    private int _repathsThisTick; // path-query budget spent this tick (MaxRepathsPerTick)

    public IReadOnlyList<ZombieInstance> Zombies => _zombies;

    // Region queries for the host's per-region replication (LevelNavigation.tryGetBounds and the
    // region's own zombie list).
    public byte BoundOf(Vector3 position) => LevelNavigationData.TryGetBound(_bounds, position);
    public int BoundCount => _byBound.Length;
    public IReadOnlyList<ZombieInstance> ZombiesInBound(byte bound) => _byBound[bound];

    // AlertTool's line-of-sight test, wired to real world geometry by the host (optional).
    public VisionBlocked? VisionBlocked;

    // The CharacterController's world collision, wired to real colliders by the host (optional).
    public ZombieMoveResolver? MoveResolver;

    // The Seeker's navmesh pathfinding, wired to the NavigationServer by the host (optional).
    public ZombiePathQuery? PathQuery;

    // Real ground (object floors, sidewalks) via physics on the host; heightfield otherwise.
    public ZombieGroundSnap? GroundSnap;

    // Fires when a zombie's swing lands (attackTime/2 after it starts): (zombie, player id, damage).
    public Action<ZombieInstance, byte, byte>? OnAttack;

    public IReadOnlyList<NavFlag>? Navmesh => _navmesh; // for the host's NavigationServer regions

    public ZombieSystem(
        IReadOnlyList<ZombieTable> tables,
        IReadOnlyList<NavBound> bounds,
        GroundSampler ground,
        IReadOnlyList<NavFlag>? navmesh = null)
    {
        _tables = tables;
        _bounds = bounds;
        _ground = ground;
        _navmesh = navmesh;
        _byBound = new List<ZombieInstance>[bounds.Count];
        for (int i = 0; i < _byBound.Length; i++)
            _byBound[i] = new List<ZombieInstance>();
    }

    // ZombieManager.generateZombies: per nav bound, cap = min(flag maxZombies, ceil(eligible spawn
    // count x Spawn_Chance)), then draw spawnpoints at random without replacement. Positions arrive
    // in Unity coordinates straight from Animals.dat and are mirrored (z -> -z) here.
    public void Spawn(IReadOnlyList<ZombieSpawnpointData> spawnpoints, Random random)
    {
        _random = random;
        var perBound = new List<ZombieSpawnpointData>[_bounds.Count];
        for (int i = 0; i < perBound.Length; i++)
            perBound[i] = new List<ZombieSpawnpointData>();
        foreach (ZombieSpawnpointData spawn in spawnpoints)
        {
            Vector3 godotPoint = new(spawn.Point.X, spawn.Point.Y, -spawn.Point.Z);
            byte bound = LevelNavigationData.TryGetBound(_bounds, godotPoint);
            // LevelZombies.load keeps a spawnpoint only when tryGetBounds AND checkNavigation
            // pass — inside the expanded territory and the non-expanded navmesh box.
            if (bound != LevelNavigationData.NoBound && CheckNavigation(godotPoint))
                perBound[bound].Add(new ZombieSpawnpointData(spawn.Type, godotPoint));
        }

        ushort nextId = 0;
        for (int b = 0; b < perBound.Length; b++)
        {
            if (!_bounds[b].SpawnZombies)
                continue;
            List<ZombieSpawnpointData> pool = perBound[b];
            int max = Math.Min(_bounds[b].MaxZombies, (int)MathF.Ceiling(pool.Count * SpawnChance));
            for (int n = 0; n < max && pool.Count > 0; n++)
            {
                int pick = random.Next(pool.Count);
                ZombieSpawnpointData spawn = pool[pick];
                pool.RemoveAt(pick);
                ZombieInstance zombie = Create(nextId++, (byte)b, spawn, random);
                _zombies.Add(zombie);
                _byBound[b].Add(zombie);
            }
        }
    }

    private ZombieInstance Create(ushort id, byte bound, ZombieSpawnpointData spawn, Random random)
    {
        ZombieTable table = _tables[spawn.Type];
        EZombieSpeciality speciality = EZombieSpeciality.Normal;
        if (table.IsMega)
            speciality = EZombieSpeciality.Mega;
        else if (random.NextSingle() < CrawlerChance)
            speciality = EZombieSpeciality.Crawler;
        else if (random.NextSingle() < SprinterChance)
            speciality = EZombieSpeciality.Sprinter;

        Vector3 position = spawn.Point;
        if (_ground(position.X, position.Z, out float y))
            position.Y = y;

        return new ZombieInstance
        {
            Id = id,
            Bound = bound,
            Type = spawn.Type,
            Speciality = speciality,
            IsHyper = _bounds[bound].HyperAgro,
            Shirt = RollSlot(table, 0, random),
            Pants = RollSlot(table, 1, random),
            Hat = RollSlot(table, 2, random),
            Gear = RollSlot(table, 3, random),
            Move = (byte)random.Next(4), // ZombieManager's Random.Range(0, 4)
            Idle = (byte)random.Next(3), // ZombieManager's Random.Range(0, 3)
            Home = position,
            Position = position,
            Yaw = random.NextSingle() * 360f,
        };
    }

    // ZombieManager's clothing roll: pass the slot's chance to wear a random entry, else bare (255).
    private static byte RollSlot(ZombieTable table, int slot, Random random)
    {
        if (slot >= table.Slots.Count)
            return byte.MaxValue;
        (float chance, List<ushort> items) = table.Slots[slot];
        if (items.Count == 0 || random.NextSingle() >= chance)
            return byte.MaxValue;
        return (byte)random.Next(items.Count);
    }

    public void Tick(IReadOnlyList<ZombiePlayerView> players, float dt)
    {
        _repathsThisTick = 0;
        _detectTimer += dt;
        if (_detectTimer >= ZombieDetection.DetectInterval)
        {
            _detectTimer = 0f;
            Detect(players);
        }

        foreach (ZombieInstance zombie in _zombies)
            Behave(zombie, players, dt);
    }

    // PlayerStance's 0.1 s stealth alert per player, against the zombies of the player's nav region.
    // (Indexed loops throughout: foreach over the IReadOnlyList interface boxes an enumerator per call,
    // which in the per-zombie hunt path was the tick's only steady allocation.)
    private void Detect(IReadOnlyList<ZombiePlayerView> players)
    {
        for (int p = 0; p < players.Count; p++)
        {
            ZombiePlayerView player = players[p];
            byte bound = PlayerNav(player.Position);
            if (bound == LevelNavigationData.NoBound)
                continue; // player.movement.nav == 255: no region hears the alert

            float radius = ZombieDetection.RadiusFor(player.Stance, player.Moving);
            float sqrRadius = radius * radius;
            bool sneak = player.Stance != EPlayerStance.Sprint;

            foreach (ZombieInstance zombie in _byBound[bound])
            {
                if (zombie.TargetPlayer == player.Id)
                    continue; // Zombie.checkAlert: already hunting this player
                Vector3 playerToZombie = zombie.Position - player.Position;
                float yawRad = Mathf.DegToRad(zombie.Yaw);
                Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
                if (!ZombieDetection.IsDetected(forward, playerToZombie, sqrRadius, sneak))
                    continue;
                // AlertTool's line-of-sight raycast: from the zombie's eyes toward the alert, 95%
                // of the distance so the ray doesn't clip the player's own collider.
                if (VisionBlocked != null
                    && VisionBlocked(zombie.Position + Vector3.Up, player.Position))
                    continue;
                Alert(zombie, player, players);
            }
        }
    }

    // Zombie.alert(Player): grab the target if idle; a different player only steals a live target
    // by being closer. The approach path spreads the horde: every third alerted zombie rushes, the
    // others drift to a side (megas always rush).
    private void Alert(ZombieInstance zombie, in ZombiePlayerView player, IReadOnlyList<ZombiePlayerView> players)
    {
        if (zombie.TargetPlayer != byte.MaxValue
            && TryGetPlayer(players, zombie.TargetPlayer, out ZombiePlayerView current))
        {
            float currentSqr = (current.Position - zombie.Position).LengthSquared();
            float newSqr = (player.Position - zombie.Position).LengthSquared();
            if (newSqr >= currentSqr)
                return;
            AdjustAgro(zombie.TargetPlayer, -1);
        }

        zombie.TargetPlayer = player.Id;
        zombie.LeaveDelay = 0f; // isLeaving = false
        zombie.Path = RollPath(zombie, player.Id);
        zombie.RepathTimer = 0f; // fresh target: path on the next move
        zombie.PathPoints.Clear();
        zombie.CurrentWaypointIndex = 0;
        zombie.TargetReached = false;
        AdjustAgro(player.Id, +1);
        if (zombie.State is EZombieState.Idle or EZombieState.Return)
            zombie.State = EZombieState.Chase;
    }

    private EZombiePath RollPath(ZombieInstance zombie, byte playerId)
    {
        if (zombie.Speciality == EZombieSpeciality.Mega)
            return EZombiePath.Rush;
        _agro.TryGetValue(playerId, out int agro);
        if (agro % 3 == 0)
            return EZombiePath.Rush;
        return _random.NextSingle() < 0.5f ? EZombiePath.Left : EZombiePath.Right;
    }

    private void AdjustAgro(byte playerId, int delta)
    {
        _agro.TryGetValue(playerId, out int agro);
        _agro[playerId] = Math.Max(0, agro + delta);
    }

    private void Behave(ZombieInstance zombie, IReadOnlyList<ZombiePlayerView> players, float dt)
    {
        zombie.SinceSwing += dt;
        if (zombie.PendingHit >= 0f)
        {
            zombie.PendingHit -= dt;
            if (zombie.PendingHit < 0f && zombie.TargetPlayer != byte.MaxValue)
                LandHit(zombie);
        }

        switch (zombie.State)
        {
            case EZombieState.Chase:
            case EZombieState.Attack:
                Hunt(zombie, players, dt);
                break;

            case EZombieState.Return:
                Move(zombie, zombie.LeaveTo, canTurn: true, default, dt);
                if (HorizontalDistanceSquared(zombie.Position, zombie.LeaveTo) <= ArriveDistanceSquared)
                    zombie.State = EZombieState.Idle; // stop() — the zombie settles where it is
                break;

            case EZombieState.Idle when zombie.LeaveDelay > 0f:
                // Zombie.leave: stand still for leaveTime, then walk to the retreat point.
                zombie.LeaveDelay -= dt;
                if (zombie.LeaveDelay <= 0f)
                {
                    zombie.LeaveDelay = 0f;
                    zombie.State = EZombieState.Return;
                }
                break;
        }
    }

    private void Hunt(ZombieInstance zombie, IReadOnlyList<ZombiePlayerView> players, float dt)
    {
        if (!TryGetPlayer(players, zombie.TargetPlayer, out ZombiePlayerView target))
        {
            Leave(zombie, Vector3.Zero, quick: false); // player.life.isDead -> leave(false)
            return;
        }
        if (PlayerNav(target.Position) == LevelNavigationData.NoBound)
        {
            Leave(zombie, target.Position, quick: true); // player.movement.nav == 255 -> leave(true)
            return;
        }
        float sqrHorizontal = HorizontalDistanceSquared(zombie.Position, target.Position);
        if (sqrHorizontal > MaxChaseDistanceSquared)
        {
            Leave(zombie, target.Position, quick: false); // beyond 64 m -> leave(false)
            return;
        }

        float vertical = MathF.Abs(target.Position.Y - zombie.Position.Y);
        if (sqrHorizontal < zombie.AttackRangeSquared && vertical < zombie.VerticalAttackRange)
        {
            zombie.State = EZombieState.Attack;
            if (zombie.SinceSwing > SwingInterval && zombie.PendingHit < 0f)
            {
                // A swing starts (the replicated Attack anim); the hit lands attackTime/2 later.
                zombie.SinceSwing = 0f;
                zombie.PendingHit = AttackTime / 2f;
            }
        }
        else
        {
            zombie.State = EZombieState.Chase;
        }

        // Zombie.tick's target/steering shaping, verbatim: farther than 2 m the pathfinding
        // DESTINATION is offset by the approach path (LEFT/RIGHT one metre to the zombie's own
        // side, RUSH one metre short along its own forward) with turning driven by the route;
        // inside 2 m the destination is raw and the body turns straight at the player while the
        // route still drives the legs. Movement itself never pauses for attacks — the follower's
        // endReachedDistance (0.75) is what brings the zombie to a stop at the target.
        Vector3 destination = target.Position;
        bool canTurn = true;
        Vector3 directDirection = default;
        if (sqrHorizontal > 4f)
        {
            float yawRad = Mathf.DegToRad(zombie.Yaw);
            Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            var right = new Vector3(forward.Z, 0f, -forward.X);
            destination += zombie.Path switch
            {
                EZombiePath.Left => -right,
                EZombiePath.Right => right,
                _ => -forward, // RUSH
            };
        }
        else
        {
            canTurn = false;
            directDirection = target.Position - zombie.Position;
        }
        Move(zombie, destination, canTurn, directDirection, dt);
    }

    // LegacyAIPathNoRedist.move(), ported: repath on the cadence (a FAILED query keeps the current
    // route, exactly like OnPathComplete ignoring an errored ABPath), then follow the route with
    // CalculateVelocity + RotateTowards and step through the physics. With no route at all the
    // zombie stands (path == null in the original); the straight-line seek only exists for maps
    // without a navmesh, where the original also falls back to its NonPathfinding component.
    private void Move(ZombieInstance zombie, Vector3 destination, bool canTurn,
        Vector3 directDirection, float dt)
    {
        if (PathQuery == null)
        {
            MoveTowards(zombie, destination, dt); // NonPathfindingZombieMovementComponent fallback
            return;
        }

        zombie.RepathTimer -= dt;
        if (zombie.RepathTimer <= 0f && _repathsThisTick < MaxRepathsPerTick)
        {
            _repathsThisTick++;
            zombie.RepathTimer = RepathRate;
            // Stabilize the query's destination: project it onto the navmesh XZ-first (its own
            // floor), so an off-mesh target can never snap the route into a basement below it.
            Vector3 queryTo = destination;
            if (_navmesh != null && LevelNavmesh.SnapXZ(_navmesh, destination, out Vector3 snapped))
                queryTo = snapped;
            _scratchPath.Clear();
            if (PathQuery(zombie.Position, queryTo, _scratchPath)
                && HorizontalDistanceSquared(_scratchPath[^1], queryTo) <= 4f)
            {
                // Success: replace the route (OnPathComplete). A route that stops short of the
                // destination is Godot's closest-reachable result — the official ABPath ERRORS on
                // unreachable targets instead, so it is treated as a failure and discarded.
                zombie.PathPoints.Clear();
                zombie.PathPoints.AddRange(_scratchPath);
                zombie.CurrentWaypointIndex = 0;
                zombie.TargetReached = false;
            }
        }

        if (zombie.PathPoints.Count == 0)
            return; // no route has ever succeeded: stand, like the original with path == null

        Vector3 velocity = CalculateVelocity(zombie, destination, canTurn, dt);
        if (!canTurn)
            zombie.SteerDirection = directDirection;
        RotateTowards(zombie, zombie.SteerDirection, dt);
        ApplyStep(zombie, velocity * dt);
    }

    private readonly List<Vector3> _scratchPath = new();

    // LegacyAIPathNoRedist.CalculateVelocity, ported: advance waypoints within pickNextWaypointDist
    // (XZ), aim at the forwardLook point interpolated ON THE CURRENT SEGMENT, stop within
    // endReachedDistance of the route's end, and return a velocity along the body's FORWARD scaled
    // by how aligned the body is with the desired direction (a zombie turns INTO its route instead
    // of strafing sideways onto it) and by the slowdown near the target point.
    private static Vector3 CalculateVelocity(ZombieInstance zombie, Vector3 destination,
        bool canTurn, float dt)
    {
        List<Vector3> vp = zombie.PathPoints;
        // (The original also clamps index >= count here, guarding its ASYNC path callbacks; our
        // repath is synchronous and always resets the index, so that state cannot occur.)
        int index = zombie.CurrentWaypointIndex;
        if (index <= 1)
            index = 1;

        Vector3 segmentStart;
        if (vp.Count == 1)
        {
            segmentStart = zombie.Position; // the original inserts currentPosition at [0]
            index = 0;
        }
        else
        {
            while (index < vp.Count - 1
                && HorizontalDistanceSquared(vp[index], zombie.Position)
                    < PickNextWaypointDist * PickNextWaypointDist)
                index++;
            segmentStart = vp[index - 1];
        }
        zombie.CurrentWaypointIndex = index;

        Vector3 targetPoint = CalculateTargetPoint(zombie.Position, segmentStart,
            vp[vp.Count == 1 ? 0 : index], index == vp.Count - 1, destination);
        Vector3 direction = targetPoint - zombie.Position;
        direction.Y = 0f;
        float magnitude = direction.Length();
        float slowdown = Mathf.Clamp(magnitude / SlowdownDistance, 0f, 1f);
        if (canTurn)
            zombie.SteerDirection = direction;

        if (index == vp.Count - 1 && magnitude <= EndReachedDistance)
        {
            zombie.TargetReached = true;
            return Vector3.Zero;
        }

        // magnitude here is always > endReachedDistance (the stop above returned otherwise), so
        // the normalization is safe without the original's tiny-magnitude guard.
        float yawRad = Mathf.DegToRad(zombie.Yaw);
        Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
        float alignment = (direction.X / magnitude * forward.X) + (direction.Z / magnitude * forward.Z);
        float speed = zombie.Speed * MathF.Max(alignment, MinMoveScale) * slowdown;
        if (dt > 0f)
            speed = Mathf.Clamp(speed, 0f, magnitude / (dt * 2f)); // never overshoot the point
        return forward * speed;
    }

    // LegacyAIPathNoRedist.CalculateTargetPoint, ported: interpolate the aim point forwardLook
    // ahead ON the current segment only; on the final segment, if its end sits within 4 m of the
    // real destination, head straight for the destination itself.
    private static Vector3 CalculateTargetPoint(Vector3 p, Vector3 a, Vector3 b,
        bool canGoDirectly, Vector3 destination)
    {
        if (canGoDirectly && HorizontalDistanceSquared(b, destination) < 16f)
            return destination;
        a.Y = p.Y;
        b.Y = p.Y;
        // A degenerate active segment cannot occur: consecutive duplicate waypoints are skipped
        // together by the advance loop, and the final segment short-circuits to the destination.
        float magnitude = (a - b).Length();
        float dx = b.X - a.X, dz = b.Z - a.Z;
        float factor = Mathf.Clamp(
            (((p.X - a.X) * dx) + ((p.Z - a.Z) * dz)) / (magnitude * magnitude), 0f, 1f);
        float distanceToLine = ((b - a) * factor + a - p).Length();
        float lookAheadFactor = Mathf.Clamp(ForwardLook - distanceToLine, 0f, ForwardLook) / magnitude;
        lookAheadFactor = Mathf.Clamp(lookAheadFactor + factor, 0f, 1f);
        return (b - a) * lookAheadFactor + a;
    }

    // LegacyAIPathNoRedist.RotateTowards: Quaternion.Slerp damping around Y at turningSpeed = 5.
    private static void RotateTowards(ZombieInstance zombie, Vector3 direction, float dt)
    {
        if (direction.X * direction.X + direction.Z * direction.Z < 1e-10f)
            return;
        float targetYaw = Mathf.RadToDeg(MathF.Atan2(-direction.X, -direction.Z));
        float delta = Mathf.Wrap(targetYaw - zombie.Yaw, -180f, 180f);
        zombie.Yaw = Mathf.Wrap(zombie.Yaw + (delta * MathF.Min(1f, TurningSpeed * dt)), 0f, 360f);
    }

    private void LandHit(ZombieInstance zombie)
    {
        bool hyper = _bounds[zombie.Bound].HyperAgro;
        float damage = _tables[zombie.Type].Damage * (hyper ? 1.5f : 1f);
        damage *= zombie.Speciality switch
        {
            EZombieSpeciality.Crawler => 2f,
            EZombieSpeciality.Sprinter => 0.75f,
            _ => 1f,
        };
        OnAttack?.Invoke(zombie, zombie.TargetPlayer, (byte)damage);
    }

    // Zombie.leave: drop the target, retreat 16 m away from it (with +-8 m of scatter), stand for
    // leaveTime (0.5-1 s after a quick escape, 3-6 s otherwise), then walk to the retreat point and
    // settle there. Retreat points that fall outside the zombie's territory flip toward the target
    // instead, and as a last resort the zombie just stays put.
    private void Leave(ZombieInstance zombie, Vector3 targetPosition, bool quick)
    {
        AdjustAgro(zombie.TargetPlayer, -1); // Leave only ever runs while hunting a target
        zombie.TargetPlayer = byte.MaxValue;
        zombie.PendingHit = -1f;

        Vector3 away = targetPosition == Vector3.Zero
            ? Vector3.Zero
            : (targetPosition - zombie.Position).Normalized() * 16f;
        Vector3 retreat = zombie.Position - away + Scatter();
        if (!CheckNavigation(retreat))
            retreat = zombie.Position + away + Scatter();
        if (!CheckNavigation(retreat))
            retreat = zombie.Position;

        zombie.RepathTimer = 0f; // the retreat is a new destination
        zombie.PathPoints.Clear();
        zombie.CurrentWaypointIndex = 0;
        zombie.TargetReached = false;
        zombie.LeaveTo = retreat;
        zombie.LeaveDelay = quick
            ? 0.5f + (_random.NextSingle() * 0.5f)
            : 3f + (_random.NextSingle() * 3f);
        zombie.State = EZombieState.Idle;
    }

    // LevelNavigation.checkNavigation: the non-expanded navmesh boxes when the map ships them,
    // otherwise fall back to the expanded territory bounds (maps without navigation data).
    private bool CheckNavigation(Vector3 point)
    {
        if (_navmesh != null)
            return LevelNavmesh.CheckNavigation(_navmesh, point);
        return LevelNavigationData.TryGetBound(_bounds, point) != LevelNavigationData.NoBound;
    }

    private Vector3 Scatter() =>
        new((_random.NextSingle() * 16f) - 8f, 0f, (_random.NextSingle() * 16f) - 8f);

    // PlayerMovement.nav, exactly: tryGetNavigation tests the NON-expanded navmesh boxes (the
    // expanded Bounds.dat territory is tryGetBounds, a different field). Detection gating and the
    // hunt's nav == 255 rule both key off this; maps without navmesh data fall back to bounds.
    private byte PlayerNav(Vector3 position)
    {
        if (_navmesh == null)
            return LevelNavigationData.TryGetBound(_bounds, position);
        for (int i = 0; i < _navmesh.Count; i++)
            if (_navmesh[i].ContainsXZ(position))
                return (byte)i;
        return LevelNavigationData.NoBound;
    }

    // NonPathfindingZombieMovementComponent.Move: straight-line seek with its own 720°/s turning —
    // the original's fallback when no pathfinding exists, kept for maps without navmesh data.
    private void MoveTowards(ZombieInstance zombie, Vector3 targetPosition, float dt)
    {
        Face(zombie, targetPosition, dt);
        Vector3 flat = new(targetPosition.X - zombie.Position.X, 0f, targetPosition.Z - zombie.Position.Z);
        float distance = flat.Length();
        if (distance < 1e-4f)
            return;
        float step = MathF.Min(zombie.Speed * dt, distance);
        ApplyStep(zombie, flat / distance * step);
    }

    // The physics leg of a movement step: world collision (the host's capsule collide-and-slide),
    // then the other zombies' capsules (CharacterController semantics: the mover is pushed out,
    // the blocker never budges), re-resolved against the world so separation can't shove anyone
    // through a wall, and finally the ground snap.
    private void ApplyStep(ZombieInstance zombie, Vector3 step)
    {
        if ((step.X * step.X) + (step.Z * step.Z) < 1e-10f)
            return;
        Vector3 next = zombie.Position + step;

        if (MoveResolver != null)
        {
            next = MoveResolver(zombie.Position, next, zombie.Radius);

            // A Unity CharacterController resolves collisions ITERATIVELY per move, so a dead-on
            // block destabilizes sideways within a frame or two; our single-sweep resolve is
            // deterministic and can deadlock nose-first forever (a plateau corner the funnel cut
            // over, a prop edge). Recreate that escape: when the step made under a quarter of its
            // length, sweep both tangents and take the side that physically opens — preferring,
            // when both do, the one ending closer ALONG the blocked direction, with the previous
            // side as the near-tie hysteresis so curved obstacles are rounded, never orbited.
            float stepLen = MathF.Sqrt((step.X * step.X) + (step.Z * step.Z));
            float progress = HorizontalDistanceSquared(next, zombie.Position);
            if (progress < stepLen * stepLen * 0.0625f)
            {
                var tangent = new Vector3(-step.Z / stepLen, 0f, step.X / stepLen) * stepLen;
                Vector3 ahead = zombie.Position + (step / stepLen * 4f); // where it wants to go
                Vector3 plus = MoveResolver(zombie.Position, zombie.Position + tangent, zombie.Radius);
                Vector3 minus = MoveResolver(zombie.Position, zombie.Position - tangent, zombie.Radius);
                float blocked = stepLen * stepLen * 0.0625f;
                bool plusMoves = HorizontalDistanceSquared(plus, zombie.Position) > blocked;
                bool minusMoves = HorizontalDistanceSquared(minus, zombie.Position) > blocked;
                sbyte side;
                if (plusMoves && minusMoves)
                {
                    float gain = HorizontalDistanceSquared(minus, ahead)
                        - HorizontalDistanceSquared(plus, ahead);
                    side = MathF.Abs(gain) < 0.01f
                        ? (zombie.DetourSide != 0 ? zombie.DetourSide : (sbyte)1)
                        : (gain > 0f ? (sbyte)1 : (sbyte)-1);
                }
                else if (plusMoves || minusMoves)
                {
                    side = plusMoves ? (sbyte)1 : (sbyte)-1;
                }
                else
                {
                    side = zombie.DetourSide != 0 ? zombie.DetourSide : (sbyte)1;
                }
                zombie.DetourSide = side;
                Vector3 detour = side > 0 ? plus : minus;
                if (HorizontalDistanceSquared(detour, zombie.Position) > progress)
                    next = detour;
            }
        }

        bool separated = false;
        foreach (ZombieInstance other in _byBound[zombie.Bound])
        {
            if (other == zombie)
                continue;
            float minDist = zombie.Radius + other.Radius;
            float dx = next.X - other.Position.X;
            float dz = next.Z - other.Position.Z;
            float sqr = (dx * dx) + (dz * dz);
            if (sqr >= minDist * minDist || sqr < 1e-8f)
                continue;
            float dist = MathF.Sqrt(sqr);
            next.X = other.Position.X + (dx / dist * minDist);
            next.Z = other.Position.Z + (dz / dist * minDist);
            separated = true;
        }
        if (separated && MoveResolver != null)
            next = MoveResolver(zombie.Position, next, zombie.Radius);

        // Ground: the real surface underfoot (sidewalks, house floors, stairs) when the host wires
        // physics in; the bare terrain heightfield otherwise.
        if (GroundSnap != null)
        {
            if (GroundSnap(next, out float gy))
                next.Y = gy;
        }
        else if (_ground(next.X, next.Z, out float y))
        {
            next.Y = y;
        }
        zombie.Position = next;
    }

    private static void Face(ZombieInstance zombie, Vector3 targetPosition, float dt)
    {
        float dx = targetPosition.X - zombie.Position.X;
        float dz = targetPosition.Z - zombie.Position.Z;
        if ((dx * dx) + (dz * dz) <= 1e-8f)
            return;
        float desired = Mathf.RadToDeg(MathF.Atan2(-dx, -dz));
        float delta = Mathf.Wrap(desired - zombie.Yaw, -180f, 180f);
        float turn = TurnRateDegreesPerSecond * dt;
        zombie.Yaw = Mathf.Wrap(zombie.Yaw + Mathf.Clamp(delta, -turn, turn), 0f, 360f);
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    private static bool TryGetPlayer(IReadOnlyList<ZombiePlayerView> players, byte id, out ZombiePlayerView player)
    {
        for (int i = 0; i < players.Count; i++)
        {
            if (players[i].Id == id)
            {
                player = players[i];
                return true;
            }
        }
        player = default;
        return false;
    }
}
