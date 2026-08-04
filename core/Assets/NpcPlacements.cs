using System.Collections.Generic;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// An NPC character is placed like any other object — a GUID and a transform in Objects.dat — but it
// resolves to nothing the mesh pipeline can extract: `Bundles/NPCs/Characters/<name>/Asset.dat` names the
// clothing and face Unturned dresses its ordinary player rig in, and ships no prefab.
//
// So the two have to part company before the object build: an NPC left in the list is a GUID the
// extraction plan would chase forever and the renderer would draw as a placeholder box.
public static class NpcPlacements
{
    // Takes the NPC placements out of `placements` and returns them, leaving everything else in place and
    // in order. Returns an empty list — and leaves `placements` untouched — for a map that places none,
    // which is every official map except Russia.
    public static List<PlacedObject> Partition(List<PlacedObject> placements, ObjectAssetDatabase db)
    {
        var npcs = new List<PlacedObject>();
        var kept = new List<PlacedObject>(placements.Count);
        foreach (PlacedObject placement in placements)
        {
            if (db.Resolve(placement.Guid, placement.Id) is { Type: EObjectType.Npc })
                npcs.Add(placement);
            else
                kept.Add(placement);
        }

        if (npcs.Count == 0)
            return npcs;

        placements.Clear();
        placements.AddRange(kept);
        return npcs;
    }
}
