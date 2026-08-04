using System;

namespace UnturnedGodot.Assets;

// Maps Unturned's broad object categories onto the more precise physics roles used by this port.
// MEDIUM normally means furniture that a player collides with but a zombie is allowed to shove through.
// Fences are the exception: their authored navmesh still reaches both sides, but the fence itself is a
// continuous character barrier. Publishing them as World makes both the zombie capsule and navmesh
// reconciliation respect that barrier while benches, beds and gravestones retain their original policy.
public static class ObjectCollisionPolicy
{
    public static uint PhysicsLayer(ObjectAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.Type switch
        {
            EObjectType.Resource => CollisionLayers.World,
            EObjectType.Medium => CollisionLayers.MediumFurniture | CollisionLayers.VisionBlocker
                | (IsFence(asset) ? CollisionLayers.World : 0u),
            EObjectType.Large => CollisionLayers.World | CollisionLayers.VisionBlocker,
            _ => 0u,
        };
    }

    private static bool IsFence(ObjectAsset asset) =>
        HasPathSegment(asset.Directory, "Fences")
        || asset.BundleOverridePath is { } overridePath
            && HasPathSegment(overridePath, "Fences");

    private static bool HasPathSegment(string path, string wanted)
    {
        int start = 0;
        for (int i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] is not ('/' or '\\'))
                continue;
            if (i - start == wanted.Length
                && path.AsSpan(start, wanted.Length).Equals(wanted,
                    StringComparison.OrdinalIgnoreCase))
                return true;
            start = i + 1;
        }
        return false;
    }
}
