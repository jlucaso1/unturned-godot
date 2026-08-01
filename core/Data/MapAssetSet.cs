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
    public static HashSet<Guid> Collect(string mapPath, params string[] assetsDirs)
    {
        var level = new LevelInfo(mapPath);
        var needed = new HashSet<Guid>();

        foreach (PlacedObject o in LevelObjects.Load(level.ObjectsDat))
            needed.Add(o.Guid);
        foreach (PlacedTree t in LevelTrees.Load(Path.Combine(level.Path, "Terrain", "Trees.dat")))
            needed.Add(t.Guid);

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
