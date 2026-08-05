using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Damage;

// DamageTool.raycast for a fist: what the 1.75 m ray in front of the eyes hits first.
//
// The original casts once against RayMasks.DAMAGE_SERVER and reads the kind off the collider it struck.
// Here the ray has to be answered from two different places, because the two kinds of target live in
// different worlds: zombies are not registered with the physics server at all (the brain sweeps a
// capsule and never installs a body), while trees, rubble and buildings are ordinary static geometry.
// So both are asked, and the NEAREST answer wins — which is what a single cast would have returned.
public static class PunchTargeting
{
    // How far HORIZONTALLY from a placement's origin a hit on that placement's own asset may land. A
    // trunk is struck a few metres up and slightly off its centre line, and a wide rubble pile spreads
    // further still, so this is measured in the width of the thing rather than in the ray's precision —
    // but it is not a search radius: the asset identity has already narrowed the candidates to other
    // copies of the SAME prop, and this only picks which copy.
    public const float PlacementMatchRadius = 4f;

    // Blocks a punch on world geometry between the eyes and a zombie: the fist cannot reach through a
    // wall. Same shape as the brain's own PhysicalLineBlocked, so a host installs one predicate for
    // both. Null is an unobstructed world, which is what the pure tests run in.
    public delegate bool LineBlocked(Vector3 from, Vector3 to);

    // Where the physics world stops the ray, if it does: the point, how far along it, and WHICH ASSET
    // the body it struck belongs to. That last one is what turns a point into an identification — a
    // punch that stops on a wall beside a garbage pile must not be attributed to the pile — and it is
    // Guid.Empty for anything the host cannot name (terrain, a ladder volume), which is a miss.
    //
    // A host backs this with a real cast against the solid world; null means nothing solid is known,
    // and then only zombies can be hit.
    public delegate bool WorldRaycast(Vector3 origin, Vector3 direction, float maxDistance,
        out Vector3 point, out float distance, out System.Guid asset);

    // The nearest zombie the ray enters, with the limb it entered at. Walks the population rather than
    // a broadphase because the caller has already narrowed it — a punch is one ray per swing, and swings
    // are rare — and because the ONLY correct answer is the nearest one, which needs every candidate
    // compared anyway.
    public static bool RaycastZombies(IReadOnlyList<ZombieInstance> zombies, Vector3 origin,
        Vector3 direction, float maxDistance, out PunchHit hit, LineBlocked? blocked = null)
    {
        ArgumentNullException.ThrowIfNull(zombies);
        hit = PunchHit.None;
        float nearest = maxDistance;
        for (int i = 0; i < zombies.Count; i++)
        {
            ZombieInstance zombie = zombies[i];
            if (zombie.IsDead)
                continue;
            if (!ZombieHitbox.Raycast(zombie, origin, direction, nearest, out float distance,
                    out ELimb limb))
                continue;
            Vector3 point = origin + (direction.Normalized() * distance);
            // Checked only for the candidate that would win, and only once it has: the predicate is a
            // physics query, and running one per zombie in the region would make a punch cost more than
            // the tick it happens on.
            if (blocked != null && blocked(origin, point))
                continue;
            nearest = distance;
            hit = new PunchHit(EPunchTargetKind.Zombie, zombie.Id, limb, point, distance);
        }
        return hit.Exists;
    }

    // The whole cast: whichever of the zombie population and the solid world the ray meets first.
    //
    // A world hit is only a TARGET if something breakable stands there; otherwise it is a wall, and the
    // punch lands on nothing. Either way it still shortens the ray, which is what stops a fist reaching
    // a zombie through a fence.
    public static PunchHit Resolve(Vector3 origin, Vector3 direction, float maxDistance,
        IReadOnlyList<ZombieInstance> zombies, DamageableWorld? world = null,
        WorldRaycast? worldRaycast = null, LineBlocked? blocked = null)
    {
        ArgumentNullException.ThrowIfNull(zombies);
        if (direction == Vector3.Zero || maxDistance <= 0f)
            return PunchHit.None;
        direction = direction.Normalized();

        PunchHit best = PunchHit.None;
        float reach = maxDistance;

        if (worldRaycast != null
            && worldRaycast(origin, direction, maxDistance, out Vector3 point, out float distance,
                out System.Guid asset)
            && distance <= reach)
        {
            reach = distance;
            int index = world?.Find(point, PlacementMatchRadius, asset) ?? -1;
            if (index >= 0)
                best = new PunchHit(world![index].Kind, index, ELimb.Spine, point, distance);
        }

        // The zombie cast is bounded by whatever the world stopped at, so a zombie standing behind a
        // wall the ray already hit is out of reach rather than merely further away.
        if (RaycastZombies(zombies, origin, direction, reach, out PunchHit zombieHit, blocked))
            best = zombieHit;

        return best;
    }

    // "(info.point - player.look.aim.position).sqrMagnitude > 36": the server's own ceiling on how far
    // away a punch may have landed, whatever produced the hit.
    public static bool IsWithinServerRange(Vector3 origin, Vector3 point) =>
        (point - origin).LengthSquared() <= PunchDamageData.MaxServerHitDistanceSquared;
}
