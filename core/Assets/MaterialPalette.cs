using System;
using System.Collections.Generic;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Parses a MaterialPaletteAsset (.asset). It lists the materials (by bundle path) a mesh's submeshes
// use, in order. Ported from MaterialPaletteAsset.PopulateAsset.
public sealed class MaterialPalette
{
    public Guid Guid { get; }
    public IReadOnlyList<string> MaterialPaths { get; }

    // The bundle each entry names, parallel to MaterialPaths and "" where the asset gives none. A
    // material reference in Unturned is a bundle plus a path inside it, and only the path was being kept:
    // every palette the game ships says "core.masterbundle" and the graph resolving them is core, so the
    // two agreed and the name looked redundant. A workshop palette naming a different bundle is where
    // they part, and resolving its path against the bundle in hand would find either nothing or, worse,
    // an unrelated material that happens to sit at the same path.
    public IReadOnlyList<string> MaterialBundles { get; }

    private MaterialPalette(Guid guid, List<string> materialPaths, List<string> materialBundles)
    {
        Guid = guid;
        MaterialPaths = materialPaths;
        MaterialBundles = materialBundles;
    }

    public static MaterialPalette? Read(DatDictionary root)
    {
        DatDictionary meta = root.TryGetDictionary("Metadata", out var md) ? md : root;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return null;

        DatDictionary data = root.TryGetDictionary("Asset", out var a) ? a : root;
        var paths = new List<string>();
        var bundles = new List<string>();
        if (data.TryGetList("Materials", out var list))
        {
            foreach (DatNode node in list.Items)
            {
                if (node is not DatDictionary entry || entry.GetString("Path") is not { Length: > 0 } path)
                    continue;
                paths.Add(path);
                // Kept index-parallel with the paths, so an entry without a Name still holds its slot.
                bundles.Add(entry.GetString("Name") ?? string.Empty);
            }
        }

        return new MaterialPalette(guid, paths, bundles);
    }
}
