using System;
using System.Collections.Generic;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// Maps written before GUIDs existed (Objects.dat < 8, Trees.dat < 6) identify what they place by the
// asset's legacy ushort id alone. Everything downstream of the loaders keys on the GUID — the extraction
// plan, the mesh library, the collider library, the per-GUID batching — so an id-only placement has to
// borrow its asset's GUID before it gets there, or it renders as a placeholder box forever.
//
// Monolith's 1,810 trees are the official map this covers; old workshop maps place objects the same way.
public static class LegacyPlacements
{
    // Rewrites every placement that carries no GUID but whose legacy id resolves. Returns how many were
    // rewritten; placements that already have one are left exactly as they were.
    public static int ResolveGuids(List<PlacedObject> placements, ObjectAssetDatabase db)
    {
        int resolved = 0;
        for (int i = 0; i < placements.Count; i++)
        {
            PlacedObject placement = placements[i];
            if (placement.Guid != Guid.Empty || placement.Id == 0)
                continue;

            ObjectAsset? asset = db.ResolveById(placement.Id);
            if (asset == null || asset.Guid == Guid.Empty)
                continue;

            placements[i] = new PlacedObject(placement.Position, placement.EulerDegrees, placement.Scale,
                placement.Id, asset.Guid);
            resolved++;
        }
        return resolved;
    }
}
