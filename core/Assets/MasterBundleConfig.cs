using System;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// A bundle's MasterBundle.dat: the name assets reference it by ("core.masterbundle") and the Unity folder
// its contents were built from ("Assets/CoreMasterBundle"). Asset references name a bundle and a path
// relative to that folder, so both halves are needed to look a texture or mesh up in the bundle's own
// container table, whose keys are the full lowercase Unity path.
public sealed class MasterBundleConfig
{
    public string BundleName { get; }
    public string AssetPrefix { get; }

    // Where the .masterbundle file sits, so a caller that resolved an asset can open the right bundle.
    public string Directory { get; }

    private MasterBundleConfig(string bundleName, string assetPrefix, string directory)
    {
        BundleName = bundleName;
        AssetPrefix = assetPrefix;
        Directory = directory;
    }

    // Reads <directory>/MasterBundle.dat, or null when the directory holds no bundle config.
    public static MasterBundleConfig? Load(string directory)
    {
        string path = Path.Combine(directory, "MasterBundle.dat");
        if (!File.Exists(path))
            return null;

        DatDictionary root;
        try
        {
            root = DatParser.Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        string name = root.GetString("Asset_Bundle_Name") ?? string.Empty;
        string prefix = root.GetString("Asset_Prefix") ?? string.Empty;
        if (name.Length == 0)
            return null;

        return new MasterBundleConfig(name, prefix, directory);
    }

    // The AssetBundle container key for an asset path relative to the bundle ("Terrain/Materials/PEI/
    // Grass_00.png" -> "assets/coremasterbundle/terrain/materials/pei/grass_00.png"). Unity lowercases
    // and slash-normalizes these keys.
    public string ContainerPath(string assetPath)
    {
        string combined = AssetPrefix.Length > 0
            ? AssetPrefix.TrimEnd('/') + "/" + assetPath.TrimStart('/')
            : assetPath;
        return combined.Replace('\\', '/').ToLowerInvariant();
    }

    // True when an asset reference ("Name core.masterbundle") points at this bundle. Unturned matches
    // these case-insensitively, and tolerates the name being written with or without the extension.
    public bool Matches(string referencedName)
    {
        if (string.IsNullOrEmpty(referencedName))
            return false;
        string configuredBare = WithoutExtension(BundleName);
        string referencedBare = WithoutExtension(referencedName);
        return string.Equals(configuredBare, referencedBare, StringComparison.OrdinalIgnoreCase);
    }

    private static string WithoutExtension(string name) =>
        name.EndsWith(".masterbundle", StringComparison.OrdinalIgnoreCase)
            ? name[..^".masterbundle".Length]
            : name;
}
