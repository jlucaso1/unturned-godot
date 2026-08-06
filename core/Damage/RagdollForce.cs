using System;
using Godot;

namespace UnturnedGodot.Damage;

// The shove a dying body takes with it — DamageTool's `ragdoll` vector, and what RagdollTool does to it
// before the body is thrown.
//
// This is the "knockback" a player actually sees from a punch. The stagger (ZombieStun) needs more damage
// than a fist can deal; this fires on the killing blow, whatever dealt it, and is why something killed
// with a heavy hit to the head tumbles away instead of dropping where it stood.
//
// Deliberately about no particular kind of body. RagdollTool has three near-identical entry points —
// ragdollZombie, ragdollAnimal, ragdollPlayer — that differ only in which prefab they instantiate and
// which clothing they apply; the ARITHMETIC below is copied between them verbatim. Only zombies die here
// yet, and when players and animals do, they want this and not a copy of it.
public static class RagdollForce
{
    // damageZombie (and damageAnimal, and damagePlayer): `direction * amount * RagdollForceMultiplier`.
    // The multiplier is a melee asset's own, 1 for anything that does not name one — the punch included.
    //
    // The DAMAGE is in the vector, which is the whole reason a strong hit throws a body further than a
    // weak one, and why a headshot (worth more) shoves harder than a hit to the arm.
    public static Vector3 Impulse(Vector3 direction, ushort amount, float forceMultiplier = 1f)
    {
        if (direction == Vector3.Zero || amount == 0)
            return Vector3.Zero;
        return direction.Normalized() * amount * forceMultiplier;
    }

    // ragdollZombie's own adjustments, before AddForce: lift it, scatter it sideways, and scale the lot.
    //
    // The lift is what turns a shove into a tumble — without it a body slides along the floor. The
    // sideways scatter is why two identical kills never fall the same way. Both are in the original's
    // own units, which are Unity force units against a ragdoll of a particular mass.
    public const float Lift = 8f;
    public const float Scatter = 16f;
    public const float Scale = 32f;

    // The vector handed to AddForce. `sideways` and `forward` are the two random draws, each in
    // [-1, 1] — passed in so this stays pure and a replay can reproduce the same fall.
    public static Vector3 Thrown(Vector3 impulse, float sideways, float forward)
    {
        var adjusted = new Vector3(
            impulse.X + (Math.Clamp(sideways, -1f, 1f) * Scatter),
            impulse.Y + Lift,
            impulse.Z + (Math.Clamp(forward, -1f, 1f) * Scatter));
        return adjusted * Scale;
    }
}
