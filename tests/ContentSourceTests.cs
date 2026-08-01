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
    [Fact]
    public void Discover_RealInstall_AlwaysHasTheGame()
    {
        if (GameData.Install is not { } install)
            return;

        IReadOnlyList<ContentSource> sources = ContentSource.Discover(install);

        Assert.NotEmpty(sources);
        Assert.True(sources[0].IsCore);
        Assert.True(File.Exists(sources[0].BundlePath));
        foreach (ContentSource source in sources)
            Assert.True(Directory.Exists(source.ObjectsDir) || Directory.Exists(source.TreesDir));
    }
}
