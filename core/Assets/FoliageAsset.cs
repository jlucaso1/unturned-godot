using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Mirrors SDG.Framework.Foliage.FoliageInstancedMeshInfoAsset: a foliage .asset naming the masterbundle
// mesh and material to instance (by container path), plus the shadow and draw-distance settings. These
// are the grass/flower/pebble types the Foliage.blob scatters.
public sealed class FoliageAsset
{
    public Guid Guid { get; }
    public string MeshPath { get; }      // e.g. "Terrain/Foliage/Grass/Grass_00_Mesh.fbx"
    public string MaterialPath { get; }  // e.g. "Terrain/Foliage/Grass/PEI/Grass_00_Material.mat"
    public bool CastShadows { get; }
    public int DrawDistance { get; }     // -1 uses the global foliage draw distance

    private FoliageAsset(Guid guid, string meshPath, string materialPath, bool castShadows, int drawDistance)
    {
        Guid = guid;
        MeshPath = meshPath;
        MaterialPath = materialPath;
        CastShadows = castShadows;
        DrawDistance = drawDistance;
    }

    public static bool TryParse(DatDictionary root, out FoliageAsset asset)
    {
        asset = null!;

        // Foliage assets are always the v2 layout: identity under Metadata, fields under Asset.
        if (!root.TryGetDictionary("Metadata", out DatDictionary meta) ||
            !root.TryGetDictionary("Asset", out DatDictionary data))
            return false;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return false;

        string type = meta.GetString("Type") ?? string.Empty;
        if (!type.Contains("FoliageInstancedMeshInfoAsset", StringComparison.Ordinal))
            return false;

        if (!data.TryGetDictionary("Mesh", out DatDictionary mesh) ||
            mesh.GetString("Path") is not { Length: > 0 } meshPath)
            return false;

        string materialPath = data.TryGetDictionary("Material", out DatDictionary mat)
            ? mat.GetString("Path") ?? string.Empty
            : string.Empty;
        bool castShadows = string.Equals(data.GetString("Cast_Shadows"), "true", StringComparison.OrdinalIgnoreCase);
        int drawDistance = int.TryParse(data.GetString("Draw_Distance"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int d) ? d : -1;

        asset = new FoliageAsset(guid, meshPath, materialPath, castShadows, drawDistance);
        return true;
    }

    // A foliage type, paired with the index of the ContentSource whose bundle carries its mesh.
    public readonly record struct Owned(FoliageAsset Asset, int SourceIndex);

    // Resolves the foliage types a map scatters across EVERY installed source, not just the game's own.
    // A workshop map ships its grass, flower and pebble assets next to its own bundle; scanning only the
    // core Assets folder leaves those GUIDs unresolved, so they never enter the needed set, their meshes
    // are never extracted, and the map renders with no foliage at all.
    //
    // Sources are searched in order (the game first) and the first to declare a GUID keeps it, which is
    // what the original does — an asset whose GUID is already registered is rejected.
    public static Dictionary<Guid, Owned> ScanSources(IReadOnlyList<ContentSource> sources, ISet<Guid> needed)
    {
        var owned = new Dictionary<Guid, Owned>();
        for (int i = 0; i < sources.Count; i++)
            foreach (KeyValuePair<Guid, FoliageAsset> entry in ScanForGuids(sources[i].AssetsDir, needed))
                if (!owned.ContainsKey(entry.Key))
                    owned[entry.Key] = new Owned(entry.Value, i);

        return owned;
    }

    // Scans a bundle tree for foliage .asset files, keeping those whose GUID the map actually uses.
    public static Dictionary<Guid, FoliageAsset> ScanForGuids(string root, ISet<Guid> needed)
    {
        var result = new Dictionary<Guid, FoliageAsset>();
        if (!Directory.Exists(root))
            return result;

        foreach (string file in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
        {
            DatDictionary parsed;
            try { parsed = DatParser.Parse(File.ReadAllText(file)); }
            catch (IOException) { continue; }

            if (TryParse(parsed, out FoliageAsset asset) && needed.Contains(asset.Guid))
                result[asset.Guid] = asset;
        }
        return result;
    }
}
