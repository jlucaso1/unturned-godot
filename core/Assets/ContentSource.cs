using System;
using System.Collections.Generic;
using System.IO;

namespace UnturnedGodot.Assets;

// One place object/tree assets and the master bundle holding their prefabs live together: the game's own
// Bundles folder, or a workshop item that ships a MasterBundle.dat next to its Objects/Resources trees.
//
// Workshop maps place objects from their mod's bundle, not the game's, so extraction has to run once per
// source: the prefab keys inside a bundle are relative to that bundle's own asset folders.
public sealed class ContentSource
{
    // "core.masterbundle", "california2.masterbundle": the name assets reference the bundle by.
    public string Name { get; }

    // Where the bundle and its MasterBundle.dat live (the game's Bundles folder, or the workshop item).
    public string Root { get; }

    // The .masterbundle file for this platform, or "" when the source ships none.
    public string BundlePath { get; }

    public string ObjectsDir { get; }
    public string TreesDir { get; }
    public string AssetsDir { get; }

    // The game's own content, as opposed to a workshop item.
    public bool IsCore { get; }

    // What this source's entries are namespaced by in caches keyed on Unity path ids, which are only
    // unique inside one bundle's SerializedFile. See UnturnedGodot.Unity.TextureKey.
    public string CacheTag { get; }

    private ContentSource(string name, string root, string bundlePath, string objectsDir, string treesDir,
        string assetsDir, bool isCore)
    {
        Name = name;
        CacheTag = Unity.TextureKey.TagFor(name);
        Root = root;
        BundlePath = bundlePath;
        ObjectsDir = objectsDir;
        TreesDir = treesDir;
        AssetsDir = assetsDir;
        IsCore = isCore;
    }

    // True when an asset directory belongs to this source, which is how a scanned asset is traced back to
    // the bundle its content lives in. Assets/ counts as well as Objects/ and Resources/: the physics
    // materials and landscapes under it name audio and textures packaged in this same bundle, and
    // recognising only the prefab folders sent a workshop surface's footstep audio to the game's bundle,
    // where it does not exist.
    public bool Owns(string directory)
    {
        string full = Path.GetFullPath(directory);
        return IsUnder(full, ObjectsDir) || IsUnder(full, TreesDir) || IsUnder(full, AssetsDir);
    }

    private static bool IsUnder(string path, string root)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), path);
        return !relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }

    // The game first, then every subscribed workshop item that ships a bundle, newest-id order aside.
    public static IReadOnlyList<ContentSource> Discover(string installRoot) =>
        Discover(installRoot, UnturnedInstall.CurrentPlatform);

    public static IReadOnlyList<ContentSource> Discover(string installRoot, UnturnedInstall.Platform platform)
    {
        var sources = new List<ContentSource>();

        string bundles = Path.Combine(installRoot, "Bundles");
        if (Directory.Exists(bundles))
        {
            sources.Add(new ContentSource(
                MasterBundleConfig.Load(bundles)?.BundleName ?? "core.masterbundle",
                bundles,
                UnturnedInstall.FindBundle(bundles, "core", platform) ?? "",
                Path.Combine(bundles, "Objects"),
                Path.Combine(bundles, "Trees"),
                Path.Combine(bundles, "Assets"),
                isCore: true));
        }

        foreach (string item in WorkshopItems(installRoot))
            if (ReadWorkshopItem(item, platform) is { } source)
                sources.Add(source);

        return sources;
    }

    // A workshop item is a content source when it declares a bundle and ships assets to go with it.
    // Mods that are pure map or localization items have no MasterBundle.dat and are skipped.
    public static ContentSource? ReadWorkshopItem(string itemDirectory, UnturnedInstall.Platform platform)
    {
        MasterBundleConfig? config = MasterBundleConfig.Load(itemDirectory);
        if (config == null)
            return null;

        string objects = Path.Combine(itemDirectory, "Objects");
        string trees = Path.Combine(itemDirectory, "Resources"); // the game's Trees folder, mod-side name
        string assets = Path.Combine(itemDirectory, "Assets");
        // Asset-only bundles are valid sources too. Foliage, physics materials and landscapes are consumed
        // independently of Objects/Resources, and every one of those scanners starts from Discover's
        // result. Rejecting an item that contains only one of them silently drops otherwise valid content.
        bool hasSupportedAssets = Directory.Exists(Path.Combine(assets, "Landscapes"))
            || Directory.Exists(Path.Combine(assets, "Foliage"))
            || Directory.Exists(Path.Combine(assets, "PhysicsMaterials"));
        if (!Directory.Exists(objects) && !Directory.Exists(trees) && !hasSupportedAssets)
            return null;

        string baseName = config.BundleName.EndsWith(".masterbundle", StringComparison.OrdinalIgnoreCase)
            ? config.BundleName[..^".masterbundle".Length]
            : config.BundleName;

        return new ContentSource(
            config.BundleName,
            itemDirectory,
            UnturnedInstall.FindBundle(itemDirectory, baseName, platform) ?? "",
            objects,
            trees,
            assets,
            isCore: false);
    }

    // Materialized inside the try rather than handed back as a lazy sequence. .NET's enumerator opens the
    // directory in its constructor today, so the throw does land in this catch — but that is an
    // implementation detail, not a contract, and a listing this small costs nothing to read eagerly.
    private static IReadOnlyList<string> WorkshopItems(string installRoot)
    {
        string? workshop = UnturnedInstall.WorkshopContentDirectory(installRoot);
        if (workshop == null || !Directory.Exists(workshop))
            return Array.Empty<string>();

        try
        {
            return new List<string>(Directory.EnumerateDirectories(workshop));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>(); // unreadable library: no workshop content rather than no game
        }
    }
}
