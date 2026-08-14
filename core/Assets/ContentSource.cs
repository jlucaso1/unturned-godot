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
    // The file MasterBundleConfig.Load reads. Named here only so HasAssetDefinition can tell the bundle
    // declaration apart from the asset definitions it is looking for.
    private const string BundleConfigFileName = "MasterBundle.dat";

    // "core.masterbundle", "california2.masterbundle": the name assets reference the bundle by.
    public string Name { get; }

    // Where the bundle and its MasterBundle.dat live (the game's Bundles folder, or the workshop item).
    public string Root { get; }

    // The .masterbundle file for this platform, or "" when the source ships none.
    public string BundlePath { get; }

    public string ObjectsDir { get; }
    public string TreesDir { get; }
    public string AssetsDir { get; }

    // Vehicle assets and their redirectors; their prefabs sit under "vehicles/<folder>" in this bundle.
    public string VehiclesDir { get; }

    // Spawn table assets. Every category's tables share one legacy id namespace, but each lives in its own
    // subfolder, so a consumer scans the subtree it needs rather than the whole tree.
    public string SpawnsDir { get; }

    // NPC character assets. They carry no prefab of their own — an NPC is the player's own rig wearing
    // what its .dat names — but a map places them by GUID like any other object, so they have to be in
    // the asset database for those placements to resolve at all.
    //
    // The Characters subtree specifically, not all of NPCs/: its Dialogues, Quests and Vendors siblings
    // are records with a GUID, an ID and a Type of their own, and nothing places them. Scanned, they
    // parse as Unknown-type assets and take a slot in the object legacy-id table — the game's own
    // T.Rickster_Blackmarket vendor is ID 27, low in the range old maps place objects from.
    public string NpcsDir { get; }

    // The game's own content, as opposed to a workshop item.
    public bool IsCore { get; }

    // What this source's entries are namespaced by in caches keyed on Unity path ids, which are only
    // unique inside one bundle's SerializedFile. See UnturnedGodot.Unity.TextureKey.
    public string CacheTag { get; }

    private ContentSource(string name, string root, string bundlePath, string objectsDir, string treesDir,
        string assetsDir, string vehiclesDir, string spawnsDir, string npcsDir, bool isCore)
    {
        Name = name;
        string nameTag = Unity.TextureKey.TagFor(name);
        // Steam workshop directory names are item ids and therefore stable across platforms and library
        // moves. Bundle names alone are not unique: unrelated items often reuse names such as
        // "shared.masterbundle", while their Unity PathIDs overlap freely.
        CacheTag = isCore ? nameTag : Unity.TextureKey.Discriminate(nameTag,
            Path.GetFileName(Path.TrimEndingDirectorySeparator(root)));
        Root = root;
        BundlePath = bundlePath;
        ObjectsDir = objectsDir;
        TreesDir = treesDir;
        AssetsDir = assetsDir;
        VehiclesDir = vehiclesDir;
        SpawnsDir = spawnsDir;
        NpcsDir = npcsDir;
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
        return IsUnder(full, ObjectsDir) || IsUnder(full, TreesDir) || IsUnder(full, AssetsDir)
            || IsUnder(full, VehiclesDir);
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
                Path.Combine(bundles, "Vehicles"),
                Path.Combine(bundles, "Spawns"),
                Path.Combine(bundles, "NPCs", "Characters"),
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
        string vehicles = Path.Combine(itemDirectory, "Vehicles");
        string spawns = Path.Combine(itemDirectory, "Spawns");
        string npcs = Path.Combine(itemDirectory, "NPCs", "Characters");
        // Asset-only bundles are valid sources too. Foliage, physics materials, landscapes and spawn
        // tables are consumed independently of Objects/Resources, and every one of those scanners starts
        // from Discover's result. Rejecting an item that contains only one of them silently drops
        // otherwise valid content: an item shipping nothing but spawn tables is what a map's own
        // vehicle table resolves through, and dropping it leaves every spawnpoint using it empty.
        //
        // These named folders are only a CHEAP PRE-TEST, not the rule. Each is a directory this class
        // hands out as a property, so a hit here settles the question without touching the tree; the
        // general answer is HasAssetDefinition below.
        bool hasSupportedAssets = Directory.Exists(Path.Combine(assets, "Landscapes"))
            || Directory.Exists(Path.Combine(assets, "Foliage"))
            || Directory.Exists(Path.Combine(assets, "PhysicsMaterials"))
            || Directory.Exists(spawns)
            || Directory.Exists(npcs);
        if (!Directory.Exists(objects) && !Directory.Exists(trees) && !Directory.Exists(vehicles)
            && !hasSupportedAssets && !HasAssetDefinition(itemDirectory))
        {
            return null;
        }

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
            vehicles,
            spawns,
            npcs,
            isCore: false);
    }

    // Does this item ship any asset definition at all, anywhere below its root?
    //
    // A FIXED LIST OF FOLDER NAMES CANNOT ANSWER THIS for the item family. Maps follow the game's own
    // layout because the editor writes it, but item mods name their folders freely, and the game does
    // not care: it hands a subscribed OBJECT/ITEM/VEHICLE item's own directory to the asset worker as a
    // search location (Assets.cs:2030-2033) and the worker walks every subdirectory below it
    // (AssetsWorker.cs:139-177). Of the three items installed here that declare a bundle, one keeps its
    // thirty clothing assets in "Clothes/" and another keeps its items under "Bundles/Items/" — neither
    // name means anything to the game, and neither is a name a whitelist would have guessed. Both were
    // dropped outright before this, which left the clothing armor scan and the difficulty scan with
    // nothing of theirs to read: a zombie wearing that mod's vest took damage as if bare.
    //
    // The criterion is the worker's own: an asset definition is a ".asset" or a ".dat"
    // (AssetsWorker.cs:305-371). MasterBundle.dat is excluded by name because it is the bundle
    // DECLARATION every candidate here has by construction — counting it would make the test vacuous
    // and turn a bundle with no content behind it into a source.
    private static bool HasAssetDefinition(string itemDirectory)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(itemDirectory, "*.dat",
                SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(file).Equals(BundleConfigFileName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (string _ in Directory.EnumerateFiles(itemDirectory, "*.asset",
                SearchOption.AllDirectories))
            {
                return true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An item we cannot read contributes nothing, rather than aborting the whole library scan.
        }

        return false;
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
