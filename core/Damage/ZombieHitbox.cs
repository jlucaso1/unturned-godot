using System;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Damage;

// Where on a zombie a hit landed, as an ELimb.
//
// Unturned answers this with geometry: the zombie prefab carries a collider per bone, and the limb is
// whichever one the ray struck (RaycastInfo reads it off the collider's Limb component). This port's
// zombies are not in the physics world at all — the brain sweeps a capsule and never registers a body —
// so the limb is derived from WHERE on that capsule the hit is instead: how high up it landed, and which
// side of the zombie's own centre line.
//
// That is an approximation, and it is the honest one available: it reproduces the property the damage
// table actually depends on — a fist to the head is worth 16.5 and a fist to a leg 4.5 — without
// pretending to a per-bone precision no collider here can supply. The bands are laid out against the 2 m
// body ZombieBody describes, in fractions of it, so a mega's larger capsule scales with it.
public static class ZombieHitbox
{
    // Fractions of the body's height. The skull band is the head; below it the torso, which is arms as
    // well once the hit is far enough off the centre line; and the lower half is legs, with the bottom
    // sixth being feet.
    public const float SkullBand = 0.8f;
    public const float TorsoBand = 0.45f;
    public const float FootBand = 0.15f;

    // How far off the centre line, as a fraction of the capsule's radius, a torso hit stops being the
    // spine and becomes an arm. The arms hang outside the shoulders, so a hit that lands near the middle
    // of the silhouette is the chest and one that lands at its edge is a limb.
    public const float ArmInset = 0.55f;

    // And where within the torso band the arm gives way to the hand — the hand is the lower half of it.
    public const float HandBand = 0.62f;

    // The capsule the zombie occupies, as the mover sweeps it: standing on the zombie's feet, as tall as
    // ZombieBody's top, and as thick as the instance's own radius (megas are wider).
    public static float HeightOf(ZombieInstance zombie)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        // The mega's body is the same capsule scaled by its radius, which is what the client scales its
        // avatar by too (Zombie.cs's spawn scale). Deriving it keeps the two from drifting.
        float scale = zombie.Radius / BakedNavGraph.AgentRadius;
        return ZombieBody.CapsuleTop * MathF.Max(1f, scale);
    }

    // The way the zombie faces, in the yaw convention the brain uses throughout: the model looks along
    // (-sin yaw, 0, -cos yaw).
    public static Vector3 ForwardOf(ZombieInstance zombie)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        float yaw = Mathf.DegToRad(zombie.Yaw);
        return new Vector3(-MathF.Sin(yaw), 0f, -MathF.Cos(yaw));
    }

    // The limb a world-space `point` on (or in) the zombie corresponds to.
    public static ELimb LimbAt(ZombieInstance zombie, Vector3 point)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        // HeightOf is never zero — it is ZombieBody's own capsule, scaled up for a mega — so the
        // division needs no guard, and a point above or below the body clamps to the nearest band
        // rather than being refused: a ray that entered the capsule landed SOMEWHERE on it.
        float fraction = Math.Clamp((point.Y - zombie.Position.Y) / HeightOf(zombie), 0f, 1f);

        // Which side: the component of the hit along the zombie's own right vector. Right is forward
        // rotated a quarter turn about up, which for (-sin, 0, -cos) is (-cos, 0, sin).
        float yaw = Mathf.DegToRad(zombie.Yaw);
        var right = new Vector3(-MathF.Cos(yaw), 0f, MathF.Sin(yaw));
        Vector3 offset = point - zombie.Position;
        float lateral = new Vector3(offset.X, 0f, offset.Z).Dot(right);
        bool isRight = lateral >= 0f;

        if (fraction >= SkullBand)
            return ELimb.Skull;

        if (fraction >= TorsoBand)
        {
            if (MathF.Abs(lateral) < zombie.Radius * ArmInset)
                return ELimb.Spine;
            if (fraction >= HandBand)
                return isRight ? ELimb.RightArm : ELimb.LeftArm;
            return isRight ? ELimb.RightHand : ELimb.LeftHand;
        }

        if (fraction >= FootBand)
            return isRight ? ELimb.RightLeg : ELimb.LeftLeg;
        return isRight ? ELimb.RightFoot : ELimb.LeftFoot;
    }

    // Ray against the zombie's capsule. `distance` is how far along `direction` it was entered, and
    // `limb` what the entry point corresponds to.
    public static bool Raycast(ZombieInstance zombie, Vector3 origin, Vector3 direction, float maxDistance,
        out float distance, out ELimb limb)
    {
        ArgumentNullException.ThrowIfNull(zombie);
        limb = ELimb.Spine;
        if (!Hitscan.RayCapsule(origin, direction, maxDistance, zombie.Position, HeightOf(zombie),
                zombie.Radius, out distance, out Vector3 point))
            return false;
        limb = LimbAt(zombie, point);
        return true;
    }
}
