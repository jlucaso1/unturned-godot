using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// Everything a map's placements resolve to, once. `Objects` has had its trees folded in, its legacy ids
// rewritten to GUIDs and its NPCs taken out; `NeededGuids` is what the extraction plan and the mesh and
// collider libraries are then keyed on.
//
// `Db` is echoed back rather than only taken as an argument so a caller can hold the whole result in one
// value — every consumer needs the database again to colour a fallback box or to ask an asset's type.
// `LegacyResolved` is a count rather than a log line because each caller prefixes its own ([server],
// [stream], [unturned-godot]).
public sealed record LevelContent(
    List<PlacedObject> Objects,
    List<PlacedObject> Vehicles,
    List<PlacedObject> Npcs,
    ObjectAssetDatabase Db,
    IReadOnlyDictionary<Guid, FoliageAsset.Owned> FoliageAssets,
    HashSet<Guid> NeededGuids,
    int LegacyResolved);

// The one order in which a map's placements resolve.
//
// This used to be written out four times — the interactive build, the dedicated server's collision-only
// build, the streamer and the editor preview — and the fourth copy had already drifted into three visible
// bugs: it never partitioned the NPCs (so the preview drew Russia's forty characters as boxes and chased
// their GUIDs through the extraction plan on every load), it built the needed set from the raw placement
// GUIDs instead of the database's resolution (so the documented stale-GUID-to-legacy-id fallback never
// ran), and it took foliage GUIDs into the needed set without resolving them to assets first. The other
// three stayed in step only because someone kept near-identical explanatory comments in each of them.
//
// The order is the whole reason this is one function:
//
//   * the trees are appended only after the asset database exists, because a pre-GUID tree is placed by an
//     id from the RESOURCE namespace and only the database can tell it from the identically-numbered
//     object (see LegacyPlacements). That is enforced structurally here: `db` is a parameter, so it
//     cannot not exist yet.
//   * the vehicles are rolled after the asset scan, which they resolve their redirectors through.
//   * the NPCs leave the object list BEFORE the needed set is built. They resolve to the player rig, not
//     to an extracted mesh, so a GUID left in is one the extraction plan chases on every load and
//     ObjectsBuilder then draws as a placeholder box (see NpcPlacements).
//   * the needed set comes from ObjectAssetDatabase.ResolvePlacementGuids, not from the placements' own
//     GUIDs, because that is what applies the stale-modern-GUID-to-legacy-id fallback and rewrites the
//     placement to match.
//   * only foliage types that RESOLVED to an asset join the needed set. An unresolved GUID has nothing to
//     extract, so counting it as needed reports the cache cold on every boot.
//
// Deliberately free of threading, and deliberately split so a caller can still overlap what it used to.
// Four things go into a resolve and three of them are independent reads: the placement files
// (ReadPlacements), the asset database (ContentExtraction.ScanAssets), and the foliage the blob names
// (ResolveFoliage). Each caller decides WHEN to run those — the streamer on its own tasks, the
// synchronous builds inline — and none of them decides what to do with the results. That is the whole of
// the split: a first version did the reads inside the resolve, which read beautifully and quietly turned
// the streamer's three parallel reads into one sequential chain on the cold-load path.
public static class LevelContentPlan
{
    // Trees are Unturned "resources", placed through a separate file from the objects. Every caller
    // derived this path itself; the map layout is as much a rule as the ordering is.
    public static string TreesDat(LevelInfo level) => Path.Combine(level.Path, "Terrain", "Trees.dat");

    // The map's placement files, straight off disk and otherwise untouched. Depends on nothing but the
    // map folder — not on the asset database, not on the foliage — so a caller that can overlap it with
    // those should. Handed to Resolve rather than read inside it for exactly that reason: on a workshop
    // map this is a hundred thousand placements, and a cold load that waits for the asset scan before it
    // starts reading them pays the sum of the two instead of the longer one.
    public readonly record struct LevelPlacements(List<PlacedObject> Objects, List<PlacedTree> Trees);

    public static LevelPlacements ReadPlacements(LevelInfo level) =>
        new(LevelObjects.Load(level.ObjectsDat), LevelTrees.Load(TreesDat(level)));

    // Which foliage types the map's blob actually resolves to an asset. Also independent of the asset
    // database and of the placements, so it belongs in the same parallel phase. `foliageGuids` is what
    // the caller read out of Foliage.blob — through the residency index, the chunked loader or the header
    // alone, which is a streaming decision and not this one's. Null means "this build wants no foliage",
    // and is not the same as an empty list.
    //
    // Across every source, not just the core Assets folder: a workshop map's own grass and pebble assets
    // live next to its bundle, and scanning core alone leaves them unresolved — no needed GUID, no
    // extraction, no foliage on the map.
    public static IReadOnlyDictionary<Guid, FoliageAsset.Owned> ResolveFoliage(
        IReadOnlyList<ContentSource> sources, IReadOnlyList<Guid>? foliageGuids) =>
        foliageGuids != null
            ? FoliageAsset.ScanSources(sources, new HashSet<Guid>(foliageGuids))
            : new Dictionary<Guid, FoliageAsset.Owned>();

    // For a caller with nothing to overlap. The only place the three steps are composed, so a caller that
    // does have something to overlap runs the same pieces in its own order rather than its own sequence.
    public static LevelContent Resolve(IReadOnlyList<ContentSource> sources, LevelInfo level,
        ObjectAssetDatabase db, IReadOnlyList<Guid>? foliageGuids, HolidayPolicy holidays) =>
        Resolve(sources, level, db, ReadPlacements(level), ResolveFoliage(sources, foliageGuids), holidays);

    // `holidays` is required, and deliberately has no default. It used to default to the map's own answer,
    // which read well and was wrong twice over: the editor must not apply holiday redirects at all (see
    // WorldPreview), and a silent default is exactly what let that regress without the compiler asking.
    // Every caller now says which calendar it is building against, in one visible place. The stakes are
    // higher than what gets drawn — the placement list is what DamageableWorld indexes into, so two
    // builds of one map that disagree about the date disagree about the indices too.
    public static LevelContent Resolve(IReadOnlyList<ContentSource> sources, LevelInfo level,
        ObjectAssetDatabase db, LevelPlacements placements,
        IReadOnlyDictionary<Guid, FoliageAsset.Owned> foliageAssets, HolidayPolicy holidays)
    {
        HolidayPolicy holiday = holidays;
        List<PlacedObject> objects = placements.Objects;
        List<PlacedTree> trees = placements.Trees;

        // A pre-GUID map names its objects and trees by legacy id; everything below is keyed on GUIDs, so
        // those placements borrow theirs from the database first — and the trees become placements here,
        // resolved through the resource namespace rather than the object one. This is also where the
        // holiday rules run: an asset carrying Holiday_Restriction does not exist outside its holiday, so
        // both lists SHRINK here as well as being rewritten.
        int legacyResolved = LegacyPlacements.ResolveGuids(objects, db, holiday)
            + LegacyPlacements.AppendTrees(trees, objects, db, holiday);

        // A spawned vehicle is a GUID and a transform like any other placement, so its mesh joins the same
        // needed set, the same extraction plan and the same batching — only the scene root is its own.
        List<PlacedObject> vehicles = VehicleSpawnPlan.Load(level, sources, db);

        List<PlacedObject> npcs = NpcPlacements.Partition(objects, db);

        HashSet<Guid> needed = db.ResolvePlacementGuids(objects);
        foreach (PlacedObject vehicle in vehicles)
            needed.Add(vehicle.Guid);
        foreach (Guid guid in foliageAssets.Keys)
            needed.Add(guid);
        // ResolvePlacementGuids never contributes one, but a vehicle or foliage asset declared without a
        // GUID would. Nothing downstream can request the empty GUID from a bundle or a cache, so it is not
        // a need — it is a miss that would be re-counted on every load.
        needed.Remove(Guid.Empty);

        return new LevelContent(objects, vehicles, npcs, db, foliageAssets, needed, legacyResolved);
    }
}
