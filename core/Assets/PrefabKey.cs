using System;
using System.IO;

namespace UnturnedGodot.Assets;

// Where an asset's content sits inside its bundle: the lowercase folder path the AssetBundle container
// addresses it by, without the bundle's own "assets/<name>masterbundle/" prefix.
//
// "objects/small/business/cardboard_0" is the key its object.prefab, its colliders and its decal.png all
// hang off. Deriving it from the asset's own directory is what keeps every layout working, the game's and
// a workshop mod's alike — the game files its harvestables under Trees/ while a mod may use Resources/.
public static class PrefabKey
{
    // An explicit override wins over all of that, whatever the category: Bundle_Override_Path is a field
    // of Unturned's base Asset, not of ObjectAsset, and a holiday variant that reuses a base asset's
    // prefab uses it the same way from either folder. Trees/Ornament_0_XMAS is the official one — it
    // declares "/Trees/Ornament_0", and deriving its key from its own folder instead left the ornament on
    // PEI, Washington and Yukon with no mesh at all.
    public static string For(ObjectAsset asset, ContentSource source)
    {
        if (asset.BundleOverridePath is { Length: > 0 } overridePath)
            return Normalized(overridePath);
        if (IsUnder(asset.Directory, source.TreesDir))
            return Root(source.TreesDir) + "/" + Folder(asset.Directory, source.TreesDir);
        if (IsUnder(asset.Directory, source.VehiclesDir))
            return Root(source.VehiclesDir) + "/" + Folder(asset.Directory, source.VehiclesDir);
        return Root(source.ObjectsDir) + "/" + Folder(asset.Directory, source.ObjectsDir);
    }

    private static bool IsUnder(string directory, string root) =>
        !Path.GetRelativePath(root, directory).StartsWith("..", StringComparison.Ordinal);

    private static string Folder(string directory, string bundlesDir) =>
        Path.GetRelativePath(bundlesDir, directory).Replace('\\', '/').ToLowerInvariant();

    // The bundle-side name of an asset folder: Bundles/Objects -> "objects", <mod>/Resources ->
    // "resources". This is the first segment of every prefab key built from that folder.
    private static string Root(string bundlesDir) =>
        Path.GetFileName(Path.TrimEndingDirectorySeparator(bundlesDir)).ToLowerInvariant();

    private static string Normalized(string overridePath) =>
        overridePath.Replace('\\', '/').Trim('/').ToLowerInvariant(); // already "objects/..."
}
