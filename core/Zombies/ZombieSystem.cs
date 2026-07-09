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
    public sbyte DetourSide;          // while blocked head-on, hug the obstacle on this side (0 = none)
    public readonly List<Vector3> PathPoints = new(); // the Seeker's current route over the navmesh
    public int PathSegment;           // monotonic progress along the route (carrot following)
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

    // Zombie.GetHorizontalAttackRangeSquared for a player target on a dedicated server:
    // ATTACK_PLAYER(2) x 0.5 for NORMAL x 2 for megas.
    public float AttackRange => 2f
        * (Speciality == EZombieSpeciality.Normal ? 0.5f : 1f)
        * (Speciality == EZombieSpeciality.Mega ? 2f : 1f);

    public float VerticalAttackRange => 2.1f * (Speciality == EZombieSpeciality.Mega ? 1.5f : 1f);
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

    public const float RepathRate = 0.5f;               // LegacyAIPath.repathRate
    public const float PickNextWaypointDist = 1f;       // LegacyAIPathNoRedist.pickNextWaypointDist
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
            byte bound = LevelNavigationData.TryGetBound(_bounds, player.Position);
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
        zombie.PathSegment = 0;
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
                Seek(zombie, zombie.LeaveTo, Vector3.Zero, dt);
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
        if (LevelNavigationData.TryGetBound(_bounds, target.Position) == LevelNavigationData.NoBound)
        {
            Leave(zombie, target.Position, quick: true); // player.movement.nav == 255 -> leave(true)
            return;
        }
        if (HorizontalDistanceSquared(zombie.Position, target.Position) > MaxChaseDistanceSquared)
        {
            Leave(zombie, target.Position, quick: false); // beyond 64 m -> leave(false)
            return;
        }

        float sqrHorizontal = HorizontalDistanceSquared(zombie.Position, target.Position);
        float vertical = MathF.Abs(target.Position.Y - zombie.Position.Y);
        if (sqrHorizontal < zombie.AttackRange * zombie.AttackRange && vertical < zombie.VerticalAttackRange)
        {
            zombie.State = EZombieState.Attack;
            Face(zombie, target.Position, dt);
            if (zombie.SinceSwing > SwingInterval && zombie.PendingHit < 0f)
            {
                // A swing starts (the replicated Attack anim); the hit lands attackTime/2 later.
                zombie.SinceSwing = 0f;
                zombie.PendingHit = AttackTime / 2f;
            }
            return;
        }

        zombie.State = EZombieState.Chase;
        Vector3 steerOffset = Vector3.Zero;
        if (zombie.Path != EZombiePath.Rush && sqrHorizontal > 4f)
        {
            // Zombie.cs LEFT/RIGHT paths: drift one metre beside the approach line until within
            // 2 m. The offset shifts the STEERING target only — offsetting the pathfinding
            // destination made its navmesh snap flip between the two sides of a wall on every
            // repath, so zombies entering houses oscillated back out.
            float yawRad = Mathf.DegToRad(zombie.Yaw);
            Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
            var right = new Vector3(forward.Z, 0f, -forward.X);
            steerOffset = zombie.Path == EZombiePath.Left ? -right : right;
        }
        Seek(zombie, target.Position, steerOffset, dt);
    }

    // The Seeker + FunnelModifier + LegacyAIPath port: recalculate the navmesh route to the RAW
    // destination every repathRate seconds and follow it carrot-on-a-stick — project the current
    // position onto the route (monotonically, so loops never read backwards) and steer at the
    // point one pickNextWaypointDist ahead ALONG the polyline. This holds the corridor through
    // doorways instead of cutting corners into the frames. A PARTIAL route (unreachable target)
    // holds at its end like the original's end-of-path; no route at all decays into the
    // straight-line NonPathfinding seek, safety-netted by MoveResolver as before.
    private void Seek(ZombieInstance zombie, Vector3 destination, Vector3 steerOffset, float dt)
    {
        if (PathQuery == null)
        {
            MoveTowards(zombie, destination + steerOffset, dt);
            return;
        }

        zombie.RepathTimer -= dt;
        if (zombie.RepathTimer <= 0f && _repathsThisTick < MaxRepathsPerTick)
        {
            _repathsThisTick++;
            zombie.RepathTimer = RepathRate;
            zombie.PathPoints.Clear();
            zombie.PathSegment = 0;
            if (!PathQuery(zombie.Position, destination, zombie.PathPoints))
                zombie.PathPoints.Clear();
        }

        Vector3 target;
        bool onRoute = zombie.PathPoints.Count >= 2;
        if (onRoute)
        {
            target = Carrot(zombie);
        }
        else if (zombie.PathPoints.Count == 1)
        {
            target = zombie.PathPoints[0];
        }
        else
        {
            target = destination;
        }

        // The side drift never displaces the route's END (a partial route's end is a wall or a
        // doorway the zombie must actually reach).
        bool atRouteEnd = onRoute && zombie.PathSegment >= zombie.PathPoints.Count - 2
            && HorizontalDistanceSquared(target, zombie.PathPoints[^1]) < 0.01f;
        MoveTowards(zombie, atRouteEnd || !onRoute ? target : target + steerOffset, dt);
    }

    // Projects the zombie onto its route near its current segment (monotonic: it never jumps to a
    // later fold of a route that loops back), then walks one lookahead ahead along the polyline.
    private static Vector3 Carrot(ZombieInstance zombie)
    {
        List<Vector3> pts = zombie.PathPoints;

        int bestSegment = zombie.PathSegment;
        float bestT = 0f;
        float bestDistance = float.MaxValue;
        int lastSegment = Math.Min(pts.Count - 2, zombie.PathSegment + 5);
        for (int i = zombie.PathSegment; i <= lastSegment; i++)
        {
            float ax = pts[i].X, az = pts[i].Z;
            float bx = pts[i + 1].X, bz = pts[i + 1].Z;
            float dx = bx - ax, dz = bz - az;
            float lengthSquared = (dx * dx) + (dz * dz);
            float t = lengthSquared < 1e-8f
                ? 0f
                : Mathf.Clamp((((zombie.Position.X - ax) * dx) + ((zombie.Position.Z - az) * dz))
                    / lengthSquared, 0f, 1f);
            float px = ax + (dx * t), pz = az + (dz * t);
            float ddx = zombie.Position.X - px, ddz = zombie.Position.Z - pz;
            float distance = (ddx * ddx) + (ddz * ddz);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestSegment = i;
                bestT = t;
            }
        }
        zombie.PathSegment = bestSegment;

        // Walk pickNextWaypointDist ahead along the polyline from the projection.
        Vector3 point = pts[bestSegment].Lerp(pts[bestSegment + 1], bestT);
        float remaining = PickNextWaypointDist;
        int segment = bestSegment;
        while (segment < pts.Count - 1)
        {
            Vector3 segmentEnd = pts[segment + 1];
            float segmentLeft = new Vector2(segmentEnd.X - point.X, segmentEnd.Z - point.Z).Length();
            if (segmentLeft > remaining)
            {
                float f = remaining / segmentLeft;
                return new Vector3(
                    point.X + ((segmentEnd.X - point.X) * f),
                    segmentEnd.Y,
                    point.Z + ((segmentEnd.Z - point.Z) * f));
            }
            remaining -= segmentLeft;
            point = segmentEnd;
            segment++;
        }
        return pts[^1];
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
        zombie.PathSegment = 0;
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

    // NonPathfindingZombieMovementComponent.Move: seek straight at the target, turning the body at
    // 720°/s, then resolve the step against the other zombies' capsules the way a Unity
    // CharacterController does — the mover is pushed out, the blocker never budges — so a horde
    // funnels into a queue instead of a single stacked point.
    private void MoveTowards(ZombieInstance zombie, Vector3 targetPosition, float dt)
    {
        Face(zombie, targetPosition, dt);
        Vector3 flat = new(targetPosition.X - zombie.Position.X, 0f, targetPosition.Z - zombie.Position.Z);
        float distance = flat.Length();
        if (distance < 1e-4f)
            return;
        float step = MathF.Min(zombie.Speed * dt, distance);
        Vector3 direction = flat / distance;
        Vector3 next = zombie.Position + (direction * step);

        // World geometry first (trees, walls, props — the CharacterController slide)...
        if (MoveResolver != null)
        {
            Vector3 resolved = MoveResolver(zombie.Position, next, zombie.Radius);
            float progress = HorizontalDistanceSquared(resolved, zombie.Position);
            if (progress < step * step * 0.0625f) // under 25% of the step: blocked head-on
            {
                // The real game's navmesh routes around obstacles; without one, hug the obstacle:
                // walk the tangent on a sticky side (like a CharacterController skirting a trunk).
                // The side is rolled once and kept for good — re-rolling whenever the direct line
                // briefly opened made zombies orbit back and forth on a curved trunk.
                if (zombie.DetourSide == 0)
                    zombie.DetourSide = _random.NextSingle() < 0.5f ? (sbyte)-1 : (sbyte)1;
                var tangent = new Vector3(-direction.Z * zombie.DetourSide, 0f, direction.X * zombie.DetourSide);
                Vector3 detour = MoveResolver(zombie.Position, zombie.Position + (tangent * step), zombie.Radius);
                if (HorizontalDistanceSquared(detour, zombie.Position) > progress)
                    resolved = detour;
            }
            next = resolved;
        }

        // ...then the other zombies' capsules.
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
        }

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
