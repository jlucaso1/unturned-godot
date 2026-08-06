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
internal static class PunchPhysics
{
    public const uint DamageMask = CollisionLayers.World | CollisionLayers.MediumFurniture;

    public static void Attach(PunchDamageHost host, Func<World3D?> resolveWorld,
        UnturnedGodot.Net.NetServer? server = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(resolveWorld);

        host.Resolved += LogPunch;

        // DamageTool.ServerSpawnLegacyImpact: the server is what tells everyone a fist met a surface, the
        // thrower included. Unreliable, exactly as the original sends it — an impact that arrives late is
        // worse than one that never arrives, and the next swing is half a second away.
        //
        // Sent to EVERY connection rather than to those within EffectManager.SMALL of the point: this
        // port's server has no per-connection relevance filter yet, and one datagram per swing is far
        // below the per-tick state stream it rides beside. The client is what decides audibility, through
        // the sound's own 16 m rolloff.
        if (server != null)
            host.Impacted += impact => server.Broadcast(ImpactNetMessages.WriteImpact(impact),
                UnturnedGodot.Net.ESendType.Unreliable);

        // Resolved on every swing rather than cached for the host's lifetime, which is what the zombie
        // brain does — its queries run per zombie per tick and cannot pay a lookup each time. A punch is
        // a handful of events per minute, so the cheaper thing here is the CORRECT thing: a session that
        // rebuilds its world (a level reload) must not keep raycasting against the space it left.
        var ray = new PhysicsRayQueryParameters3D { CollisionMask = DamageMask };

        host.WorldRaycast = (Vector3 origin, Vector3 direction, float maxDistance,
            out Vector3 point, out Vector3 normal, out float distance, out System.Guid asset,
            out string surface) =>
        {
            point = Vector3.Zero;
            normal = Vector3.Up;
            distance = 0f;
            asset = System.Guid.Empty;
            surface = string.Empty;
            PhysicsDirectSpaceState3D? space = resolveWorld()?.DirectSpaceState;
            if (space == null)
                return false;
            ray.From = origin;
            ray.To = origin + (direction.Normalized() * maxDistance);
            using Godot.Collections.Dictionary hit = space.IntersectRay(ray);
            if (hit.Count == 0)
                return false;
            point = (Vector3)hit["position"];
            normal = (Vector3)hit["normal"];
            distance = (point - origin).Length();
            asset = AssetOf(hit);
            surface = SurfaceOf(hit);
            return true;
        };

        // LineBlocked is deliberately left unset. It exists for a host that can answer "is there a wall
        // between these two points" but cannot cast the punch's own ray — a pure test, or a server with
        // no object collision. Where the cast above works, it already shortens the punch to whatever
        // solid thing it met first, so a zombie behind a wall is out of reach rather than merely
        // occluded, and a second query would only be another chance to disagree with the first.
    }

    // Which asset the body a ray struck belongs to, so the punch damages the thing it hit rather than
    // whatever breakable happens to stand nearby. Object collision lives on server-owned bodies that
    // carry the asset's GUID in their name (ObjectCollisionNames), so the name is the identity and
    // terrain or anything else resolves to Guid.Empty.
    //
    // Two shapes of body, because ObjectsBuilder builds either. Ordinarily one InstancedStaticBodies owns
    // every collider and the query's RID says which of its names was struck; under UG_NODE_PHYSICS=1 each
    // collider is its own InstancedStaticBody node, and then the node's own name is the answer. Reading
    // only the batched form left every breakable unpunchable in the other mode — the hit came back
    // unnamed, so the ledger refused it as terrain.
    internal static System.Guid AssetOf(Godot.Collections.Dictionary hit)
    {
        if (!hit.TryGetValue("collider", out Variant colliderValue))
            return System.Guid.Empty;

        string name = colliderValue.As<Node>() switch
        {
            InstancedStaticBodies owner when hit.TryGetValue("rid", out Variant rid) =>
                owner.NameFor(rid.As<Rid>()),
            InstancedStaticBody body => body.Name,
            _ => string.Empty,
        };
        return ObjectCollisionNames.TryParseGuid(name, out System.Guid guid) ? guid : System.Guid.Empty;
    }

    // What the struck surface is made of — PhysicsTool.GetColliderSharedPhysicsMaterialName. The shape
    // index the query reports is what picks the collider out of the body, since one body carries every
    // collider of a building and they are rarely all the same stuff.
    //
    // Two shapes of body again, for the same reason AssetOf reads both. Terrain and anything else the
    // builders did not give a surface answer with the empty string, which is a punch that makes no sound
    // and leaves no mark — the original's own behaviour for a collider with no PhysicMaterial.
    //
    // The TERRAIN is a known gap: PhysicsTool.GetMaterialName answers a "Ground" collider from the splat
    // map rather than from the collider, and the splat sampler lives on the client. Punching the ground
    // is silent here until that is wired through.
    private static string SurfaceOf(Godot.Collections.Dictionary hit)
    {
        if (!hit.TryGetValue("collider", out Variant colliderValue)
            || !hit.TryGetValue("shape", out Variant shapeValue))
        {
            return string.Empty;
        }

        int shape = shapeValue.As<int>();
        return colliderValue.As<Node>() switch
        {
            InstancedStaticBodies owner when hit.TryGetValue("rid", out Variant rid) =>
                owner.MaterialFor(rid.As<Rid>(), shape),
            InstancedStaticBody body => body.MaterialFor(shape),
            _ => string.Empty,
        };
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
