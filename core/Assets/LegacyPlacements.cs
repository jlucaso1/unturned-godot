using System;
using System.Collections.Generic;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// Maps written before GUIDs existed (Objects.dat < 8, Trees.dat < 6) identify what they place by the
// asset's legacy ushort id alone. Everything downstream of the loaders keys on the GUID — the extraction
// plan, the mesh library, the collider library, the per-GUID batching — so an id-only placement has to
// borrow its asset's GUID before it gets there, or it renders as a placeholder box forever.
//
// Which id table to look in is not a detail: Unturned's EAssetType keeps OBJECT and RESOURCE ids in
// separate namespaces and they collide constantly (69 of the game's own resource ids also name an
// object). Monolith's 1,810 trees are placed with eight ids, and every one of them names an object too —
// resolving those through the object table renders a forest of houses. So objects and trees resolve
// through their own namespace here, and a tree is converted into a placement only once it has.
public static class LegacyPlacements
{
    // Rewrites every object placement that carries no GUID but whose legacy id resolves. Returns how many
    // were rewritten; placements that already have one are left exactly as they were.
    public static int ResolveGuids(List<PlacedObject> placements, ObjectAssetDatabase db)
    {
        int resolved = 0;
        for (int i = 0; i < placements.Count; i++)
        {
            PlacedObject placement = placements[i];
            if (placement.Guid != Guid.Empty || placement.Id == 0)
                continue;

            if (db.ResolveById(placement.Id) is not { } asset || asset.Guid == Guid.Empty)
                continue;

            placements[i] = new PlacedObject(placement.Position, placement.EulerDegrees, placement.Scale,
                placement.Id, asset.Guid);
            resolved++;
        }
        return resolved;
    }

    // Appends the map's trees to its object placements, resolving any that carry only a legacy id through
    // the resource namespace. Returns how many needed that resolution. Trees are placed exactly like
    // objects once they have a GUID — same transform, same asset database, same mesh pipeline — so this
    // is where the two lists become one.
    public static int AppendTrees(IReadOnlyList<PlacedTree> trees, List<PlacedObject> placements,
        ObjectAssetDatabase db)
    {
        int resolved = 0;
        foreach (PlacedTree tree in trees)
        {
            Guid guid = tree.Guid;
            if (guid == Guid.Empty && tree.Id != 0
                && db.ResolveResourceById(tree.Id) is { Guid: var resourceGuid } && resourceGuid != Guid.Empty)
            {
                guid = resourceGuid;
                resolved++;
            }

            placements.Add(new PlacedObject(tree.Position, tree.EulerDegrees, tree.Scale, tree.Id, guid));
        }
        return resolved;
    }
}
