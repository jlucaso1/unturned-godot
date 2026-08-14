using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;

namespace UnturnedGodot.Data;

// The set of object GUIDs a map needs meshes for, read straight from its own files. The runtime paths
// (ObjectStreamer, WorldBuilder) already hold the parsed placements and derive this inline; this is for
// callers that only have a map folder — the editor dock answering "is this map's cache complete?" without
// loading the map.
public static class MapAssetSet
{
    // Placed objects + trees + the foliage types that resolve to an asset. Unresolved foliage GUIDs are
    // left out deliberately: nothing can be extracted for them, so counting them would make the cache look
    // permanently incomplete.
    // assetsDirs are searched in order for the foliage types: the game's Assets folder, then any workshop
    // source's. A workshop map keeps its own grass assets beside its bundle, so a core-only scan would
    // leave those GUIDs out and report the cache complete while the map's foliage is missing.
    public static HashSet<Guid> Collect(string mapPath, params string[] assetsDirs) =>
        Collect(mapPath, null, assetsDirs);

    // `db`, when given, also covers the maps that place by legacy id instead of GUID: without it their
    // placements contribute nothing but Guid.Empty, and a caller measuring cache completeness (the editor
    // dock) would call Monolith fully cached while all 1,810 of its trees were still missing.
    public static HashSet<Guid> Collect(string mapPath, ObjectAssetDatabase? db, params string[] assetsDirs)
    {
        var level = new LevelInfo(mapPath);
        var needed = new HashSet<Guid>();

        List<PlacedObject> placements = LevelObjects.Load(level.ObjectsDat);
        IReadOnlyList<PlacedTree> trees = LevelTrees.Load(Path.Combine(level.Path, "Terrain", "Trees.dat"));
        if (db != null)
        {
            // The map's own holiday policy, so the cache this reports on is the one the map would
            // actually load: out of season a Christmas prop is not placed, and its mesh is not needed.
            var holidays = HolidayPolicy.ForMap(level.Path);
            LegacyPlacements.ResolveGuids(placements, db, holidays);
            LegacyPlacements.AppendTrees(trees, placements, db, holidays);
        }
        else
        {
            foreach (PlacedTree t in trees)
                placements.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, t.Id, t.Guid));
        }

        foreach (PlacedObject o in placements)
            needed.Add(o.Guid);

        if (LevelFoliageChunks.ReadAssetGuids(Path.Combine(level.Path, "Foliage.blob")) is { } foliageGuids)
        {
            var wanted = new HashSet<Guid>(foliageGuids);
            foreach (string assetsDir in assetsDirs)
                if (Directory.Exists(assetsDir))
                    foreach (Guid guid in FoliageAsset.ScanForGuids(assetsDir, wanted).Keys)
                        needed.Add(guid);
        }

        needed.Remove(Guid.Empty);
        return needed;
    }
}
