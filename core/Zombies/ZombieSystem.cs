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

// The animation-relevant behavior states a zombie replicates.
public enum EZombieState : byte
{
    Idle = 0,
    Chase = 1,
    Attack = 2,
    Return = 3,
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
    public float AttackCooldown;

    // ZombieManager.getZombieSpeed with Slow_Movement=false (NORMAL difficulty).
    public float Speed => Speciality switch
    {
        EZombieSpeciality.Crawler => 3f,
        EZombieSpeciality.Sprinter => 6.5f,
        EZombieSpeciality.Mega => 6f,
        _ => 5.5f,
    };
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

// The server-side zombie brain: spawning per ZombieManager.generateZombies, aggro per AlertTool,
// chase/attack per Zombie.cs. Movement is the straight-line seek of Unturned's
// NonPathfindingZombieMovementComponent — velocity = direction-to-target x speed, ground-clamped —
// so no navmesh is required, matching the game's own fallback movement model.
public sealed class ZombieSystem
{
    // ZombiesConfigData NORMAL difficulty.
    public const float SpawnChance = 0.25f;
    public const float CrawlerChance = 0.15f;
    public const float SprinterChance = 0.15f;

    public const float AttackRange = 2f;    // Zombie.ATTACK_PLAYER
    public const float AttackTime = 0.5f;   // dedicated-server Attack_0 fallback length
    public const float ArriveRadius = 0.5f; // close enough to home to go back to idle

    private readonly IReadOnlyList<ZombieTable> _tables;
    private readonly IReadOnlyList<NavBound> _bounds;
    private readonly GroundSampler _ground;
    private readonly List<ZombieInstance> _zombies = new();
    private float _detectTimer;

    public IReadOnlyList<ZombieInstance> Zombies => _zombies;

    // Fires when a zombie lands a hit: (zombie, player id, table damage).
    public Action<ZombieInstance, byte, byte>? OnAttack;

    public ZombieSystem(
        IReadOnlyList<ZombieTable> tables,
        IReadOnlyList<NavBound> bounds,
        GroundSampler ground)
    {
        _tables = tables;
        _bounds = bounds;
        _ground = ground;
    }

    // ZombieManager.generateZombies: per nav bound, cap = min(flag maxZombies, ceil(eligible spawn
    // count x Spawn_Chance)), then draw spawnpoints at random without replacement. Positions arrive
    // in Unity coordinates straight from Animals.dat and are mirrored (z -> -z) here.
    public void Spawn(IReadOnlyList<ZombieSpawnpointData> spawnpoints, Random random)
    {
        var perBound = new List<ZombieSpawnpointData>[_bounds.Count];
        for (int i = 0; i < perBound.Length; i++)
            perBound[i] = new List<ZombieSpawnpointData>();
        foreach (ZombieSpawnpointData spawn in spawnpoints)
        {
            Vector3 godotPoint = new(spawn.Point.X, spawn.Point.Y, -spawn.Point.Z);
            byte bound = LevelNavigationData.TryGetBound(_bounds, godotPoint);
            if (bound != LevelNavigationData.NoBound)
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
                _zombies.Add(Create(nextId++, (byte)b, spawn, random));
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
        _detectTimer += dt;
        if (_detectTimer >= ZombieDetection.DetectInterval)
        {
            _detectTimer = 0f;
            Detect(players);
        }

        foreach (ZombieInstance zombie in _zombies)
            Behave(zombie, players, dt);
    }

    private void Detect(IReadOnlyList<ZombiePlayerView> players)
    {
        foreach (ZombiePlayerView player in players)
        {
            float radius = ZombieDetection.RadiusFor(player.Stance, player.Moving);
            float sqrRadius = radius * radius;
            bool sneak = player.Stance != EPlayerStance.Sprint;
            byte bound = LevelNavigationData.TryGetBound(_bounds, player.Position);
            if (bound == LevelNavigationData.NoBound)
                continue;

            foreach (ZombieInstance zombie in _zombies)
            {
                if (zombie.Bound != bound)
                    continue;
                Vector3 playerToZombie = zombie.Position - player.Position;
                float yawRad = Mathf.DegToRad(zombie.Yaw);
                Vector3 forward = new(-MathF.Sin(yawRad), 0f, -MathF.Cos(yawRad));
                if (!ZombieDetection.IsDetected(forward, playerToZombie, sqrRadius, sneak))
                    continue;
                Alert(zombie, player, players);
            }
        }
    }

    // Zombie.alert(Player): keep the nearest aggro target — a new alert only replaces a live target
    // when the newcomer is closer.
    private void Alert(ZombieInstance zombie, in ZombiePlayerView player, IReadOnlyList<ZombiePlayerView> players)
    {
        if (zombie.TargetPlayer != byte.MaxValue && zombie.TargetPlayer != player.Id
            && TryGetPlayer(players, zombie.TargetPlayer, out ZombiePlayerView current))
        {
            float currentSqr = (current.Position - zombie.Position).LengthSquared();
            float newSqr = (player.Position - zombie.Position).LengthSquared();
            if (newSqr >= currentSqr)
                return;
        }
        zombie.TargetPlayer = player.Id;
        if (zombie.State is EZombieState.Idle or EZombieState.Return)
            zombie.State = EZombieState.Chase;
    }

    private void Behave(ZombieInstance zombie, IReadOnlyList<ZombiePlayerView> players, float dt)
    {
        if (zombie.AttackCooldown > 0f)
            zombie.AttackCooldown -= dt;

        switch (zombie.State)
        {
            case EZombieState.Chase:
            case EZombieState.Attack:
                if (!TryGetPlayer(players, zombie.TargetPlayer, out ZombiePlayerView target)
                    || LevelNavigationData.TryGetBound(_bounds, target.Position) != zombie.Bound)
                {
                    // Target gone or outside the zombie's territory: give up and walk home,
                    // Unturned's navmesh-bound retreat.
                    zombie.TargetPlayer = byte.MaxValue;
                    zombie.State = EZombieState.Return;
                    return;
                }
                Pursue(zombie, target.Position, dt);
                break;

            case EZombieState.Return:
                MoveTowards(zombie, zombie.Home, dt);
                if (HorizontalDistanceSquared(zombie.Position, zombie.Home) <= ArriveRadius * ArriveRadius)
                    zombie.State = EZombieState.Idle;
                break;
        }
    }

    private void Pursue(ZombieInstance zombie, Vector3 targetPosition, float dt)
    {
        if (HorizontalDistanceSquared(zombie.Position, targetPosition) <= AttackRange * AttackRange)
        {
            Face(zombie, targetPosition);
            zombie.State = EZombieState.Attack;
            if (zombie.AttackCooldown <= 0f)
            {
                zombie.AttackCooldown = AttackTime;
                OnAttack?.Invoke(zombie, zombie.TargetPlayer, _tables[zombie.Type].Damage);
            }
            return;
        }
        zombie.State = EZombieState.Chase;
        MoveTowards(zombie, targetPosition, dt);
    }

    private void MoveTowards(ZombieInstance zombie, Vector3 targetPosition, float dt)
    {
        Face(zombie, targetPosition);
        Vector3 flat = new(targetPosition.X - zombie.Position.X, 0f, targetPosition.Z - zombie.Position.Z);
        float distance = flat.Length();
        if (distance < 1e-4f)
            return;
        float step = MathF.Min(zombie.Speed * dt, distance);
        Vector3 next = zombie.Position + (flat / distance * step);
        if (_ground(next.X, next.Z, out float y))
            next.Y = y;
        zombie.Position = next;
    }

    private static void Face(ZombieInstance zombie, Vector3 targetPosition)
    {
        float dx = targetPosition.X - zombie.Position.X;
        float dz = targetPosition.Z - zombie.Position.Z;
        if ((dx * dx) + (dz * dz) > 1e-8f)
            zombie.Yaw = Mathf.RadToDeg(MathF.Atan2(-dx, -dz));
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dz * dz);
    }

    private static bool TryGetPlayer(IReadOnlyList<ZombiePlayerView> players, byte id, out ZombiePlayerView player)
    {
        foreach (ZombiePlayerView candidate in players)
        {
            if (candidate.Id == id)
            {
                player = candidate;
                return true;
            }
        }
        player = default;
        return false;
    }
}
