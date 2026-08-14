using System;
using System.Collections.Generic;
using Godot;
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
//
// This is also where the rest of what a placement owes its asset is settled, because this is the first
// point that HAS the asset. Unturned does the same three things during the load itself:
//
//   * holiday SUBSTITUTION (LevelObjects.cs:620, LevelGround.cs:717) — an asset naming a
//     Christmas_Redirect or Halloween_Redirect is swapped for the one it names, and a placement whose
//     redirect resolves to nothing is dropped outright, so the list itself changes shape by season;
//   * holiday RESTRICTION (LevelObject.cs:428, ResourceSpawnpoint.cs:539) — an asset carrying
//     Holiday_Restriction does not exist outside its holiday, neither drawn nor collidable;
//   * a tree's TRANSFORM (ResourceSpawnpoint.cs:484, LevelGround.cs:782) — the vertical offset that
//     buries the root ball, and, for a Trees.dat older than version 8, the deterministic lean and scale
//     the file never stored.
public static class LegacyPlacements
{
    // Rewrites every object placement that carries no GUID but whose legacy id resolves, and applies the
    // holiday rules. Returns how many were rewritten BY ID; placements that already have a GUID keep it.
    // The list also shrinks: a placement the holiday hides or fails to substitute is removed.
    public static int ResolveGuids(List<PlacedObject> placements, ObjectAssetDatabase db) =>
        ResolveGuids(placements, db, HolidayPolicy.FromClock());

    public static int ResolveGuids(List<PlacedObject> placements, ObjectAssetDatabase db,
        HolidayPolicy holidays)
    {
        var redirects = new Dictionary<Guid, ObjectAsset?>();
        int resolved = 0;
        int write = 0;
        for (int read = 0; read < placements.Count; read++)
        {
            PlacedObject placement = placements[read];
            Guid guid = placement.Guid;
            ushort id = placement.Id;

            // The substitution runs on the GUID the FILE carries, before the legacy id below is allowed
            // to supply one — the order Unturned reads them in. It has a sharp edge worth naming: an
            // id-only placement enters here as Guid.Empty, nothing resolves Guid.Empty, and so during a
            // holiday every id-only object on a redirect-enabled map is dropped. That is the game's own
            // behaviour and not a defect introduced here (LevelObjects.cs:1275 finds the original asset
            // by GUID and only by GUID). It is also unreachable in practice: id-only means an Objects.dat
            // older than version 8, and Allow_Holiday_Redirects lives in a Config.json that maps of that
            // age do not have, so those maps never turn substitution on in the first place.
            if (holidays.AllowRedirects)
            {
                if (Substitute(db, redirects, guid, holidays.Active, AssetFamily.Object)
                    is not { } substitute)
                {
                    continue;
                }
                // "id = redirect.id; GUID = redirect.GUID" — assigned unconditionally, because the map
                // hands back the ORIGINAL asset for anything it does not substitute, and the game takes
                // that asset's id either way.
                guid = substitute.Guid;
                id = substitute.Id;
            }

            if (guid == Guid.Empty && id != 0
                && db.ResolveById(id) is { Guid: var byId } && byId != Guid.Empty)
            {
                guid = byId;
                resolved++;
            }

            if (IsHiddenByHoliday(db.Resolve(guid, id), holidays.Active))
                continue;

            placements[write++] = guid == placement.Guid && id == placement.Id
                ? placement
                : new PlacedObject(placement.Position, placement.EulerDegrees, placement.Scale, id, guid);
        }

        placements.RemoveRange(write, placements.Count - write);
        return resolved;
    }

    // Appends the map's trees to its object placements, resolving any that carry only a legacy id through
    // the resource namespace. Returns how many needed that resolution. Once a tree has a GUID it is
    // placed through the same asset database and the same mesh pipeline as an object, which is why the
    // two lists become one here — but it does NOT share an object's transform, and that is the whole of
    // finding one: the point in Trees.dat is where the trunk meets the ground, not where the model's
    // origin goes.
    public static int AppendTrees(IReadOnlyList<PlacedTree> trees, List<PlacedObject> placements,
        ObjectAssetDatabase db) =>
        AppendTrees(trees, placements, db, HolidayPolicy.FromClock());

    public static int AppendTrees(IReadOnlyList<PlacedTree> trees, List<PlacedObject> placements,
        ObjectAssetDatabase db, HolidayPolicy holidays)
    {
        var redirects = new Dictionary<Guid, ObjectAsset?>();
        int resolved = 0;
        foreach (PlacedTree tree in trees)
        {
            Guid guid = tree.Guid;
            ushort id = tree.Id;

            // Same order as the objects above, and the same drop: LevelGround.cs:757 clears the GUID when
            // the redirect resolves to nothing and skips the tree, so a forest genuinely thins out in
            // December on a map whose pines redirect to snowy ones the install is missing.
            if (holidays.AllowRedirects)
            {
                if (Substitute(db, redirects, guid, holidays.Active, AssetFamily.Resource)
                    is not { } substitute)
                {
                    continue;
                }
                guid = substitute.Guid;
                id = substitute.Id;
            }

            // The jitter asset is looked up BY GUID ONLY, deliberately. Unturned reaches for it with
            // `Assets.find(guid)` (LevelGround.cs:869) while the legacy id is not resolved until the
            // ResourceSpawnpoint constructor runs, several lines later — so a tree from a Trees.dat older
            // than version 6, which has no GUID at all, finds nothing and keeps identity and unit scale.
            // Monolith's 1,810 trees are exactly that case and stand dead straight in the real game too;
            // Alpha Valley's version 6 file does carry GUIDs, and every one of its trees leans.
            Vector3 euler = tree.EulerDegrees;
            Vector3 scale = tree.Scale;
            if (tree.NeedsLegacyRotationAndScale && db.ResolveByGuid(guid) is { } jitterAsset)
                jitterAsset.GetLegacyRotationAndScale(tree.Position, out euler, out scale);

            if (guid == Guid.Empty && id != 0
                && db.ResolveResourceById(id) is { Guid: var resourceGuid } && resourceGuid != Guid.Empty)
            {
                guid = resourceGuid;
                resolved++;
            }

            // ResourceSpawnpoint's own lookup: the GUID when there is one, the RESOURCE id when there is
            // not. Both the offset and the restriction below read this asset, not the jitter one.
            ObjectAsset? asset = guid != Guid.Empty
                ? db.ResolveByGuid(guid)
                : id != 0 ? db.ResolveResourceById(id) : null;

            if (IsHiddenByHoliday(asset, holidays.Active))
                continue;

            // ResourceSpawnpoint.cs:484: `point + Vector3.up * scale.y * asset.verticalOffset`. Scaled by
            // the tree's own Y, so a big pine sinks further than a sapling of the same species — and
            // taken from the asset rather than from the -0.75 default, because the two mushrooms invert
            // the sign and would otherwise be planted 0.85 m underground. A tree whose asset is missing
            // is left where the file put it: there is no offset to apply without one.
            Vector3 position = tree.Position;
            if (asset != null)
                position = new Vector3(position.X, position.Y + (scale.Y * asset.VerticalOffset), position.Z);

            placements.Add(new PlacedObject(position, euler, scale, id, guid));
        }
        return resolved;
    }

    // areConditionsMet, as far as a placement list can carry it. Unturned re-evaluates this at runtime,
    // because a holiday can begin while a server is up; here it is settled once at load, which is the
    // same answer for any session that does not run across midnight on the 20th of October.
    private static bool IsHiddenByHoliday(ObjectAsset? asset, ENPCHoliday active) =>
        asset is { HolidayRestriction: not ENPCHoliday.None }
        && !HolidayUtil.IsHolidayActive(asset.HolidayRestriction, active);

    // TreeRedirectorMap.redirect / LegacyObjectRedirectorMap.redirect, which are the same method twice.
    // Memoized per load exactly as they are — a map places the same few hundred GUIDs thousands of times.
    //
    // The three outcomes are worth keeping distinct, and returning the ASSET is what keeps them in one
    // value the way the game's own method does: null means "drop this placement", the ORIGINAL asset
    // means "nothing to substitute for this holiday, keep it", and any other asset is the swap. Both of
    // the ways a lookup can come up empty — an unknown original and an unknown target — collapse into
    // the single null the game treats them as.
    private static ObjectAsset? Substitute(ObjectAssetDatabase db, Dictionary<Guid, ObjectAsset?> memo,
        Guid guid, ENPCHoliday active, AssetFamily required)
    {
        if (memo.TryGetValue(guid, out ObjectAsset? cached))
            return cached;

        ObjectAsset? result = null;
        if (Matching(db.ResolveByGuid(guid), required) is { } original)
        {
            Guid target = original.GetHolidayRedirect(active);
            // "Does not have a redirect for this event, so use the original tree." A named target that
            // resolves to nothing is the drop instead: Unturned logs "Missing holiday redirect" and
            // leaves the asset null, which the loader reads as "skip this placement".
            //
            // The TARGET is type-checked as well as the original, and that is not belt-and-braces. The
            // game resolves it through AssetReference<ResourceAsset>/<ObjectAsset>, which is typed: a
            // tree whose Christmas_Redirect names an object, a vehicle or an NPC comes back unresolved
            // and the tree is dropped. Checking only the original would let that placement through and
            // then hand an unrelated asset a tree's transform, vertical offset and mesh.
            result = target == Guid.Empty ? original : Matching(db.ResolveByGuid(target), required);
        }

        memo[guid] = result;
        return result;
    }

    // Which of Unturned's asset CLASSES a redirect is allowed to land on. This port keeps resources,
    // vehicles and objects in one table tagged by EObjectType, where the game has ResourceAsset,
    // VehicleAsset and ObjectAsset as separate types — so the distinction the typed AssetReference makes
    // for free has to be made explicitly here.
    private enum AssetFamily
    {
        // ObjectAsset proper: the editor categories Small/Medium/Large plus NPC and Decal, which are
        // ObjectAssets in the game too. Vehicles and their redirectors are not, and neither are
        // resources. Unknown is excluded as well: an unrecognised Type is not evidence of an object.
        Object,

        // ResourceAsset: the harvestables under Bundles/Trees.
        Resource,
    }

    private static ObjectAsset? Matching(ObjectAsset? asset, AssetFamily required) => asset switch
    {
        null => null,
        { Type: EObjectType.Resource } => required == AssetFamily.Resource ? asset : null,
        {
            Type: EObjectType.Small or EObjectType.Medium or EObjectType.Large or EObjectType.Npc
            or EObjectType.Decal
        } => required == AssetFamily.Object ? asset : null,
        // Vehicle, VehicleRedirector and Unknown belong to neither family.
        _ => null,
    };
}
