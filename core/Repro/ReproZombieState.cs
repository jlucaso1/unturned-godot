using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

// The brain state <-> dump record translation, in one place and in both directions. Round-tripping is
// the whole contract: a field that is captured but not restored is a field the replay silently makes
// up, and that is exactly the class of difference that makes a bug "not reproducible here".
public static class ReproZombieState
{
    public static ReproZombieRecord ToRecord(ZombieInstance zombie)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        return new ReproZombieRecord
        {
            Id = zombie.Id,
            Bound = zombie.Bound,
            Type = zombie.Type,
            Speciality = zombie.Speciality,
            IsHyper = zombie.IsHyper,
            Shirt = zombie.Shirt,
            Pants = zombie.Pants,
            Hat = zombie.Hat,
            Gear = zombie.Gear,
            Move = zombie.Move,
            Idle = zombie.Idle,
            Health = zombie.Health,
            MaxHealth = zombie.MaxHealth,
            Home = ReproVector.From(zombie.Home),
            Position = ReproVector.From(zombie.Position),
            Yaw = ReproVector.Round(zombie.Yaw),
            State = zombie.State,
            Target = zombie.TargetPlayer,
            Approach = zombie.Path,
            SinceSwing = ReproVector.Round(zombie.SinceSwing),
            PendingHit = ReproVector.Round(zombie.PendingHit),
            LeaveDelay = ReproVector.Round(zombie.LeaveDelay),
            LeaveTo = ReproVector.From(zombie.LeaveTo),
            RepathTimer = ReproVector.Round(zombie.RepathTimer),
            BlockedRouteTime = ReproVector.Round(zombie.BlockedRouteTime),
            WaypointIndex = zombie.CurrentWaypointIndex,
            TargetReached = zombie.TargetReached,
            PathIsPartial = zombie.PathIsPartial,
            RouteServedAnotherTarget = zombie.RouteServedAnotherTarget,
            RouteEscapingCollision = zombie.RouteEscapingCollision,
            EscapeRouteHasProgress = zombie.EscapeRouteHasProgress,
            CollisionEscapeRoute = zombie.CollisionEscapeRoute,
            Steer = ReproVector.From(zombie.SteerDirection),
            Route = ReproVector.Flatten(zombie.PathPoints),
        };
    }

    public static ZombieInstance FromRecord(ReproZombieRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        // A dump written before health existed carries zero for both, and zero health is DEAD — the
        // replay would start with a population the brain immediately treats as corpses. Absent means
        // "as it spawned", which without the table in hand is whatever MaxHealth says, and when that is
        // absent too the zombie is simply left at full: the old dumps this covers were all taken in a
        // build where nothing could take health off in the first place.
        ushort maxHealth = record.MaxHealth;
        ushort health = record.Health != 0 ? record.Health : maxHealth;
        var zombie = new ZombieInstance
        {
            Health = health,
            MaxHealth = maxHealth,
            Id = (ushort)record.Id,
            Bound = record.Bound,
            Type = record.Type,
            Speciality = record.Speciality,
            IsHyper = record.IsHyper,
            Shirt = record.Shirt,
            Pants = record.Pants,
            Hat = record.Hat,
            Gear = record.Gear,
            Move = record.Move,
            Idle = record.Idle,
            Home = ReproVector.To(record.Home),
            Position = ReproVector.To(record.Position),
            Yaw = record.Yaw,
            State = record.State,
            TargetPlayer = record.Target,
            Path = record.Approach,
            SinceSwing = record.SinceSwing,
            PendingHit = record.PendingHit,
            LeaveDelay = record.LeaveDelay,
            LeaveTo = ReproVector.To(record.LeaveTo),
            RepathTimer = record.RepathTimer,
            BlockedRouteTime = record.BlockedRouteTime,
            CurrentWaypointIndex = record.WaypointIndex,
            TargetReached = record.TargetReached,
            PathIsPartial = record.PathIsPartial,
            RouteServedAnotherTarget = record.RouteServedAnotherTarget,
            RouteEscapingCollision = record.RouteEscapingCollision,
            EscapeRouteHasProgress = record.EscapeRouteHasProgress,
            CollisionEscapeRoute = record.CollisionEscapeRoute,
            SteerDirection = ReproVector.To(record.Steer),
        };
        ReproVector.Unflatten(record.Route, zombie.PathPoints);
        return zombie;
    }

    public static ReproSystemState ToRecord(ZombieSystemState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var record = new ReproSystemState
        {
            DetectTimer = ReproVector.Round(state.DetectTimer),
            RepathCursor = state.RepathCursor,
            RandomState = state.RandomState,
            RandomIsReproducible = state.RandomIsReproducible,
        };
        foreach (ZombieInstance zombie in state.Zombies)
            record.Zombies.Add(ToRecord(zombie));
        foreach (KeyValuePair<byte, int> entry in state.Agro)
            record.Agro.Add(new ReproAgro { Player = entry.Key, Count = entry.Value });
        return record;
    }

    public static ZombieSystemState FromRecord(ReproSystemState record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var state = new ZombieSystemState
        {
            DetectTimer = record.DetectTimer,
            RepathCursor = record.RepathCursor,
            RandomState = record.RandomState,
            RandomIsReproducible = record.RandomIsReproducible,
        };
        foreach (ReproZombieRecord zombie in record.Zombies)
            state.Zombies.Add(FromRecord(zombie));
        foreach (ReproAgro agro in record.Agro)
            state.Agro[agro.Player] = agro.Count;
        return state;
    }

    public static ReproPlayerSample ToSample(in ZombiePlayerView player) => new()
    {
        Id = player.Id,
        Position = ReproVector.From(player.Position),
        Stance = player.Stance,
        Moving = player.Moving,
    };

    public static ZombiePlayerView FromSample(ReproPlayerSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        return new ZombiePlayerView(sample.Id, ReproVector.To(sample.Position), sample.Stance,
            sample.Moving);
    }

    // The nav world a replay rebuilds the brain on. Bounds and tables are small and always travel with
    // the dump; the navmesh triangles are sliced (see ReproCapture), and the flag boxes stay exact so
    // checkNavigation and the player's nav region answer the way they did live.
    public static List<NavBound> ToBounds(ReproWorldData world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var bounds = new List<NavBound>(world.Bounds.Count);
        foreach (ReproNavBound bound in world.Bounds)
            bounds.Add(new NavBound
            {
                Center = ReproVector.To(bound.Center),
                Size = ReproVector.To(bound.Size),
                MaxZombies = bound.MaxZombies,
                SpawnZombies = bound.SpawnZombies,
                HyperAgro = bound.HyperAgro,
            });
        return bounds;
    }

    public static List<ZombieTable> ToTables(ReproWorldData world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var tables = new List<ZombieTable>(world.Tables.Count);
        foreach (ReproZombieTable table in world.Tables)
            tables.Add(new ZombieTable
            {
                Name = table.Name,
                IsMega = table.IsMega,
                Health = table.Health,
                Damage = table.Damage,
            });
        return tables;
    }

    public static List<NavFlag> ToNavFlags(ReproWorldData world)
    {
        ArgumentNullException.ThrowIfNull(world);
        var flags = new List<NavFlag>(world.NavFlags.Count);
        foreach (ReproNavFlag flag in world.NavFlags)
        {
            var vertices = new Vector3[flag.Vertices.Length / 3];
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = new Vector3(flag.Vertices[i * 3], flag.Vertices[(i * 3) + 1],
                    flag.Vertices[(i * 3) + 2]);
            flags.Add(new NavFlag
            {
                Center = ReproVector.To(flag.Center),
                Size = ReproVector.To(flag.Size),
                Vertices = vertices,
                Triangles = (int[])flag.Triangles.Clone(),
            });
        }
        return flags;
    }
}
