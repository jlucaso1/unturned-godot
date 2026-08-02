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

    private MaterialPalette(Guid guid, List<string> materialPaths)
    {
        Guid = guid;
        MaterialPaths = materialPaths;
    }

    public static MaterialPalette? Read(DatDictionary root)
    {
        DatDictionary meta = root.TryGetDictionary("Metadata", out var md) ? md : root;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return null;

        DatDictionary data = root.TryGetDictionary("Asset", out var a) ? a : root;
        var paths = new List<string>();
        if (data.TryGetList("Materials", out var list))
            foreach (DatNode node in list.Items)
                if (node is DatDictionary entry && entry.GetString("Path") is { Length: > 0 } path)
                    paths.Add(path);

        return new MaterialPalette(guid, paths);
    }
}
