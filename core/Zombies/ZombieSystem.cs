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

// Last bounded local-recovery outcome. This is diagnostic only (not replicated or serialized into the
// brain state), so the headless repro trace can explain why a body did not receive a physical route.
public enum EZombieCollisionDetourResult : byte
{
    None = 0,
    Deferred = 1,
    SearchFailed = 2,
    NavmeshRejected = 3,
    GroundRejected = 4,
    Installed = 5,
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

    // Zombie.isStunned, as a countdown rather than a timestamp: seconds of stagger left. Above zero the
    // body does nothing at all — no move, no swing, no repath — which is the whole of what a stun is on
    // the server side. See ZombieStun.
    public float StunRemaining;

    public bool IsStunned => StunRemaining > 0f;
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
    // The last route was disproved by collision, so keep the first replacement that physically moves
    // instead of swapping back to a shorter route through the same obstacle on the next repath.
    public bool RouteEscapingCollision;
    public bool EscapeRouteHasProgress; // the current replacement has delivered forward world motion
    // A short lateral waypoint installed only after physics disproves the graph route. Keeping this
    // distinct from an ordinary partial route lets the body finish walking round an in-face obstacle
    // instead of accepting the same blocked graph answer at every repath.
    public bool CollisionEscapeRoute;
    public EZombieCollisionDetourResult LastCollisionDetourResult;
    public int LastCollisionDetourProbes;
    public int LastCollisionDetourRoutePoints;
    public int LastCollisionDetourFailedEdge = -1;

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

    // What is left of it. Zombie.askDamage subtracts from this and calls the zombie dead at zero, which
    // is the ONE state in this class that something outside the brain writes — everything else here is
    // decided by the tick. Filled at spawn from the map's own zombie table (see HealthFor).
    public ushort Health;

    // The value it started at, so a client — or a debug overlay — can show a fraction rather than a
    // bare number, and so a respawn can put it back without re-deriving the table lookup.
    public ushort MaxHealth;

    public bool IsDead => Health == 0;

    // Zombie.tellAlive's health assignment: the table's number, then the speciality's own adjustment.
    // A crawler carries half again as much (it is harder to hit), a sprinter half as much (it is
    // fragile), and a mega simply uses the table — the mega tables are already written with thousands.
    // The bosses the original also lists have no spawner here, so their fixed numbers are not ported.
    public static ushort HealthFor(ZombieTable table, EZombieSpeciality speciality)
    {
        ArgumentNullException.ThrowIfNull(table);
        return speciality switch
        {
            EZombieSpeciality.Crawler => (ushort)(table.Health * 1.5f),
            EZombieSpeciality.Sprinter => (ushort)(table.Health * 0.5f),
            _ => table.Health,
        };
    }
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

// Blocks a physical interaction along the COMPLETE segment. Unlike VisionBlocked this is not the
// original AlertTool query (which deliberately stops at 95%): attacks and collision recovery need to
// know about a thin wall immediately beside the target as well as World-only terrain/resources.
public delegate bool PhysicalLineBlocked(Vector3 from, Vector3 to);

// Resolves a zombie's step against the world's colliders the way its Unity CharacterController
// does — sliding along walls, trees and props instead of passing through them. Receives the
// current and desired positions plus the capsule radius; returns where the step actually ends.
// Null means an unobstructed world (principally tests or legacy repro seams; both authoritative hosts
// install the shared physics resolver).
public delegate Vector3 ZombieMoveResolver(Vector3 from, Vector3 to, float radius);

// Finds a walkable route over the level's pre-baked navmesh (the Seeker's A* + funnel). Fills
// waypoints from start to destination and returns whether any path exists. Null means no navmesh
// (maps without nav data); a ready graph returning false is authoritative and never grants wall travel.
// `radius` is the body asking. Megas are nearly twice as wide as everyone else, and a route built for
// the narrow one walks their capsule into every jamb it passes, so the width cannot be a property of
// the graph — it has to travel with the request.
public delegate bool ZombiePathQuery(Vector3 from, Vector3 to, List<Vector3> path, float radius);

// Local physical recovery is constrained to enabled baked faces rather than the NavFlag's rectangular
// territory. The segment predicate permits only the graph's walkable corridor or one bounded authored
// seam whose physical ground is checked separately by ZombieSystem.
public delegate bool ZombieNavmeshProject(Vector3 point, out Vector3 projected);
public delegate bool ZombieNavmeshSegmentProbe(Vector3 from, Vector3 to, float radius);

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
    public const float ApproachPathOffset = 1f;         // Zombie.tick LEFT/RIGHT/RUSH spread
    // The A* Pathfinding Project computes paths on a time budget per frame (AstarPath.maxFrameTime),
    // queueing the rest — a horde alerted by the same detect pass never recalculates in one burst.
    // Same shape here: 8 per 0.08 s tick (100/s) sustains the official 0.5 s repath cadence for ~50
    // concurrent hunters — a full region's worth. (Two per tick starved hordes: routes went stale
    // and packs visibly chased where the player USED to be.) Tokens are granted round-robin so no
    // fixed subset monopolizes the budget. Cost note: MapGetPath measures ~1.3 ms median against
    // PEI's map after warm-up, so a saturated budget is ~10 ms of an 80 ms tick — deliberate
    // headroom spent on route freshness.
    public const int MaxRepathsPerTick = 8;
    internal const int MaxCollisionDetourPhysicsQueriesPerTick =
        ZombieCollisionDetour.MaxEdgeProbes + (ZombieCollisionDetour.Rings * ZombieCollisionDetour.Spokes)
        + ZombieCollisionDetour.MaxWallFollowPointProbes
        + 224; // final-route ground support, never speculative rejected edges
    // A valid slide can deliver less than the requested forward component while still moving around an
    // obstacle. Only sustained near-zero delivery invalidates a route, so a stale path through a window
    // cannot outrank the pathfinder's later partial-but-executable route through the door forever.
    public const float BlockedRouteTimeout = 0.75f;
    public const float MinRouteProgressFraction = 0.2f;
    // How much shorter a replacement route has to be before it is worth turning the body onto it.
    // Both edges of the safe range are measured rather than derived. At 0 the tie-break does nothing
    // and the spin it exists to cure comes back (415 degrees of turning to close 13 m in the
    // two-equal-routes test). Replaying the captured horde, every value from 1.0 to 2.0 m produces
    // identical trajectories. From 2.5 m up the band is wide enough to refuse a genuine improvement:
    // one body clings to a worse route, turns 329 degrees instead of 200, and never reaches its
    // target at all. So this sits in the middle of that plateau rather than on either edge.
    public const float RouteTieBand = 1.5f;
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

    // Full World | VisionBlocker segment used to authorize attack start/hit and to prove that a collided
    // route has actually carried the body around its obstacle. Falls back to VisionBlocked for old hosts
    // and old repro dumps, but authoritative hosts install this independently.
    public PhysicalLineBlocked? PhysicalLineBlocked;

    // The CharacterController's world collision, wired to real colliders by the host (optional).
    public ZombieMoveResolver? MoveResolver;

    // The Seeker's navmesh pathfinding, wired to the baked graph by the host (optional).
    public ZombiePathQuery? PathQuery;

    public ZombieNavmeshProject? NavmeshProject;
    public ZombieNavmeshSegmentProbe? NavmeshSupportsSegment;

    // False while an attached pathfinder is still publishing/reconciling its graph. This is distinct
    // from PathQuery returning false for a READY graph, which means the destination is unreachable.
    // While unavailable the original movement fallback keeps pursuing directly instead of freezing.
    public Func<bool>? PathReady;

    // Real ground (object floors, sidewalks) via physics on the host; heightfield otherwise.
    public ZombieGroundSnap? GroundSnap;

    // Fires when a zombie's swing lands (attackTime/2 after it starts): (zombie, player id, damage).
    public Action<ZombieInstance, byte, byte>? OnAttack;

    public IReadOnlyList<NavFlag>? Navmesh => _navmesh; // for route endpoint projection and repro capture

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

        ushort health = ZombieInstance.HealthFor(table, speciality);

        return new ZombieInstance
        {
            Id = id,
            Health = health,
            MaxHealth = health,
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
        // A blocked horde shares one physical-query allowance. Without this, every body crossing the
        // timeout in the same frame could synchronously spend a full 1,024-edge local search plus its
        // 288 ground candidates against Godot physics.
        _collisionDetourPhysicsBudget = MaxCollisionDetourPhysicsQueriesPerTick;
        // A failed local search can consume that entire allowance. Grant the first heavy recovery to
        // the body carrying the most blocked-route evidence; bodies not granted keep their saturated
        // evidence for the next tick. Once the winner is reset, the next-oldest waiter naturally wins,
        // so fixed simulation order cannot let one permanently blocked zombie starve the rest.
        _collisionDetourGranted = null;
        float oldestBlocked = BlockedRouteTimeout - MathF.Max(0f, dt);
        for (int i = 0; i < _zombies.Count; i++)
        {
            ZombieInstance candidate = _zombies[i];
            if (candidate.PathPoints.Count == 0
                || candidate.State is not (EZombieState.Chase or EZombieState.Attack or EZombieState.Return)
                || candidate.BlockedRouteTime + 1e-6f < oldestBlocked)
                continue;
            oldestBlocked = candidate.BlockedRouteTime;
            _collisionDetourGranted = candidate;
        }
        // Poll the asynchronously published pathfinder once per authoritative tick, not once per hunter.
        // A horde must not turn one readiness check into hundreds of identical calls.
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
                // A staggered body will not run Move this tick, so a token spent on it is a token the
                // hunters behind it in the queue do not get — and the budget is what keeps a blocked
                // horde from stalling the tick.
                if (z.IsStunned)
                    continue;
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
    private int _collisionDetourPhysicsBudget;
    private ZombieInstance? _collisionDetourGranted;

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
        zombie.RouteEscapingCollision = false;
        zombie.EscapeRouteHasProgress = false;
        zombie.CollisionEscapeRoute = false;
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

    public ZombieInstance? Find(ushort id)
    {
        for (int i = 0; i < _zombies.Count; i++)
            if (_zombies[i].Id == id)
                return _zombies[i];
        return null;
    }

    // Zombie.askDamage plus the alert DamageTool.damageZombie asks for afterwards: take the health off,
    // and — if it survived — turn it on whoever hit it.
    //
    // Being hit is the one way a zombie aggroes that has nothing to do with stealth: AlertPosition is
    // set by the punch (PlayerEquipment passes the attacker's transform whenever the attacker is inside
    // a nav region), and Zombie.alert is called with startle true, so a zombie punched from behind while
    // it stands idle turns around whether or not it could have heard the attacker walking. Without it
    // the only thing punching a zombie does is kill it silently, several swings later.
    //
    // Returns true when the zombie died on this hit. A dead one is removed from the population here, so
    // the caller's next read of Zombies no longer contains it — that is what makes the death final for
    // the brain; announcing it to the clients is the caller's job.
    // Raised when a hit staggers a zombie: the zombie and the reel index the clients should play. A host
    // replicates it; a pure test just watches it.
    public event Action<ZombieInstance, byte>? Stunned;

    // The mode's two stun switches. NORMAL can stun and stuns on damage alone; HARD turns Only_Critical
    // on, EASY and NORMAL leave it off, and nothing can stun on HARD's Can_Stun=false. Settable because
    // this is server config, read from Config.json in the original.
    public bool CanStun { get; set; } = true;
    public bool OnlyCriticalStuns { get; set; }

    public bool Damage(ZombieInstance zombie, ushort amount, byte instigator,
        IReadOnlyList<ZombiePlayerView> players,
        EZombieStunOverride stunOverride = EZombieStunOverride.None)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        ArgumentNullException.ThrowIfNull(players);
        if (amount == 0 || zombie.IsDead)
            return false;

        zombie.Health = UnturnedGodot.Damage.PunchDamageResolver.ApplyToHealth(zombie.Health, amount);
        if (zombie.IsDead)
        {
            Remove(zombie);
            return true;
        }

        // Zombie.askDamage stuns only on the branch where the zombie SURVIVED — a killing blow has no
        // stagger to play, because the body is being replaced by a ragdoll.
        if (ZombieStun.ShouldStun(amount, zombie.Speciality == EZombieSpeciality.Mega, CanStun,
                OnlyCriticalStuns, stunOverride))
            Stun(zombie);

        if (instigator != byte.MaxValue && TryGetPlayer(players, instigator, out ZombiePlayerView attacker))
            Alert(zombie, attacker, players);
        return false;
    }

    // Zombie.stun: the body stops where it stands, drops the swing it was in the middle of, and plays one
    // of its speciality's reels.
    //
    // Three things go, and each is a bug if it stays:
    //
    // The in-progress swing (PendingHit) is CANCELLED rather than paused. stun() sets isAttacking false,
    // and a hit left counting down would land its damage a moment INTO the stagger — which is precisely
    // the hit the player staggered the zombie to avoid.
    //
    // The ATTACK STATE goes with it, back to Chase. It is replicated, and a zombie left in Attack for the
    // second it spends staggered has every client re-triggering its swing animation off that state: the
    // body plays the stagger and then snaps into a swing it is not making. Leaving is the same
    // `isAttacking = false`, seen from the wire.
    //
    // And the swing CLOCK is reset. Without it, a zombie interrupted late in its cooldown swings the
    // instant the stagger ends, which turns the stun from a reprieve into a delay.
    public void Stun(ZombieInstance zombie)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        if (zombie.IsDead)
            return;

        zombie.StunRemaining = ZombieStun.DurationSeconds;
        zombie.PendingHit = -1f;
        zombie.SinceSwing = 0f;
        if (zombie.State == EZombieState.Attack)
            zombie.State = EZombieState.Chase;
        Stunned?.Invoke(zombie, ZombieStun.ClipFor(zombie.Speciality, _random.NextSingle()));
    }

    // Takes a zombie out of the population. Its aggro claim goes back to the player it was holding —
    // the tally decides how the next zombie to notice that player approaches them, and a dead zombie
    // that keeps its claim inflates that number for the rest of the session.
    public void Remove(ZombieInstance zombie)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        if (zombie.TargetPlayer != byte.MaxValue)
        {
            AdjustAgro(zombie.TargetPlayer, -1);
            zombie.TargetPlayer = byte.MaxValue;
        }
        _zombies.Remove(zombie);
        if (zombie.Bound < _byBound.Length)
            _byBound[zombie.Bound].Remove(zombie);
    }

    private void Behave(ZombieInstance zombie, IReadOnlyList<ZombiePlayerView> players, float dt)
    {
        // A stunned zombie does nothing while it recovers. Zombie.OnUpdate returns early on isStunned
        // before any of its behaviour runs, so this sits ahead of the swing clock as well: an interrupted
        // swing does not resume where it left off, it is simply gone (stun() clears isAttacking).
        if (zombie.IsStunned)
        {
            zombie.StunRemaining -= dt;
            return;
        }

        zombie.SinceSwing += dt;
        if (zombie.PendingHit >= 0f)
        {
            zombie.PendingHit -= dt;
            // A swing is only an animation until its hit frame. Revalidate the target at that frame:
            // it can have stepped away or behind a thin fence since the swing began.
            if (zombie.PendingHit < 0f
                && TryGetPlayer(players, zombie.TargetPlayer, out ZombiePlayerView hitTarget)
                && CanHit(zombie, hitTarget))
                LandHit(zombie);
        }

        switch (zombie.State)
        {
            case EZombieState.Chase:
            case EZombieState.Attack:
                Hunt(zombie, players, dt);
                break;

            case EZombieState.Return:
                Move(zombie, zombie.LeaveTo, zombie.LeaveTo, approachOffset: 0f,
                    canTurn: true, default, dt);
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
        bool canAttack = sqrHorizontal < zombie.AttackRangeSquared
            && vertical < zombie.VerticalAttackRange;
        Vector3 eye = zombie.Position + (Vector3.Up * ZombieBody.EyeHeight);
        // Perception remains AlertTool-compatible (95% and VisionBlocker-only). Physical permission is a
        // different question: use the complete eye-to-eye segment so a fence in the final 5% is not
        // invisible, and include World-only terrain/resources so clear visual LOS cannot prematurely end
        // a collision escape. Old replay/host seams fall back to their recorded visibility answer.
        bool hasPhysicalLine = PhysicalLineBlocked != null || VisionBlocked != null;
        bool needsVision = sqrHorizontal <= 4f
            || (zombie.RouteEscapingCollision && PhysicalLineBlocked == null && VisionBlocked != null);
        bool visionBlocked = needsVision && VisionBlocked?.Invoke(eye, target.Position) == true;
        // Close steering needs the same answer even just outside this speciality's attack range: the
        // reported face-to-face case otherwise spent the collision timeout staring into the wall before
        // it allowed its already-valid route to turn the body around it.
        bool needsPhysicalLine = canAttack || sqrHorizontal <= 4f || zombie.RouteEscapingCollision;
        bool physicalBlocked = false;
        if (needsPhysicalLine)
        {
            Vector3 playerEye = target.Position
                + (Vector3.Up * PlayerConfig.EyeHeightFor(target.Stance));
            physicalBlocked = PhysicalLineBlocked != null
                ? PhysicalLineBlocked(eye, playerEye)
                : (needsVision ? visionBlocked : VisionBlocked?.Invoke(eye, target.Position) == true);
        }

        if (canAttack && !physicalBlocked)
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
        // An eye ray only proves that eye-height geometry is clear. A sill, kerb or thin fence can sit
        // below both endpoints while the movement capsule is still unable to take the old shortcut.
        // Once an escape route has actually moved the body, ask the same resolver that moves it whether
        // the complete direct segment is now executable. With no movement seam (old hosts/replays), keep
        // the historical physical-line fallback because there is no stronger authority to consult.
        bool escapeCleared = zombie.RouteEscapingCollision && hasPhysicalLine && !physicalBlocked
            && (MoveResolver == null || (zombie.EscapeRouteHasProgress
                && CapsuleCanReach(zombie, target.Position)));
        if (escapeCleared)
        {
            zombie.RouteEscapingCollision = false;
            zombie.EscapeRouteHasProgress = false;
            zombie.CollisionEscapeRoute = false;
        }
        if (sqrHorizontal > 4f)
        {
            // The lateral formation offset is useful in open ground, but after collision has disproved
            // a route it can put the next destination inside the very obstacle being escaped. In the
            // captured PEI corner the body is 2.1 m from the player, so LEFT turns the physical recovery
            // target one metre into the house. Recover toward the real target until its full physical
            // segment clears.
            if (!zombie.RouteEscapingCollision)
            {
                float yawRad = Mathf.DegToRad(zombie.Yaw);
                Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
                var right = new Vector3(forward.Z, 0f, -forward.X);
                destination += zombie.Path switch
                {
                    EZombiePath.Left => -right * ApproachPathOffset,
                    EZombiePath.Right => right * ApproachPathOffset,
                    _ => -forward * ApproachPathOffset, // RUSH
                };
            }
        }
        else
        {
            // Inside two metres the original turns straight at the player so attacks stay faced, but
            // that assumes there is nothing between them. Across a thin wall the route correctly goes
            // around while this override keeps the body staring and pushing into the player through
            // the wall. It then invalidates the executable route every 0.75 s for "not moving" and
            // adopts it again forever. When vision geometry proves the target is occluded, keep the
            // route's steering until the complete physical segment is actually clear.
            // Visual occlusion still selects route-facing close to an ordinary wall. A recovery route is
            // stronger: keep its steering even through World-only geometry until the physical line is
            // clear. With no query installed, absence of evidence must not cancel recovery.
            canTurn = visionBlocked || physicalBlocked || zombie.RouteEscapingCollision;
            if (!canTurn)
            {
                directDirection = target.Position - zombie.Position;
            }
        }
        Move(zombie, destination, target.Position, ApproachPathOffset, canTurn, directDirection, dt);
    }

    // LegacyAIPathNoRedist.move(), ported: repath on the cadence (a FAILED query keeps a route that
    // physics is still delivering, like OnPathComplete ignoring an errored ABPath), then follow it with
    // CalculateVelocity + RotateTowards and step through the physics. With no route at all the
    // zombie stands (path == null in the original); the straight-line seek only exists for maps
    // without a navmesh, where the original also falls back to its NonPathfinding component.
    private void Move(ZombieInstance zombie, Vector3 destination, Vector3 routeAnchor,
        float approachOffset, bool canTurn, Vector3 directDirection, float dt)
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
                // Storeys can share the same XZ. A downstairs endpoint directly below an upstairs
                // target is not complete and must not veto the real route via stairs.
                float newError = (_scratchPath[^1] - queryTo).LengthSquared();
                float fromError = (queryFrom - queryTo).LengthSquared();
                // A route inherited from a previous target scores as no route at all. It is still
                // being walked, but it was aimed somewhere else, so letting it set the bar a
                // replacement has to clear lets it reject its own succession indefinitely.
                float oldError = zombie.PathPoints.Count > 0 && !zombie.RouteServedAnotherTarget
                    ? (zombie.PathPoints[^1] - queryTo).LengthSquared()
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
                // The closest reachable face can lie around a doorway that initially leads AWAY from
                // a nearby target across a wall. Requiring its endpoint to improve straight-line
                // distance rejects that only executable prefix and leaves a route-less zombie staring
                // through the window. With no incumbent, accept a partial route that actually advances
                // across the mesh; once it reaches that safe prefix, the collision-aware fallback takes
                // over. A later, worse partial still cannot replace a better route already being walked.
                bool startsUsefulPartial = !complete && zombie.PathPoints.Count == 0
                    && _scratchPath.Count > 1
                    && HorizontalDistanceSquared(_scratchPath[0], _scratchPath[^1])
                        > EndReachedDistance * EndReachedDistance;

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
                // shorter than what is left of the incumbent by more than RouteTieBand, or the
                // incumbent stands. Anything genuinely better still wins, and
                // an incumbent that stops delivering motion is still thrown out by the blocked-route
                // timeout — this only stops a coin-flip from costing a turn every half second.
                // RouteEndpointStillServes is what stops this becoming stickiness: the incumbent may
                // only refuse a replacement while it still arrives at the target as it now stands. Judge
                // that against the REAL target, not the one-metre LEFT/RIGHT/RUSH formation point. The
                // latter rotates with the zombie's own yaw, so comparing endpoints to it made an unmoving
                // player look stale every time the body turned and reintroduced the two-route loop.
                // A route that has physically moved after collision invalidated its predecessor is not a
                // near-tie: it is a proven escape. Merely being the first answer after the timeout proves
                // nothing — it can be the same blocked shortcut, and must not veto a later detour before
                // it delivers forward motion.
                bool incumbentStillServes = zombie.PathPoints.Count > 0
                    && RouteEndpointStillServes(zombie.PathPoints[^1], routeAnchor, approachOffset);
                // Partial prefixes end at the closest reachable face, so two queries from opposite
                // sides of a doorway can alternately call outside and inside "closer" and reverse the
                // zombie every repath. Finish the safe prefix already in hand; a COMPLETE route may
                // still replace it immediately, and physical non-delivery still clears it on timeout.
                bool keepsPartialPrefix = !complete && zombie.PathIsPartial
                    && zombie.PathPoints.Count > 0;
                bool keepsCollisionEscape = zombie.CollisionEscapeRoute
                    && zombie.RouteEscapingCollision;
                bool keepsWalking = keepsCollisionEscape || keepsPartialPrefix
                    || (complete && incumbentStillServes
                        && (zombie.RouteEscapingCollision
                            ? zombie.EscapeRouteHasProgress && !zombie.PathIsPartial
                            : KeepsWalkingTheCurrentRoute(zombie, _scratchPath, destination)));
                if (!keepsWalking
                    && (complete || (improvesFrom && improvesRoute) || startsUsefulPartial))
                {
                    zombie.PathPoints.Clear();
                    zombie.PathPoints.AddRange(_scratchPath);
                    zombie.CurrentWaypointIndex = 0;
                    zombie.TargetReached = false;
                    zombie.PathIsPartial = !complete;
                    zombie.RouteServedAnotherTarget = false; // this one was built for the current target
                    zombie.CollisionEscapeRoute = false;
                    if (zombie.RouteEscapingCollision)
                        zombie.EscapeRouteHasProgress = false;
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

        // A complete route is only complete for the target position it was built against. Once the
        // body has reached that endpoint but still cannot attack (or the target has left its legitimate
        // completion radius), standing there is not progress and produces no movement step for the
        // collision timeout to judge. Prefer a freshly accepted graph route above; if none exists, ask
        // the bounded physical planner once and discard the exhausted incumbent on failure.
        bool exhaustedCompleteRoute = zombie.TargetReached && !zombie.PathIsPartial
            && zombie.PathPoints.Count > 0
            && (zombie.State == EZombieState.Chase
                || !RouteEndpointStillServes(zombie.PathPoints[^1], routeAnchor, approachOffset));
        if (exhaustedCompleteRoute)
        {
            zombie.RouteEscapingCollision = true;
            zombie.EscapeRouteHasProgress = false;
            if (!TryInstallCollisionDetour(zombie, zombie.Position, routeAnchor))
            {
                zombie.PathPoints.Clear();
                zombie.CurrentWaypointIndex = 0;
                zombie.TargetReached = false;
                zombie.CollisionEscapeRoute = false;
                zombie.RouteServedAnotherTarget = false;
                zombie.RepathTimer = 0f;
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
            MoveTowards(zombie, destination, dt, routeGuided: true,
                collisionDetourDestination: routeAnchor);
            return;
        }

        Vector3 velocity = CalculateVelocity(zombie, destination, canTurn, dt,
            out Vector3 intendedDirection);
        if (!canTurn)
            zombie.SteerDirection = directDirection;
        RotateTowards(zombie, zombie.SteerDirection, dt);
        ApplyStep(zombie, velocity * dt, intendedDirection, routeAnchor, dt, routeGuided: true);
    }

    private readonly List<Vector3> _scratchPath = new();
    private readonly ZombieCollisionDetour _collisionDetour = new();

    // Is the route in hand still the one to walk? Only asked of a complete replacement against a
    // complete incumbent that was built for this target: a partial incumbent, or one inherited from
    // another target, has no standing to refuse.
    //
    // A replacement shorter than what is left of the incumbent by more than the tie band is genuinely
    // better and wins; anything inside the band is the coin flip this exists to stop.
    //
    // The band is NOT how far the body travels between repaths. Both lengths here are measured from
    // the same position at the same instant, so travel does not enter the comparison — the quantity
    // being absorbed is how much two near-tied answers to the SAME question can differ, which is a
    // property of the geometry, not of the body asking. A sprinter and a crawler standing on the same
    // spot get the same two routes and should make the same choice.
    //
    // Complete endpoints can differ by almost two metres. On the active final segment the follower
    // ignores either endpoint and aims at destination, so both lengths below replace that last point
    // with the destination they actually walk to. Without that replacement, endpoint placement alone
    // could reverse the 1.5 m comparison. Completeness itself is 3D: a route directly below the player
    // on another storey has the same XZ but no standing to veto the stairs.
    private static bool KeepsWalkingTheCurrentRoute(ZombieInstance zombie, List<Vector3> replacement,
        Vector3 destination)
    {
        if (zombie.PathPoints.Count == 0 || zombie.PathIsPartial || zombie.RouteServedAnotherTarget)
            return false;
        float remaining = EffectiveRouteLengthFrom(zombie.Position, zombie.PathPoints,
            zombie.CurrentWaypointIndex, destination);
        // From the second waypoint, because the first is not a place the body goes. The query is made
        // from the position SNAPPED onto the mesh, so a body pushed off it by collision gets a route
        // beginning several metres away, and the follower does not walk there and back — CalculateVelocity
        // joins the route at index 1 and uses index 0 only as the segment origin to aim along. Billing
        // those two legs overstates the offer by the snap distance.
        //
        // This one is reasoned from the follower's own indexing rather than from a repro: no test here
        // makes it change an outcome, because the veto turns out to be far harder to hold than it
        // looks — the destination is offset a metre perpendicular to the body's own yaw, so it swings
        // as the body turns and `oldError` leaves the arming radius on its own. Measuring the offer
        // the way it is actually walked is right regardless of whether today's thresholds notice.
        float offered = EffectiveRouteLengthFrom(zombie.Position, replacement,
            replacement.Count > 1 ? 1 : 0, destination);
        return offered + RouteTieBand >= remaining;
    }

    // A successful query may finish within a 2 m 3D sphere around its requested endpoint. Chase
    // endpoints are requested up to one horizontal metre from the actual player for horde formation,
    // so subtract that possible offset from the horizontal error before testing the SAME 3D sphere.
    // Treating horizontal and vertical tolerances independently admits diagonal endpoints outside the
    // completion sphere and can let a route on the lower storey veto the real stairs route.
    private static bool RouteEndpointStillServes(Vector3 endpoint, Vector3 anchor,
        float approachOffset)
    {
        const float completionRadius = 2f;
        float horizontal = MathF.Sqrt(HorizontalDistanceSquared(endpoint, anchor));
        float residualHorizontal = MathF.Max(0f, horizontal - MathF.Max(0f, approachOffset));
        float vertical = endpoint.Y - anchor.Y;
        return (residualHorizontal * residualHorizontal) + (vertical * vertical)
            <= completionRadius * completionRadius;
    }

    // The ground still to cover, measured exactly as the follower walks it. On a complete route whose
    // final endpoint is within four metres, CalculateTargetPoint ignores that endpoint and aims from the
    // active final segment directly at destination. Model that by replacing the last point rather than
    // charging for a leg the body never executes.
    internal static float EffectiveRouteLengthFrom(Vector3 position, List<Vector3> route, int from,
        Vector3 destination)
    {
        if (route.Count == 0)
            return 0f;
        int at = Math.Clamp(from, 0, route.Count - 1);
        int last = route.Count - 1;
        bool walksToDestination = HorizontalDistanceSquared(route[last], destination) < 16f;
        Vector3 Point(int index) => walksToDestination && index == last ? destination : route[index];

        float total = MathF.Sqrt(HorizontalDistanceSquared(position, Point(at)));
        for (int i = at; i + 1 < route.Count; i++)
            total += MathF.Sqrt(HorizontalDistanceSquared(Point(i), Point(i + 1)));
        return total;
    }

    // LegacyAIPathNoRedist.CalculateVelocity, ported: advance waypoints within pickNextWaypointDist
    // (XZ), aim at the forwardLook point interpolated ON THE CURRENT SEGMENT, stop within
    // endReachedDistance of the route's end, and return a velocity along the body's FORWARD scaled
    // by how aligned the body is with the desired direction (a zombie turns INTO its route instead
    // of strafing sideways onto it) and by the slowdown near the target point.
    private static Vector3 CalculateVelocity(ZombieInstance zombie, Vector3 destination,
        bool canTurn, float dt, out Vector3 intendedDirection)
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
            float pickNext = zombie.CollisionEscapeRoute ? 0.2f : PickNextWaypointDist;
            while (index < vp.Count - 1
                && HorizontalDistanceSquared(vp[index], zombie.Position)
                    < pickNext * pickNext)
                index++;
            segmentStart = vp[index - 1];
        }
        zombie.CurrentWaypointIndex = index;

        Vector3 targetPoint = CalculateTargetPoint(zombie.Position, segmentStart,
            vp[vp.Count == 1 ? 0 : index],
            index == vp.Count - 1 && !zombie.CollisionEscapeRoute, destination);
        Vector3 direction = targetPoint - zombie.Position;
        direction.Y = 0f;
        intendedDirection = direction;
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

    private bool CanHit(ZombieInstance zombie, ZombiePlayerView target)
    {
        if (HorizontalDistanceSquared(zombie.Position, target.Position) >= zombie.AttackRangeSquared
            || MathF.Abs(target.Position.Y - zombie.Position.Y) >= zombie.VerticalAttackRange)
            return false;
        Vector3 from = zombie.Position + (Vector3.Up * ZombieBody.EyeHeight);
        Vector3 to = target.Position + (Vector3.Up * PlayerConfig.EyeHeightFor(target.Stance));
        if (PhysicalLineBlocked != null)
            return !PhysicalLineBlocked(from, to);
        return VisionBlocked?.Invoke(from, target.Position) != true;
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
        zombie.RouteEscapingCollision = false;
        zombie.EscapeRouteHasProgress = false;
        zombie.CollisionEscapeRoute = false;
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
        bool routeGuided = false, Vector3? collisionDetourDestination = null)
    {
        Face(zombie, targetPosition, dt);
        Vector3 flat = new(targetPosition.X - zombie.Position.X, 0f, targetPosition.Z - zombie.Position.Z);
        float distance = flat.Length();
        if (distance < 1e-4f)
            return;
        float step = MathF.Min(zombie.Speed * dt, distance);
        ApplyStep(zombie, flat / distance * step, flat,
            collisionDetourDestination ?? targetPosition, dt, routeGuided);
    }

    // The physics leg of a movement step: world collision (the host's capsule collide-and-slide),
    // then the other zombies' capsules (CharacterController semantics: the mover is pushed out,
    // the blocker never budges), re-resolved against the world so separation can't shove anyone
    // through a wall, and finally the ground snap. Only the first, route-requested world move is
    // evidence about the route: a neighbour holding the final position back means "wait for the
    // crowd", not "the nav route crosses a wall". Sustained world non-delivery invalidates only the
    // stale route so a newly reconciled door route is allowed to replace it on the next bounded repath.
    private void ApplyStep(ZombieInstance zombie, Vector3 step, Vector3 intendedDirection,
        Vector3 destination, float dt, bool routeGuided)
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
        // Preserve what the collision world delivered before zombie separation changes the result.
        // The second resolve below protects the world from a separation push, but neither crowd
        // displacement nor its clamp says anything about whether the requested nav segment is valid.
        Vector3 worldNext = next;

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

        float requestedLength = MathF.Sqrt((step.X * step.X) + (step.Z * step.Z));
        float deliveredX = worldNext.X - before.X, deliveredZ = worldNext.Z - before.Z;
        float intendedLength = MathF.Sqrt((intendedDirection.X * intendedDirection.X)
            + (intendedDirection.Z * intendedDirection.Z));
        // The requested step follows the body's FORWARD while it turns toward the route. Projecting
        // onto that step therefore called any CharacterController wall slide progress, including the
        // captured fence loop where the body repeatedly rotated, slid a few millimetres along the
        // fence, and snapped back. The route's intended leg is the actual question: only delivery
        // toward that leg clears the evidence. Ordinary turning has far less than the 0.75 s timeout
        // to align, and crowd separation remains deliberately absent because worldNext predates it.
        float deliveredAlongIntent = intendedLength > 1e-5f
            ? ((deliveredX * intendedDirection.X) + (deliveredZ * intendedDirection.Z))
                / intendedLength
            : 0f;
        float minimumAlongIntent = requestedLength * MinRouteProgressFraction;
        if (deliveredAlongIntent >= minimumAlongIntent)
        {
            zombie.BlockedRouteTime = 0f;
            if (zombie.RouteEscapingCollision)
                zombie.EscapeRouteHasProgress = true;
            return;
        }

        // Every edge of a local collision route was already accepted by this same capsule resolver.
        // While the body rotates into one of those short legs, forward delivery is sufficient evidence
        // that the verified route is being executed. Graph routes do not get this exception: the fence
        // loop consisted precisely of unverified forward wall-slides while the body kept rotating.
        float deliveredAlongStep = ((deliveredX * step.X) + (deliveredZ * step.Z))
            / requestedLength;
        if (zombie.CollisionEscapeRoute && deliveredAlongStep >= minimumAlongIntent)
        {
            zombie.BlockedRouteTime = 0f;
            zombie.EscapeRouteHasProgress = true;
            return;
        }

        zombie.BlockedRouteTime += MathF.Max(0f, dt);
        if (zombie.BlockedRouteTime + 1e-6f < BlockedRouteTimeout)
            return;

        if (!ReferenceEquals(_collisionDetourGranted, zombie))
        {
            // Do not discard the evidence while this tick's bounded physics allowance belongs to an
            // older waiter. Keeping it saturated makes this body eligible again immediately next tick.
            zombie.BlockedRouteTime = BlockedRouteTimeout;
            SetCollisionDetourDiagnostic(zombie, EZombieCollisionDetourResult.Deferred,
                probes: 0, routePoints: 0);
            return;
        }

        // The physics world is authoritative: after sustained failure this route is demonstrably stale.
        // Clear it before the next repath so a partial route with a worse endpoint can be considered on
        // executability instead of losing forever to the blocked route's perfect endpoint score. Remember
        // WHY it was cleared: once an alternative starts moving, a later geometrically-shorter query must
        // not immediately steer the body back into the same collision-only wall.
        zombie.BlockedRouteTime = 0f;
        zombie.RouteEscapingCollision = true;
        zombie.EscapeRouteHasProgress = false;
        if (TryInstallCollisionDetour(zombie, before, destination))
            return;
        zombie.PathPoints.Clear();
        zombie.PathIsPartial = false;
        zombie.CollisionEscapeRoute = false;
        zombie.RouteServedAnotherTarget = false;
        zombie.CurrentWaypointIndex = 0;
        zombie.TargetReached = false;
        zombie.RepathTimer = 0f;
    }

    // Keep the state-machine seam here and the bounded physical search in ZombieCollisionDetour. The
    // scratch list is safe to reuse: movement is sequential, and the last graph query has already been
    // copied into the zombie before a blocked step can reach this method.
    private bool TryInstallCollisionDetour(ZombieInstance zombie, Vector3 from, Vector3 destination)
    {
        if (MoveResolver == null)
            return false;
        if (_collisionDetourPhysicsBudget <= 0)
        {
            SetCollisionDetourDiagnostic(zombie, EZombieCollisionDetourResult.Deferred,
                probes: 0, routePoints: 0);
            return false;
        }
        if (!_collisionDetour.TryFind(from, destination, zombie.Radius, DetourPoint,
            ResolveCollisionDetourMotion, _scratchPath))
        {
            SetCollisionDetourDiagnostic(zombie, EZombieCollisionDetourResult.SearchFailed);
            return false;
        }
        for (int i = 1; i < _scratchPath.Count; i++)
            // PointProbe already constrained every generated waypoint to an enabled face. The body
            // and the final target are deliberately exempt: either can stand on a real object floor
            // omitted/disabled in the baked mesh, which is the graph seam this fallback repairs.
            // Validate only generated-to-generated edges against nav topology; every edge, including
            // the two boundary legs, is still checked continuously against physical ground below.
            if (i > 1 && i < _scratchPath.Count - 1
                && NavmeshSupportsSegment?.Invoke(
                    _scratchPath[i - 1], _scratchPath[i], zombie.Radius) == false)
            {
                SetCollisionDetourDiagnostic(zombie, EZombieCollisionDetourResult.NavmeshRejected, i);
                _scratchPath.Clear();
                return false;
            }
            else if (!PhysicalGroundSupportsSegment(
                _scratchPath[i - 1], _scratchPath[i], zombie.Radius))
            {
                SetCollisionDetourDiagnostic(zombie, EZombieCollisionDetourResult.GroundRejected, i);
                _scratchPath.Clear();
                return false;
            }
        zombie.PathPoints.Clear();
        zombie.PathPoints.AddRange(_scratchPath);
        zombie.CurrentWaypointIndex = 1;
        zombie.TargetReached = false;
        zombie.PathIsPartial = false;
        zombie.RouteServedAnotherTarget = false;
        zombie.CollisionEscapeRoute = true;
        zombie.RepathTimer = RepathRate;
        SetCollisionDetourDiagnostic(zombie, EZombieCollisionDetourResult.Installed);
        return true;
    }

    private void SetCollisionDetourDiagnostic(ZombieInstance zombie,
        EZombieCollisionDetourResult result, int failedEdge = -1,
        int probes = -1, int routePoints = -1)
    {
        zombie.LastCollisionDetourResult = result;
        zombie.LastCollisionDetourProbes = probes >= 0 ? probes : _collisionDetour.LastProbeCount;
        zombie.LastCollisionDetourRoutePoints = routePoints >= 0 ? routePoints : _scratchPath.Count;
        zombie.LastCollisionDetourFailedEdge = failedEdge;
    }

    private bool DetourPoint(Vector3 point, out Vector3 grounded)
    {
        if (NavmeshProject != null)
        {
            if (!NavmeshProject(point, out grounded))
                return false;
        }
        else
        {
            grounded = point;
            if (!CheckNavigation(point))
                return false;
        }
        if (GroundSnap != null)
        {
            if (!SpendCollisionDetourPhysicsQuery()
                || !GroundSnap(grounded, out float y))
                return false;
            grounded.Y = y;
        }
        return true;
    }

    private Vector3 ResolveCollisionDetourMotion(Vector3 from, Vector3 target, float radius)
    {
        if (!SpendCollisionDetourPhysicsQuery())
            return from;
        return MoveResolver!(from, target, radius);
    }

    private bool PhysicalGroundSupportsSegment(Vector3 from, Vector3 target, float radius)
    {
        float horizontal = MathF.Sqrt(HorizontalDistanceSquared(from, target));
        if (horizontal <= 1e-5f)
            return true;
        float spacing = MathF.Max(0.25f, radius);
        int intervals = Math.Max(1, (int)MathF.Ceiling(horizontal / spacing));
        float step = horizontal / intervals;
        float maxVerticalStep = MathF.Max(PlayerConfig.StepOffset,
            MathF.Tan(Mathf.DegToRad(PlayerConfig.MaxWalkableSlopeDegrees)) * step) + 0.05f;
        float previous = from.Y;
        for (int i = 1; i <= intervals; i++)
        {
            Vector3 sample = from.Lerp(target, i / (float)intervals);
            float y;
            if (GroundSnap != null)
            {
                if (!SpendCollisionDetourPhysicsQuery() || !GroundSnap(sample, out y))
                    return false;
            }
            else if (!_ground(sample.X, sample.Z, out y))
                return false;
            // A missing floor or cliff is the dangerous case: the horizontal capsule sweep cannot see
            // support disappearing beneath it. Upward geometry is already authoritative in MoveResolver
            // and a nearby counter top can legitimately win GroundSnap without lying on the centreline,
            // so treating every upward sample as the path's floor rejects open door/counter routes.
            if (previous - y > maxVerticalStep)
                return false;
            previous = y;
        }
        return true;
    }

    private bool SpendCollisionDetourPhysicsQuery()
    {
        if (_collisionDetourPhysicsBudget <= 0)
            return false;
        _collisionDetourPhysicsBudget--;
        return true;
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

    private bool CapsuleCanReach(ZombieInstance zombie, Vector3 target)
    {
        Vector3 resolved = MoveResolver!(zombie.Position, target, zombie.Radius);
        return HorizontalDistanceSquared(resolved, target) <= 1e-6f;
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
