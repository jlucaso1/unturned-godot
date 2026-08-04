using System;
using Godot;
using UnturnedGodot.Damage;

namespace UnturnedGodot;

// Gives the punch host the two physics queries it cannot make itself, against the SERVER's own world —
// the same static bodies the movement solver and the zombie brain resolve against.
//
// DamageTool.raycast masks RayMasks.DAMAGE_SERVER, which is the solid world plus the props and
// buildables a swing can break. Here that reduces to World (terrain, LARGE objects and resources) plus
// MediumFurniture (the benches, gravestones and rubble piles), and deliberately NOT the player bit: a
// punch has to reach past the thrower's own capsule, which is a metre closer than anything it can hit.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class PunchPhysics
{
    public const uint DamageMask = CollisionLayers.World | CollisionLayers.MediumFurniture;

    public static void Attach(PunchDamageHost host, Func<World3D?> resolveWorld)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resolveWorld);

        host.Resolved += LogPunch;

        World3D? world = null;
        World3D? World() => world ??= resolveWorld();

        // Reused rather than allocated per swing. Punches are rare, but these are RefCounted objects and
        // every other per-tick query in this port owns one for the same reason.
        var ray = new PhysicsRayQueryParameters3D { CollisionMask = DamageMask };

        host.WorldRaycast = (Vector3 origin, Vector3 direction, float maxDistance,
            out Vector3 point, out float distance) =>
        {
            point = Vector3.Zero;
            distance = 0f;
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return false;
            ray.From = origin;
            ray.To = origin + (direction.Normalized() * maxDistance);
            using Godot.Collections.Dictionary hit = space.IntersectRay(ray);
            if (hit.Count == 0)
                return false;
            point = (Vector3)hit["position"];
            distance = (point - origin).Length();
            return true;
        };

        // LineBlocked is deliberately left unset. It exists for a host that can answer "is there a wall
        // between these two points" but cannot cast the punch's own ray — a pure test, or a server with
        // no object collision. Where the cast above works, it already shortens the punch to whatever
        // solid thing it met first, so a zombie behind a wall is out of reach rather than merely
        // occluded, and a second query would only be another chance to disagree with the first.
    }

    // PUNCH_LOG=1 prints what every swing found. The damage model has no HUD in front of it yet — no hit
    // marker, no health bar, no ragdoll — so this is how a session says out loud that a fist connected,
    // and how the numbers can be checked against the game's own without a debugger attached.
    private static void LogPunch(PunchResult result)
    {
        if (!EnvFlag.IsOn(OS.GetEnvironment("PUNCH_LOG"), whenUnset: false))
            return;
        if (!result.Hit.Exists)
        {
            Log.Print($"[punch] player {result.PlayerId} swung and hit nothing");
            return;
        }
        Log.Print($"[punch] player {result.PlayerId} hit {result.Hit.Kind} #{result.Hit.Id} "
            + $"({result.Hit.Limb}) for {result.Amount}"
            + (result.Destroyed ? " — destroyed" : string.Empty));
    }
}
