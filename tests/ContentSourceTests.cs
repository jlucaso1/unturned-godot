using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class ContentSourceTests
{
    private const string CoreConfig = """
        Asset_Bundle_Name core.masterbundle
        Asset_Prefix Assets/CoreMasterBundle
        Asset_Bundle_Version 6
        """;

    private const string ModConfig = """
        Asset_Bundle_Name california2.masterbundle
        Asset_Prefix Assets/CaliforniaMasterBundle
        Asset_Bundle_Version 4
        """;

    // A Steam library laid out like the real one: <root>/steamapps/common/Unturned plus workshop items.
    private static string BuildLibrary(TempDir dir)
    {
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "MasterBundle.dat"), CoreConfig);
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "core_linux.masterbundle"),
            new byte[] { 1 });
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "Objects", "Cardboard",
            "Cardboard.dat"), "GUID 2e698a7b85e94c019b3f91ec8796a961\nType Small\nID 57\n");
        Directory.CreateDirectory(Path.Combine(install, "Bundles", "Trees"));
        return install;
    }

    private static void AddMod(TempDir dir, string itemId, string config, string bundleFile)
    {
        string item = Path.Combine("steamapps", "workshop", "content", "304930", itemId);
        dir.Write(Path.Combine(item, "MasterBundle.dat"), config);
        dir.Write(Path.Combine(item, bundleFile), new byte[] { 1 });
        dir.Write(Path.Combine(item, "Objects", "CA_Sign", "CA_Sign.dat"),
            "GUID 0517b7a03b844929856fc4f72701fca9\nType Medium\n");
    }

    [Fact]
    public void Discover_FindsTheGameAndEveryWorkshopBundle()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        AddMod(dir, "3711646503", ModConfig, "california2_linux.masterbundle");
        // A map-only item (no bundle) and a mod item with a bundle but no assets: neither is a source.
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "3707778928", "Map.meta"), "x");
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "999", "MasterBundle.dat"),
            "Asset_Bundle_Name empty.masterbundle\n");

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);

        Assert.Equal(2, sources.Count);
        Assert.True(sources[0].IsCore);
        Assert.Equal("core.masterbundle", sources[0].Name);
        Assert.EndsWith("core_linux.masterbundle", sources[0].BundlePath, StringComparison.Ordinal);
        Assert.False(sources[1].IsCore);
        Assert.Equal("california2.masterbundle", sources[1].Name);
        Assert.EndsWith("california2_linux.masterbundle", sources[1].BundlePath, StringComparison.Ordinal);
        Assert.EndsWith("Resources", sources[1].TreesDir, StringComparison.Ordinal); // mod-side Trees
    }

    [Fact]
    public void WorkshopItemsWithTheSameBundleNameHaveDistinctCacheTags()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        AddMod(dir, "100", ModConfig, "california2_linux.masterbundle");
        AddMod(dir, "200", ModConfig, "california2_linux.masterbundle");

        IReadOnlyList<ContentSource> mods = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);

        Assert.Equal(3, mods.Count);
        Assert.NotEqual(mods[1].CacheTag, mods[2].CacheTag);
        Assert.StartsWith("california2-", mods[1].CacheTag);
        Assert.Equal("core", mods[0].CacheTag);
    }

    [Fact]
    public void Discover_PicksTheBundleForThePlatform()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        AddMod(dir, "1", ModConfig, "california2.masterbundle");      // the Windows build
        dir.Write(Path.Combine("steamapps", "workshop", "content", "304930", "1",
            "california2_mac.masterbundle"), new byte[] { 1 });

        ContentSource mod = ContentSource.Discover(install, UnturnedInstall.Platform.Mac)[1];
        Assert.EndsWith("california2_mac.masterbundle", mod.BundlePath, StringComparison.Ordinal);

        // On Linux neither variant is native, so the unsuffixed one is accepted.
        ContentSource onLinux = ContentSource.Discover(install, UnturnedInstall.Platform.Linux)[1];
        Assert.EndsWith("california2.masterbundle", onLinux.BundlePath, StringComparison.Ordinal);
    }

    // A map mod may ship nothing but its terrain layers. Terrain-layer discovery only looks at the sources
    // Discover returns, so rejecting such an item for having no Objects/Resources left its custom landscape
    // materials unfindable and the map fell back to flat splat colors.
    [Fact]
    public void Discover_AcceptsAnItemThatOnlyShipsLandscapeAssets()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5000");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, "Assets", "Landscapes", "CA_Dirt", "CA_Dirt.asset"),
            "Metadata { GUID 4d2f3f0f7b8e4a2b9c1d5e6f70819243 }\n");

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);

        ContentSource mod = Assert.Single(sources, s => !s.IsCore);
        Assert.EndsWith(Path.Combine("5000", "Assets"), mod.AssetsDir, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Foliage")]
    [InlineData("PhysicsMaterials")]
    public void Discover_AcceptsOtherSupportedAssetOnlyBundles(string assetKind)
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5002");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, "Assets", assetKind, "Custom.asset"), "Metadata {}\n");

        ContentSource mod = Assert.Single(ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux), source => !source.IsCore);

        Assert.EndsWith(Path.Combine("5002", "Assets"), mod.AssetsDir, StringComparison.Ordinal);
    }

    // A vehicle mod, and an item that adds nothing but spawn tables. The tables a map's vehicle table
    // resolves through are found only on the sources Discover returns, so rejecting either left every
    // spawnpoint using them empty.
    [Theory]
    [InlineData("Vehicles")]
    [InlineData("Spawns")]
    public void Discover_AcceptsAnItemThatOnlyShipsVehiclesOrSpawnTables(string tree)
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5003");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, tree, "CA_Car", "CA_Car.dat"), "GUID x\nType Vehicle\n");

        ContentSource mod = Assert.Single(ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux), source => !source.IsCore);

        Assert.EndsWith(Path.Combine("5003", "Vehicles"), mod.VehiclesDir, StringComparison.Ordinal);
        Assert.EndsWith(Path.Combine("5003", "Spawns"), mod.SpawnsDir, StringComparison.Ordinal);
    }

    // A vehicle asset belongs to the item that ships it, so its prefab is looked for in that item's
    // bundle rather than the game's.
    [Fact]
    public void Owns_ClaimsTheItemsOwnVehicleTree()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5004");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, "Vehicles", "CA_Car", "CA_Car.dat"), "GUID x\nType Vehicle\n");

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);
        ContentSource core = Assert.Single(sources, source => source.IsCore);
        ContentSource mod = Assert.Single(sources, source => !source.IsCore);

        Assert.True(mod.Owns(Path.Combine(mod.VehiclesDir, "CA_Car")));
        Assert.False(core.Owns(Path.Combine(mod.VehiclesDir, "CA_Car")));
        Assert.True(core.Owns(Path.Combine(core.VehiclesDir, "Offroader")));
    }

    // Item mods do not follow the game's folder layout, and nothing makes them: the loader hands the
    // item's own directory to the asset worker and recurses. "Clothes" is what the admin-tools item
    // installed on this machine calls its clothing folder and "Bundles/Items" is where a stacking mod
    // puts its items; neither is a name a whitelist would have guessed, and both were dropped outright
    // before this, leaving the clothing armor scan with none of their assets to read.
    [Theory]
    [InlineData("Clothes")]
    [InlineData("Bundles/Items/Drinks")]
    [InlineData("Assets/Zombie_Difficulty")]
    public void Discover_AcceptsAnItemWhoseAssetsSitOutsideEveryKnownFolder(string folder)
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5005");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, Path.Combine(folder.Split('/')), "Thing", "Thing.dat"),
            "GUID 8a1c0f5d6b2e4738a9f0c1d2e3b4a596\nType Vest\nID 30000\n");

        ContentSource mod = Assert.Single(ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux), source => !source.IsCore);

        Assert.EndsWith(Path.Combine("304930", "5005"), mod.Root, StringComparison.Ordinal);
    }

    // ".asset" counts as well as ".dat" — a difficulty asset, a landscape or a vehicle redirector is
    // written in that form, and the worker looks for both (AssetsWorker.cs:305-371).
    [Fact]
    public void Discover_AcceptsAnItemWhoseOnlyContentIsALooseAssetFile()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5006");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "Custom", "Hard.asset"),
            "Metadata { GUID 2b7d4e1a9c3f45608172a3b4c5d6e7f8 }\n");

        Assert.Single(ContentSource.Discover(install, UnturnedInstall.Platform.Linux),
            source => !source.IsCore);
    }

    // Still not a source: a bundle declaration with no content of any kind behind it.
    [Fact]
    public void Discover_RejectsAnItemWithNoContentAtAll()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5001");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        // An Assets folder that carries no Landscapes subfolder is not content either.
        dir.Write(Path.Combine(item, "Assets", "readme.txt"), "x");

        Assert.True(ContentSource.Discover(install, UnturnedInstall.Platform.Linux)[0].IsCore);
        Assert.Single(ContentSource.Discover(install, UnturnedInstall.Platform.Linux));
    }

    // An item whose tree cannot be walked contributes nothing, rather than aborting the library scan and
    // costing the player every other mod plus the game itself.
    [Fact]
    public void Discover_ItemWithAnUnreadableTree_IsSkippedWithoutThrowing()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only

        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5007");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "Clothes", "Vest", "Vest.dat"), "GUID x\nType Vest\nID 30000\n");
        string locked = Path.Combine(dir.Path, item, "Clothes");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            Directory.EnumerateFiles(locked).GetEnumerator().MoveNext();
            return; // running as root: modes do not apply
        }
        catch (UnauthorizedAccessException)
        {
            // expected
        }

        try
        {
            ContentSource core = Assert.Single(ContentSource.Discover(install,
                UnturnedInstall.Platform.Linux));
            Assert.True(core.IsCore);
        }
        finally
        {
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ...but ONE denied subtree must not hide the readable assets beside it.
    //
    // This is the case the test above cannot catch, because there everything the item ships is behind
    // the lock and dropping it is right. Here the item has content the scan can read AND an unrelated
    // directory it cannot list — a permission a mod author set, a partial download, a directory owned by
    // another user. Directory.EnumerateFiles(..., AllDirectories) aborts its whole enumeration on the
    // first such child, so the readable sibling is never reached and the item reads as shipping nothing:
    // a source silently lost to a folder that had nothing to do with it. SafeFileTree isolates the denied
    // subtree instead, which is what every other optional content walk in this repository does.
    [Fact]
    public void Discover_OneDeniedSubtreeDoesNotHideTheAssetsBesideIt()
    {
        if (!PosixPermissions.AreEnforced)
            return;

        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        string item = Path.Combine("steamapps", "workshop", "content", "304930", "5008");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        // Readable, and enough on its own to make this item a source.
        dir.Write(Path.Combine(item, "Custom", "Hard.asset"),
            "Metadata { GUID 3c8e5f2b0d4a56719283b4c5d6e7f8a9 }\n");
        dir.Write(Path.Combine(item, "Private", "Secret", "notes.dat"), "nothing to see\n");
        string locked = Path.Combine(dir.Path, item, "Private");
        File.SetUnixFileMode(locked, UnixFileMode.None);

        try
        {
            Assert.Single(ContentSource.Discover(install, UnturnedInstall.Platform.Linux),
                source => !source.IsCore);
        }
        finally
        {
            File.SetUnixFileMode(locked,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void Owns_TracesAnAssetDirectoryBackToItsBundle()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        AddMod(dir, "1", ModConfig, "california2_linux.masterbundle");

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install,
            UnturnedInstall.Platform.Linux);

        Assert.True(sources[0].Owns(Path.Combine(install, "Bundles", "Objects", "Cardboard")));
        Assert.False(sources[0].Owns(Path.Combine(sources[1].ObjectsDir, "CA_Sign")));
        Assert.True(sources[1].Owns(Path.Combine(sources[1].ObjectsDir, "CA_Sign")));
        Assert.False(sources[1].Owns(Path.Combine(install, "Bundles", "Objects", "Cardboard")));
        Assert.False(sources[1].Owns(dir.Path)); // a parent directory is not owned

        // Assets/ too: a physics material or landscape under it names audio and textures packaged in
        // that same source's bundle, and the audio extraction resolves the bundle through this.
        Assert.True(sources[1].Owns(Path.Combine(sources[1].AssetsDir, "PhysicsMaterials", "CA")));
        Assert.True(sources[0].Owns(Path.Combine(install, "Bundles", "Assets", "Landscapes")));
        Assert.False(sources[0].Owns(Path.Combine(sources[1].AssetsDir, "PhysicsMaterials")));
    }

    [Fact]
    public void Discover_WithoutAnInstallOrWorkshop_YieldsNothingAndDoesNotThrow()
    {
        using var dir = new TempDir();
        Assert.Empty(ContentSource.Discover(Path.Combine(dir.Path, "nope"),
            UnturnedInstall.Platform.Linux));

        // An install that is not inside a Steam library still yields its own bundles.
        dir.Write(Path.Combine("Standalone", "Bundles", "Objects", "X", "X.dat"), "GUID x\n");
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(Path.Combine(dir.Path, "Standalone"),
            UnturnedInstall.Platform.Linux);
        ContentSource core = Assert.Single(sources);
        Assert.True(core.IsCore);
        Assert.Equal("core.masterbundle", core.Name); // no MasterBundle.dat: the default name
        Assert.Equal("", core.BundlePath);
    }

    [Fact]
    public void ReadWorkshopItem_WithoutAConfig_IsNotASource()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("item", "Objects", "X", "X.dat"), "GUID x\n");
        Assert.Null(ContentSource.ReadWorkshopItem(Path.Combine(dir.Path, "item"),
            UnturnedInstall.Platform.Linux));
    }

    [Fact]
    public void ReadWorkshopItem_BundleNameWithoutTheExtension_StillFindsTheFile()
    {
        // Mods are free to write the name bare; the file on disk carries the extension either way.
        using var dir = new TempDir();
        dir.Write(Path.Combine("item", "MasterBundle.dat"), "Asset_Bundle_Name mymod\n");
        dir.Write(Path.Combine("item", "mymod_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine("item", "Objects", "X", "X.dat"), "GUID x\n");

        ContentSource? source = ContentSource.ReadWorkshopItem(Path.Combine(dir.Path, "item"),
            UnturnedInstall.Platform.Linux);

        Assert.NotNull(source);
        Assert.Equal("mymod", source!.Name);
        Assert.EndsWith("mymod_linux.masterbundle", source.BundlePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadWorkshopItem_WithAssetsButNoBundleFile_HasNoBundlePath()
    {
        // A mod whose bundle failed to download still contributes its asset definitions; extraction just
        // has nothing to read them from.
        using var dir = new TempDir();
        dir.Write(Path.Combine("item", "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine("item", "Objects", "X", "X.dat"), "GUID x\n");

        ContentSource? source = ContentSource.ReadWorkshopItem(Path.Combine(dir.Path, "item"),
            UnturnedInstall.Platform.Linux);

        Assert.NotNull(source);
        Assert.Equal("", source!.BundlePath);
    }

    [Fact]
    public void Discover_AtAFilesystemRoot_HasNoWorkshopToScan()
    {
        // Path.GetPathRoot has no two parent directories to walk up to a Steam library.
        Assert.Empty(ContentSource.Discover(Path.GetPathRoot(Path.GetTempPath())!,
            UnturnedInstall.Platform.Linux));
        Assert.Null(UnturnedInstall.WorkshopContentDirectory(Path.GetPathRoot(Path.GetTempPath())!));
    }

    [Fact]
    public void Discover_UnreadableWorkshopDirectory_YieldsOnlyTheGame()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX permissions only

        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        AddMod(dir, "1", ModConfig, "california2_linux.masterbundle");
        string workshop = UnturnedInstall.WorkshopContentDirectory(install)!;
        File.SetUnixFileMode(workshop, UnixFileMode.None);

        try
        {
            Directory.EnumerateDirectories(workshop).GetEnumerator().MoveNext();
            return; // running as root: modes do not apply
        }
        catch (UnauthorizedAccessException)
        {
            // expected
        }

        ContentSource core = Assert.Single(ContentSource.Discover(install, UnturnedInstall.Platform.Linux));
        Assert.True(core.IsCore);
        File.SetUnixFileMode(workshop,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    // The real install: the game is always a source, and any subscribed mod with a bundle joins it.
    //
    // The per-source assertion is deliberately NOT "has an Objects or Resources tree". Item mods have
    // neither — the one installed here keeps its clothing in Clothes/ — and requiring those folders is
    // exactly the assumption that used to drop them. What every source does have is the bundle
    // declaration that made it one, and a root that still exists.
    [RealDataFact]
    public void Discover_RealInstall_AlwaysHasTheGame()
    {
        string install = GameData.Install!;

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install);

        Assert.NotEmpty(sources);
        Assert.True(sources[0].IsCore);
        Assert.True(File.Exists(sources[0].BundlePath));
        foreach (ContentSource source in sources)
        {
            Assert.True(Directory.Exists(source.Root), source.Root);
            Assert.True(source.IsCore || File.Exists(Path.Combine(source.Root, "MasterBundle.dat")),
                source.Root);
        }
    }
}
