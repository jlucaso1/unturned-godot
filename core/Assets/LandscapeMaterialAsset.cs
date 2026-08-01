using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Mirrors SDG.Unturned.LandscapeMaterialAsset: one splat layer of a landscape tile. The texture it names
// lives in a master bundle by container path ("core.masterbundle" + "Terrain/Materials/PEI/Grass_00.png"),
// which is how a map gets its own layer art instead of a fixed set of names.
//
// Level.hierarchy stores eight of these GUIDs per tile, in splatmap layer order, so resolving them is what
// turns "layer 3 of this tile" into a real texture.
public sealed class LandscapeMaterialAsset
{
    public Guid Guid { get; }

    // The bundle the texture lives in ("core.masterbundle", or a workshop map's own bundle) and its path
    // inside it. Empty when the asset ships no texture (a few fallback materials).
    public string TextureBundle { get; }
    public string TexturePath { get; }

    // Optional detail mask, same addressing as the texture.
    public string MaskBundle { get; }
    public string MaskPath { get; }

    // The surface name the footstep/impact audio resolves through (FOLIAGE_STATIC, CONCRETE, ...).
    public string PhysicsMaterial { get; }

    private LandscapeMaterialAsset(Guid guid, string textureBundle, string texturePath,
        string maskBundle, string maskPath, string physicsMaterial)
    {
        Guid = guid;
        TextureBundle = textureBundle;
        TexturePath = texturePath;
        MaskBundle = maskBundle;
        MaskPath = maskPath;
        PhysicsMaterial = physicsMaterial;
    }

    public static bool TryParse(DatDictionary root, out LandscapeMaterialAsset asset)
    {
        asset = null!;

        if (!root.TryGetDictionary("Metadata", out DatDictionary meta) ||
            !root.TryGetDictionary("Asset", out DatDictionary data))
            return false;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return false;

        string type = meta.GetString("Type") ?? string.Empty;
        if (!type.Contains("LandscapeMaterialAsset", StringComparison.Ordinal))
            return false;

        (string textureBundle, string texturePath) = ReadReference(data, "Texture");
        (string maskBundle, string maskPath) = ReadReference(data, "Mask");

        asset = new LandscapeMaterialAsset(guid, textureBundle, texturePath, maskBundle, maskPath,
            data.GetString("Physics_Material") ?? string.Empty);
        return true;
    }

    // A "Texture { Name <bundle> Path <path> }" block; both keys are present but empty when unused.
    private static (string Bundle, string Path) ReadReference(DatDictionary data, string key) =>
        data.TryGetDictionary(key, out DatDictionary reference)
            ? (reference.GetString("Name") ?? string.Empty, reference.GetString("Path") ?? string.Empty)
            : (string.Empty, string.Empty);

    // Every landscape material under a bundle tree, by GUID. The whole set is small (~100 assets), and a
    // map's tiles reference them by GUID alone, so there is nothing to filter on up front.
    public static Dictionary<Guid, LandscapeMaterialAsset> ScanDirectory(string root)
    {
        var result = new Dictionary<Guid, LandscapeMaterialAsset>();
        if (!Directory.Exists(root))
            return result;

        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                DatDictionary parsed;
                try
                {
                    parsed = DatParser.Parse(File.ReadAllText(file));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (TryParse(parsed, out LandscapeMaterialAsset asset))
                    result.TryAdd(asset.Guid, asset);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A workshop item can disappear or become unreadable while its lazy recursive enumeration is
            // in progress. Keep any materials already found and isolate the failure to this source tree.
        }

        return result;
    }

    // ContentSource.Discover orders the core source before workshop sources. Keep that order when the
    // same GUID is present in more than one source: a later mod must not replace the game's material.
    public static void MergeFirstClaimants(IDictionary<Guid, LandscapeMaterialAsset> destination,
        IEnumerable<KeyValuePair<Guid, LandscapeMaterialAsset>> candidates)
    {
        foreach (KeyValuePair<Guid, LandscapeMaterialAsset> candidate in candidates)
            if (!destination.ContainsKey(candidate.Key))
                destination.Add(candidate.Key, candidate.Value);
    }
}
