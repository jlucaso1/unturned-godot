using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Assets;

namespace UnturnedGodot.Damage;

// One placed thing in the level that can be broken, with the numbers off its asset. Trees and rocks are
// resources; destructible props (the garbage piles and rubble the maps scatter) are objects. Two kinds
// rather than two classes because everything downstream treats them identically — a health bar, a blade
// gate and a vulnerability flag — and the kind is only there to pick which of the punch's two numbers
// applies and which of its two gates.
public readonly record struct DamageableInstance(
    EPunchTargetKind Kind,
    Vector3 Position,
    ushort Health,
    bool VulnerableToFists,
    byte BladeId,
    bool IsVulnerable)
{
    // A resource off Bundles/Trees: Health, BladeID and the Vulnerable_To_Fists opt-in.
    public static DamageableInstance Resource(Vector3 position, ObjectAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new DamageableInstance(EPunchTargetKind.Resource, position, asset.Health,
            asset.VulnerableToFists, asset.BladeId, IsVulnerable: true);
    }

    // A destructible object: the rubble block's health, blade id and (negated) invulnerable flag. Fists
    // do not need an opt-in here — the gate is the blade id being zero, which the resolver applies.
    public static DamageableInstance Object(Vector3 position, ObjectAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return new DamageableInstance(EPunchTargetKind.Object, position, asset.RubbleHealth,
            VulnerableToFists: false, asset.RubbleBladeId, asset.RubbleIsVulnerable);
    }

    // Whether it is worth putting in the world at all: something with no health cannot be broken, so
    // carrying it would only make every lookup walk past it.
    public bool IsDamageable => Health > 0;
}

// The level's breakable furniture, and how much of each is left.
//
// Unturned keeps this in ResourceManager and ObjectManager, addressed by Regions' 64x64 grid — a hit
// transform is turned back into (region x, y, index) and the health lives on the spawnpoint. This is the
// same idea at the granularity a punch needs: a uniform grid over the placements, so the point a ray
// stopped at can be turned back into the thing that stopped it, plus one health array beside it.
//
// It is a SERVER-side ledger. Health is authoritative state and the client is never told a number, only
// that something broke — which is the same split the zombies use.
public sealed class DamageableWorld
{
    // How wide a lookup cell is, in metres. Unturned's own regions are far larger (Regions.REGION_SIZE),
    // and they are sized for streaming rather than for a 1.75 m query: a cell that big would make every
    // punch walk hundreds of trees. This is a couple of punch reaches across, so a lookup touches the
    // handful of placements that could plausibly be what was hit.
    public const float CellSize = 16f;

    private readonly List<DamageableInstance> _instances = new();
    private readonly List<ushort> _health = new();
    private readonly Dictionary<(int X, int Z), List<int>> _cells = new();

    public int Count => _instances.Count;

    public DamageableInstance this[int index] => _instances[index];

    // What is left of one placement. An index nobody has damaged still answers its asset's full health.
    public ushort HealthOf(int index) => _health[index];

    public bool IsDestroyed(int index) => _health[index] == 0;

    // Adds a placement and returns the index that names it — the id a PunchHit carries.
    public int Add(in DamageableInstance instance)
    {
        int index = _instances.Count;
        _instances.Add(instance);
        _health.Add(instance.Health);
        (int, int) cell = CellOf(instance.Position);
        if (!_cells.TryGetValue(cell, out List<int>? bucket))
            _cells[cell] = bucket = new List<int>();
        bucket.Add(index);
        return index;
    }

    // The nearest live placement to `point` within `radius`, or -1. This is the port's answer to
    // ResourceManager.tryGetRegion: the physics world says a ray stopped here, and this says what was
    // standing there. Distance is measured to the placement's ORIGIN, which for a tree is the base of
    // its trunk — so the radius has to cover a tree's own height, not merely the ray's slop.
    public int Find(Vector3 point, float radius)
    {
        int best = -1;
        float bestSquared = radius * radius;
        (int cx, int cz) = CellOf(point);
        int span = Math.Max(1, (int)MathF.Ceiling(radius / CellSize));
        for (int x = cx - span; x <= cx + span; x++)
        {
            for (int z = cz - span; z <= cz + span; z++)
            {
                if (!_cells.TryGetValue((x, z), out List<int>? bucket))
                    continue;
                foreach (int index in bucket)
                {
                    if (_health[index] == 0)
                        continue;
                    float squared = (_instances[index].Position - point).LengthSquared();
                    if (squared >= bestSquared)
                        continue;
                    bestSquared = squared;
                    best = index;
                }
            }
        }
        return best;
    }

    // Takes health off one placement, the way ResourceSpawnpoint.askDamage and RubbleInfo.askDamage do:
    // floor at zero, and nothing at all to something already broken. Returns true when THIS hit is what
    // broke it, so the caller announces a destruction once rather than on every further swing.
    public bool Damage(int index, ushort amount)
    {
        if (amount == 0 || _health[index] == 0)
            return false;
        _health[index] = PunchDamageResolver.ApplyToHealth(_health[index], amount);
        return _health[index] == 0;
    }

    // The damage a bare fist does to this particular placement, through the gates its asset carries:
    // a resource has to be Vulnerable_To_Fists, an object's rubble has to want no blade and not be
    // invulnerable. Zero means the swing lands and does nothing, which is what punching a pine does.
    public ushort PunchDamageTo(int index, float times = 1f)
    {
        DamageableInstance instance = _instances[index];
        return instance.Kind switch
        {
            EPunchTargetKind.Resource => PunchDamageResolver.Resource(instance.VulnerableToFists, times),
            EPunchTargetKind.Object =>
                PunchDamageResolver.Object(instance.BladeId, instance.IsVulnerable, times),
            _ => 0,
        };
    }

    // Puts everything back to full, which is what a resource's Reset timer eventually does per
    // placement. Nothing schedules that yet; this exists so a session that reloads a level does not
    // carry the previous one's felled trees.
    public void ResetAll()
    {
        for (int i = 0; i < _health.Count; i++)
            _health[i] = _instances[i].Health;
    }

    private static (int X, int Z) CellOf(Vector3 position) =>
        ((int)MathF.Floor(position.X / CellSize), (int)MathF.Floor(position.Z / CellSize));
}
