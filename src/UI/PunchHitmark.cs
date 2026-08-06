using System;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Damage;
using UnturnedGodot.UI;

namespace UnturnedGodot;

// The client's own raycast, for the hitmarker alone — the first half of PlayerEquipment.punch, the part
// guarded by `if (channel.IsLocalPlayer)`.
//
// The server decides damage and always will; this decides only whether the crosshair flashes, and it runs
// locally because a mark that waits a round trip does not read as feedback. A disagreement between the two
// costs a wrong mark and nothing else, which is the trade the original makes too.
//
// The ladder below is the original's, in its order: a body first (entity, critical for a skull), then the
// breakables (build). Everything else is no mark at all — punching a wall flashes nothing, even though it
// makes a sound and leaves a decal, because the crosshair is telling you whether you HURT something.
public sealed class PunchHitmark
{
    // Resolved per swing rather than captured. The hit test is built with the player, and on the
    // streaming path the asset scan finishes several seconds later — a value captured at construction is
    // null for the whole session, and the crosshair never marks a breakable.
    private readonly Func<ObjectAssetDatabase?> _assets;
    private readonly Func<ZombiesView?> _zombies;
    private readonly Func<World3D?> _world;

    // Reused across swings; a punch is a handful of events per minute but this is still the object a
    // query would otherwise allocate each time.
    private readonly PhysicsRayQueryParameters3D _ray = new()
    {
        // DamageTool.raycast masks RayMasks.DAMAGE_CLIENT. Same reduction PunchPhysics makes for the
        // server: the solid world plus medium furniture, and never the player's own capsule.
        CollisionMask = PunchPhysics.DamageMask,
    };

    public PunchHitmark(Func<ObjectAssetDatabase?> assets, Func<ZombiesView?> zombies,
        Func<World3D?> world)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _zombies = zombies ?? throw new ArgumentNullException(nameof(zombies));
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    // What the crosshair should flash for a swing from `origin` along `forward`.
    public EPlayerHit Resolve(Vector3 origin, Vector3 forward)
    {
        if (forward == Vector3.Zero)
            return EPlayerHit.None;
        forward = forward.Normalized();

        float reach = PunchDamageData.Reach;

        // The world first, only to shorten the ray: a fist cannot reach a zombie through a fence.
        Guid asset = Guid.Empty;
        bool struckWorld = false;
        PhysicsDirectSpaceState3D? space = _world()?.DirectSpaceState;
        if (space != null)
        {
            _ray.From = origin;
            _ray.To = origin + (forward * reach);
            using Godot.Collections.Dictionary hit = space.IntersectRay(_ray);
            if (hit.Count > 0)
            {
                reach = ((Vector3)hit["position"] - origin).Length();
                asset = PunchPhysics.AssetOf(hit);
                struckWorld = true;
            }
        }

        // A zombie inside that shortened reach wins, exactly as the server's own resolution does.
        if (_zombies() is { } zombies && zombies.RaycastAvatars(origin, forward, reach, out ELimb limb))
            return Hitmarker.ForLimb(limb);

        // Otherwise the world hit is a mark only if a fist could hurt what it struck. The asset's own
        // gates are the same ones PunchDamageResolver applies: a resource has to opt in through
        // Vulnerable_To_Fists, and an object's rubble has to want no blade and not be invulnerable.
        if (!struckWorld || asset == Guid.Empty || _assets()?.ResolveByGuid(asset) is not { } placed)
            return EPlayerHit.None;

        return CanFistDamage(placed) ? EPlayerHit.Build : EPlayerHit.None;
    }

    // Whether a bare hand can hurt this asset at all. Deliberately asks the ASSET rather than the
    // placement: the client has no health ledger, and the original's own client-side check is the same
    // shape — it reads the asset's vulnerability flags and marks, leaving whether the thing is already
    // dead to the server.
    public static bool CanFistDamage(ObjectAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Type == EObjectType.Resource)
            return asset.VulnerableToFists && asset.Health > 0;
        return asset.RubbleIsVulnerable && asset.RubbleBladeId == 0 && asset.RubbleHealth > 0;
    }
}
