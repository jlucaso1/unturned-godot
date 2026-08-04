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
    public bool RepathGranted;        // this tick's path-query token (granted round-robin)
    public readonly List<Vector3> PathPoints = new(); // the Seeker's current route over the navmesh
    public int CurrentWaypointIndex;  // LegacyAIPathNoRedist.currentWaypointIndex
    public bool TargetReached;        // LegacyAIPathNoRedist.targetReached
    public bool PathIsPartial;        // route ends at the closest reachable point, not the target island
    public Vector3 SteerDirection;    // LegacyAIPathNoRedist.targetDirection
    public float RepathTimer;         // counts down to the next path recalculation
    public float BlockedRouteTime;    // sustained physical failure while following the current route
    public bool RouteServedAnotherTarget; // route kept across a retarget: it may not veto its replacement

    // ZombieManager.getZombieSpeed with Slow_Movement=false (NORMAL difficulty).
    public float Speed => Speciality switch
    {
        EZombieSpeciality.Crawler => 3f,
        EZombieSpeciality.Sprinter => 6.5f,
        EZombieSpeciality.Mega => 6f,
        _ => 5.5f,
    };

    // The CharacterController capsule (SetCapsuleRadiusAndHeight): megas 0.75, everyone else 0.4. The
    // ordinary radius comes from BakedNavGraph so the routes and the body cannot drift apart — the funnel
    // insets wall portals by exactly this much.
    public float Radius => Speciality == EZombieSpeciality.Mega ? 0.75f : BakedNavGraph.AgentRadius;

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
// waypoints from start to destination and returns whether any path exists. Null means no navmesh
// (maps without nav data); a ready graph returning false is authoritative and never grants wall travel.
// `radius` is the body asking. Megas are nearly twice as wide as everyone else, and a route built for
// the narrow one walks their capsule into every jamb it passes, so the width cannot be a property of
// the graph — it has to travel with the request.
public delegate bool ZombiePathQuery(Vector3 from, Vector3 to, List<Vector3> path, float radius);

// Samples the REAL walking surface near a position — object floors, sidewalks, stairs — the way a
// CharacterController's grounding does, using the current height as the reference so stacked floors
// resolve to the right one. Null falls back to the terrain heightfield alone.
public delegate bool ZombieGroundSnap(Vector3 position, out float y);

// The server-side zombie brain: spawning per ZombieManager.generateZombies, aggro per AlertTool,
// hunting per Zombie.cs (approach paths, 64 m give-up, leave retreats, swing cadence). Movement is
// Unturned's NonPathfindingZombieMovementComponent — straight-line seek, 720°/s turning, and the
// CharacterController collision that makes crowds queue instead of stacking — so no navmesh is
// required, matching the game's own fallback movement model.
public sealed partial class ZombieSystem
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
    // Same shape here: 8 per 0.08 s tick (100/s) sustains the official 0.5 s repath cadence for ~50
    // concurrent hunters — a full region's worth. (Two per tick starved hordes: routes went stale
    // and packs visibly chased where the player USED to be.) Tokens are granted round-robin so no
    // fixed subset monopolizes the budget. Cost note: MapGetPath measures ~1.3 ms median against
    // PEI's map after warm-up, so a saturated budget is ~10 ms of an 80 ms tick — deliberate
    // headroom spent on route freshness.
    public const int MaxRepathsPerTick = 8;
    // A valid slide can deliver less than the requested forward component while still moving around an
    // obstacle. Only sustained near-zero delivery invalidates a route, so a stale path through a window
    // cannot outrank the pathfinder's later partial-but-executable route through the door forever.
    public const float BlockedRouteTimeout = 0.75f;
    public const float MinRouteProgressFraction = 0.2f;
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
    private bool _pathReadyThisTick;

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

    // False while an attached pathfinder is still publishing/reconciling its graph. This is distinct
    // from PathQuery returning false for a READY graph, which means the destination is unreachable.
    // While unavailable the original movement fallback keeps pursuing directly instead of freezing.
    public Func<bool>? PathReady;

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
        // The bug-repro recorder brackets the tick here (ZombieSystemState.cs): everything below is
        // what a dump has to be able to put back, and the state it must be put back to is this one.
        Observer?.BeginTick(this, players, dt);
        // Poll an engine-backed pathfinder once per authoritative tick, not once per hunter. During a
        // small map's async publication this probe can cross into NavigationServer; a horde must not turn
        // one readiness check into hundreds of identical engine calls.
        _pathReadyThisTick = PathQuery != null && (PathReady?.Invoke() ?? true);
        _detectTimer += dt;
        if (_detectTimer >= ZombieDetection.DetectInterval)
        {
            // Carry the overshoot rather than dropping it. The tick is 0.08 s and the interval 0.1 s, so
            // the timer never lands on the interval: it arrives at 0.16, and zeroing threw the extra 0.06
            // away every time, stretching a 0.1 s cadence into 0.16 s. Measured at 62 scans per 10 s
            // where the documented rate is 100 — zombies noticed a player at 62% of the rate the game
            // does, which is a third of a second of extra grace on every approach.
            _detectTimer -= ZombieDetection.DetectInterval;
            // Still at most one scan per tick: a dt longer than the interval must not bank credit for a
            // burst of scans later, so the leftover is capped at a single interval's worth.
            if (_detectTimer > ZombieDetection.DetectInterval)
                _detectTimer = ZombieDetection.DetectInterval;
            Detect(players);
        }

        // Grant this tick's path-query tokens round-robin among the hunters whose repath timer is
        // due, then run Behave in FIXED order. (Rotating Behave itself shared the budget but also
        // reordered movement and the order-dependent zombie-zombie collision — the whole simulation
        // changed with the rotation. Only the tokens rotate now.)
        int count = _zombies.Count;
        for (int i = 0; i < count; i++)
            _zombies[i].RepathGranted = false;
        if (count > 0 && _pathReadyThisTick)
        {
            _repathCursor %= count;
            int granted = 0;
            int nextCursor = _repathCursor;
            for (int i = 0; i < count && granted < MaxRepathsPerTick; i++)
            {
                ZombieInstance z = _zombies[(_repathCursor + i) % count];
                if (z.State is EZombieState.Chase or EZombieState.Return && z.RepathTimer - dt <= 0f)
                {
                    z.RepathGranted = true;
                    granted++;
                    nextCursor = (_repathCursor + i + 1) % count; // the queue resumes after the last grant
                }
            }
            if (granted > 0)
                _repathCursor = nextCursor;
        }

        for (int i = 0; i < count; i++)
        {
            CurrentZombie = _zombies[i];
            Behave(_zombies[i], players, dt);
        }
        CurrentZombie = null;
        Observer?.EndTick(this);
    }

    private int _repathCursor;

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
                CurrentZombie = zombie; // so a recorder can attribute this zombie's vision raycast
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
                    && VisionBlocked(zombie.Position + (Vector3.Up * ZombieBody.EyeHeight),
                        player.Position))
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
        if (zombie.TargetPlayer != byte.MaxValue)
        {
            // Only a LIVE target gets to defend its claim; a target that has left the roster cannot be
            // compared against and is simply given up.
            if (TryGetPlayer(players, zombie.TargetPlayer, out ZombiePlayerView current))
            {
                float currentSqr = (current.Position - zombie.Position).LengthSquared();
                float newSqr = (player.Position - zombie.Position).LengthSquared();
                if (newSqr >= currentSqr)
                    return;
            }

            // Released either way. Detect runs before the Behave loop, so in the tick a player
            // disconnects a zombie can be re-alerted here BEFORE Hunt would have noticed the target was
            // gone and called Leave — and Leave was the only path that gave the count back. Skipping it
            // left that player's tally permanently one too high, and ids are recycled, so the next
            // player handed that id inherited the skew.
            AdjustAgro(zombie.TargetPlayer, -1);
        }

        zombie.TargetPlayer = player.Id;
        zombie.LeaveDelay = 0f; // isLeaving = false
        zombie.Path = RollPath(zombie, player.Id);
        zombie.RepathTimer = 0f; // fresh target: path on the next move

        // The ROUTE deliberately survives the retarget. Changing a destination in the original only
        // schedules a Seeker query; vectorPath is written in OnPathComplete and nowhere else, so the
        // component keeps following the route it has while the replacement computes. Clearing it here
        // made Move() take its "no route has ever succeeded: stand" branch, and the replacement is not
        // instant — it waits for one of the MaxRepathsPerTick tokens. When a horde re-targets together
        // the queue is longer than the budget, so zombies stood still mid-chase for up to 0.8 s and then
        // sprinted off again; clients derive move/idle from the replicated motion, so that renders as
        // the zombie dropping aggro and immediately picking it back up.
        //
        // What must NOT survive is anything that judges the route, because both of those judgements
        // were made about a destination this zombie no longer has.
        //
        // The blocked-route evidence goes: it says "this body has not been delivering motion toward
        // where it was going", and where it was going just changed. Carried over near the timeout, one
        // blocked tick on the replacement would discard it before it had turned far enough to move,
        // which is the mid-chase freeze this whole change exists to remove.
        zombie.BlockedRouteTime = 0f;
        // And the route stops being the incumbent a replacement has to beat. Move scores a partial
        // replacement against the endpoint of the route in hand, measured to the CURRENT target — a
        // route built for someone else can happen to end nearer the new player than anything actually
        // reachable, and then it vetoes every replacement forever. If that stale endpoint also lands
        // within EndReachedDistance of the new target, the step goes to zero, so no evidence
        // accumulates and even the blocked-route timeout never fires. The route stays, and keeps the
        // body moving; it just does not get a vote on its own succession.
        zombie.RouteServedAnotherTarget = true;
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

    // How many zombies are currently hunting this player — the original's Player.agro. It shapes the
    // approach spread (RollPath rushes every third), so it is part of the simulation rather than a
    // statistic, and worth being able to read from outside.
    public int AgroOn(byte playerId) => _agro.TryGetValue(playerId, out int agro) ? agro : 0;

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

    // LegacyAIPathNoRedist.move(), ported: repath on the cadence (a FAILED query keeps a route that
    // physics is still delivering, like OnPathComplete ignoring an errored ABPath), then follow it with
    // CalculateVelocity + RotateTowards and step through the physics. With no route at all the
    // zombie stands (path == null in the original); the straight-line seek only exists for maps
    // without a navmesh, where the original also falls back to its NonPathfinding component.
    private void Move(ZombieInstance zombie, Vector3 destination, bool canTurn,
        Vector3 directDirection, float dt)
    {
        ZombiePathQuery? pathQuery = PathQuery;
        if (!_pathReadyThisTick || pathQuery == null)
        {
            MoveTowards(zombie, destination, dt); // graph absent/still building: collision-aware fallback
            return;
        }

        zombie.RepathTimer -= dt;
        if (zombie.RepathTimer <= 0f && zombie.RepathGranted)
        {
            zombie.RepathGranted = false;
            zombie.RepathTimer = RepathRate;
            // Stabilize BOTH ends of the query: project them onto the navmesh XZ-first (their own
            // floor), so an off-mesh point can never snap the route onto a different storey. The
            // destination needed this or a target standing over a basement pulled the route down
            // there; the start needs it for the same reason — a zombie pushed a little off the mesh
            // by collision, or standing on a floor the mesh does not cover, otherwise begins its
            // route at whatever polygon is nearest in 3D, which can be below or behind it. The route
            // then walks it back to that polygon first, which reads as a pointless detour.
            Vector3 queryFrom = zombie.Position;
            Vector3 queryTo = destination;
            if (_navmesh != null)
            {
                if (LevelNavmesh.SnapXZ(_navmesh, destination, out Vector3 snappedTo))
                    queryTo = snappedTo;
                if (LevelNavmesh.SnapXZ(_navmesh, zombie.Position, out Vector3 snappedFrom))
                    queryFrom = snappedFrom;
            }
            _scratchPath.Clear();
            if (pathQuery(queryFrom, queryTo, _scratchPath, zombie.Radius) && _scratchPath.Count > 0)
            {
                float newError = HorizontalDistanceSquared(_scratchPath[^1], queryTo);
                float fromError = HorizontalDistanceSquared(queryFrom, queryTo);
                // A route inherited from a previous target scores as no route at all. It is still
                // being walked, but it was aimed somewhere else, so letting it set the bar a
                // replacement has to clear lets it reject its own succession indefinitely.
                float oldError = zombie.PathPoints.Count > 0 && !zombie.RouteServedAnotherTarget
                    ? HorizontalDistanceSquared(zombie.PathPoints[^1], queryTo)
                    : float.PositiveInfinity;

                // Godot returns a useful PARTIAL route to the closest point in the start polygon's
                // connected island when the destination is on another island. PEI contains many such
                // authored islands around buildings. Discarding that route left a newly aggroed zombie
                // in Chase with no steering: clients played its running animation at its random spawn
                // yaw, which looked exactly like it was running in the wrong direction. Follow a partial
                // route when it makes real progress, but never replace an existing route with a worse
                // endpoint. A false/empty/non-progressing query still stands rather than crossing walls.
                bool complete = newError <= 4f;
                bool improvesFrom = newError + 0.01f < fromError;
                bool improvesRoute = newError + 0.01f < oldError;

                // A complete replacement no longer displaces a complete incumbent for free. Where two
                // ways round an obstacle cost about the same, which one the graph returns flips on
                // sub-metre movement, so every repath handed the follower the OTHER one; it turned to
                // face it, walked far enough to flip the answer back, and turned again. Both routes end
                // on the target, so endpoint error — all this gate used to compare — cannot tell them
                // apart, and BlockedRouteTime cannot either, because a body shuttling between two spots
                // is delivering motion the whole time. Captured from play at 13,872 degrees of turning
                // and 164 m travelled for 2 m of progress.
                //
                // So the tie is broken by the route already being walked. A replacement has to be
                // shorter than what is left of the incumbent, by more than the distance a body covers
                // between repaths, or the incumbent stands. Anything genuinely better still wins, and
                // an incumbent that stops delivering motion is still thrown out by the blocked-route
                // timeout — this only stops a coin-flip from costing a turn every half second.
                // `oldError <= 4f` is what stops this becoming stickiness: the incumbent may only refuse
                // a replacement while it still ARRIVES at the target as it now stands. A route whose
                // endpoint the target has walked away from is stale, scores as incomplete, and is
                // replaced as it always was — otherwise a body chasing a moving mark would keep walking
                // to where the mark used to be.
                bool keepsWalking = complete && oldError <= 4f
                    && KeepsWalkingTheCurrentRoute(zombie, _scratchPath);
                if (!keepsWalking && (complete || (improvesFrom && improvesRoute)))
                {
                    zombie.PathPoints.Clear();
                    zombie.PathPoints.AddRange(_scratchPath);
                    zombie.CurrentWaypointIndex = 0;
                    zombie.TargetReached = false;
                    zombie.PathIsPartial = !complete;
                    zombie.RouteServedAnotherTarget = false; // this one was built for the current target
                    // The blocked-route evidence deliberately SURVIVES a repath. Accepting a route is
                    // not evidence that it can be walked; only delivered motion is, and MoveTowards
                    // already clears the counter on the first tick a route actually moves the body. The
                    // reset used to live here, and it disarmed BlockedRouteTimeout in exactly the case
                    // it exists for: a route that is geometrically perfect but physically impassable is
                    // re-issued verbatim on every repath, so the counter restarted every RepathRate
                    // (0.5 s, 7 server ticks) and peaked at 0.56 s — short of the 0.75 s the timeout
                    // needs. A zombie standing in a wall therefore re-adopted the same wall route
                    // forever instead of invalidating it and letting an executable partial route win.
                }
            }
        }

        if (zombie.PathPoints.Count == 0)
            return; // no route has ever succeeded: stand, like the original with path == null

        // Once a partial route has safely carried the body to the edge of its connected nav island,
        // continue with the collision-aware movement component. This repairs small graph seams without
        // granting wall traversal: MoveResolver still sweeps/slides the real capsule, so a genuine wall
        // holds while an artificial navmesh split no longer strands a running-in-place zombie.
        if (zombie.PathIsPartial && zombie.TargetReached)
        {
            MoveTowards(zombie, destination, dt, routeGuided: true);
            return;
        }

        Vector3 velocity = CalculateVelocity(zombie, destination, canTurn, dt);
        if (!canTurn)
            zombie.SteerDirection = directDirection;
        RotateTowards(zombie, zombie.SteerDirection, dt);
        ApplyStep(zombie, velocity * dt, dt, routeGuided: true);
    }

    private readonly List<Vector3> _scratchPath = new();

    // How much shorter a replacement must be before it is worth turning for: one repath's worth of
    // travel. Below that the two answers are a tie the body cannot act on, because it will have moved
    // this far by the time it is asked again.
    private const float RouteSwapMargin = 2f;

    // Is the route in hand still the one to walk? Only asked of a complete replacement against a
    // complete incumbent that was built for this target: a partial incumbent, or one inherited from
    // another target, has no standing to refuse.
    private static bool KeepsWalkingTheCurrentRoute(ZombieInstance zombie, List<Vector3> replacement)
    {
        if (zombie.PathPoints.Count == 0 || zombie.PathIsPartial || zombie.RouteServedAnotherTarget)
            return false;
        float remaining = RouteLengthFrom(zombie.Position, zombie.PathPoints,
            zombie.CurrentWaypointIndex);
        float offered = RouteLengthFrom(zombie.Position, replacement, 0);
        return offered + RouteSwapMargin >= remaining;
    }

    // The ground still to cover: from where the body is to the waypoint it is heading for, then the
    // rest of the route. Measured in XZ, like everything else the follower decides on.
    private static float RouteLengthFrom(Vector3 position, List<Vector3> route, int from)
    {
        if (route.Count == 0)
            return 0f;
        int at = Math.Clamp(from, 0, route.Count - 1);
        float total = MathF.Sqrt(HorizontalDistanceSquared(position, route[at]));
        for (int i = at; i + 1 < route.Count; i++)
            total += MathF.Sqrt(HorizontalDistanceSquared(route[i], route[i + 1]));
        return total;
    }

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
        // A degenerate active segment DOES occur, and dividing by its length below put NaN into Yaw and
        // Position. The advance loop skips consecutive duplicates together, and the short-circuit above
        // covers the final segment — but only when the route's end is within 4 m of the destination.
        // A route whose end sits on the body outlives a retarget on purpose (routes survive a target
        // change so a horde does not freeze mid-chase), and Leave falls back to the current position
        // when neither retreat direction navigates, so a query from a point to itself answers with that
        // point. Retarget to a player 30 m off and this is reached with a == b.
        //
        // Aiming at b is the whole answer: there is no segment to interpolate along, and b is where the
        // route says to be. The caller then measures a zero-length direction on the last waypoint and
        // takes its TargetReached branch, which is the honest reading of a route that ends underfoot —
        // it stops, and the next repath or the partial-route fallback moves the body on.
        float magnitude = (a - b).Length();
        if (magnitude <= 1e-6f)
            return b;
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
        zombie.BlockedRouteTime = 0f;
        zombie.RouteServedAnotherTarget = false;
        zombie.PathIsPartial = false;
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
    private void MoveTowards(ZombieInstance zombie, Vector3 targetPosition, float dt,
        bool routeGuided = false)
    {
        Face(zombie, targetPosition, dt);
        Vector3 flat = new(targetPosition.X - zombie.Position.X, 0f, targetPosition.Z - zombie.Position.Z);
        float distance = flat.Length();
        if (distance < 1e-4f)
            return;
        float step = MathF.Min(zombie.Speed * dt, distance);
        ApplyStep(zombie, flat / distance * step, dt, routeGuided);
    }

    // The physics leg of a movement step: world collision (the host's capsule collide-and-slide),
    // then the other zombies' capsules (CharacterController semantics: the mover is pushed out,
    // the blocker never budges), re-resolved against the world so separation can't shove anyone
    // through a wall, and finally the ground snap. Sustained physical non-delivery invalidates only the
    // stale route so a newly reconciled door route is allowed to replace it on the next bounded repath.
    private void ApplyStep(ZombieInstance zombie, Vector3 step, float dt, bool routeGuided)
    {
        if ((step.X * step.X) + (step.Z * step.Z) < 1e-10f)
            return;
        Vector3 before = zombie.Position;
        Vector3 next = before + step;

        if (MoveResolver != null)
        {
            next = MoveResolver(zombie.Position, next, zombie.Radius);

            // No sidestep heuristic here on purpose: the original has none. A blocked zombie in
            // Unturned simply keeps pushing, and its CharacterController.Move slides it free over a
            // frame or two because that resolve is ITERATIVE. That iteration lives in MoveResolver
            // (the host's collide-and-slide), which is where the escape belongs — picking a side
            // here re-decided every tick and produced a visible left-right shuffle at window sills.
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

        if (!routeGuided)
        {
            zombie.BlockedRouteTime = 0f;
            return;
        }

        float requestedSquared = (step.X * step.X) + (step.Z * step.Z);
        float deliveredX = next.X - before.X, deliveredZ = next.Z - before.Z;
        float deliveredSquared = (deliveredX * deliveredX) + (deliveredZ * deliveredZ);
        float minimumSquared = requestedSquared
            * MinRouteProgressFraction * MinRouteProgressFraction;
        if (deliveredSquared >= minimumSquared)
        {
            zombie.BlockedRouteTime = 0f;
            return;
        }

        zombie.BlockedRouteTime += MathF.Max(0f, dt);
        if (zombie.BlockedRouteTime + 1e-6f < BlockedRouteTimeout)
            return;

        // The physics world is authoritative: after sustained failure this route is demonstrably stale.
        // Clear it before the next repath so a partial route with a worse endpoint can be considered on
        // executability instead of losing forever to the blocked route's perfect endpoint score.
        zombie.BlockedRouteTime = 0f;
        zombie.PathPoints.Clear();
        zombie.PathIsPartial = false;
        zombie.RouteServedAnotherTarget = false;
        zombie.CurrentWaypointIndex = 0;
        zombie.TargetReached = false;
        zombie.RepathTimer = 0f;
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
