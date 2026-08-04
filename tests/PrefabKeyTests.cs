using System;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

// Where an asset's content is addressed from inside its bundle. This used to be private to the mesh
// extractor, which is why nothing pinned the one rule that is easy to get backwards — an explicit
// Bundle_Override_Path outranks the folder the asset happens to sit in, whatever category that is.
public class PrefabKeyTests
{
    private static ContentSource CoreOf(TempDir dir)
    {
        string bundles = Path.Combine(dir.Path, "Bundles");
        dir.Write(Path.Combine("Bundles", "MasterBundle.dat"),
            "Asset_Bundle_Name core.masterbundle\nAsset_Prefix Assets/CoreMasterBundle\n");
        dir.Write(Path.Combine("Bundles", "core_linux.masterbundle"), "x");
        Directory.CreateDirectory(Path.Combine(bundles, "Objects"));
        Directory.CreateDirectory(Path.Combine(bundles, "Trees"));
        Directory.CreateDirectory(Path.Combine(bundles, "Vehicles"));
        Directory.CreateDirectory(Path.Combine(bundles, "Assets"));
        return ContentSource.Discover(dir.Path, UnturnedInstall.Platform.Linux)[0];
    }

    private static ObjectAsset Asset(string directory, string type = "Large", string? overridePath = null)
    {
        string text = $"GUID {Guid.NewGuid():N}\nID 1\nType {type}\n"
            + (overridePath == null ? "" : $"Bundle_Override_Path {overridePath}\n");
        Assert.True(ObjectAsset.TryParse(DatParser.Parse(text), null, out ObjectAsset? asset));
        asset.Directory = directory;
        return asset;
    }

    [Fact]
    public void For_DerivesTheKeyFromTheAssetsOwnFolder()
    {
        using var dir = new TempDir();
        ContentSource core = CoreOf(dir);

        Assert.Equal("objects/small/business/cardboard_0",
            PrefabKey.For(Asset(Path.Combine(core.ObjectsDir, "Small", "Business", "Cardboard_0")), core));
        Assert.Equal("trees/birch_1",
            PrefabKey.For(Asset(Path.Combine(core.TreesDir, "Birch_1"), "Resource"), core));
        Assert.Equal("vehicles/offroader",
            PrefabKey.For(Asset(Path.Combine(core.VehiclesDir, "Offroader"), "Vehicle"), core));
    }

    [Fact]
    public void For_BundleOverrideWinsOverEveryCategory()
    {
        using var dir = new TempDir();
        ContentSource core = CoreOf(dir);

        // Trees/Ornament_0_XMAS is the official one: a Resource in the tree folder that borrows the base
        // ornament's prefab. Deriving from its own folder left it with no mesh at all.
        Assert.Equal("trees/ornament_0", PrefabKey.For(
            Asset(Path.Combine(core.TreesDir, "Ornament_0_XMAS"), "Resource", "/Trees/Ornament_0"), core));
        Assert.Equal("objects/medium/props/grave_0", PrefabKey.For(
            Asset(Path.Combine(core.ObjectsDir, "Medium", "Props", "Grave_0_XMAS"), "Medium",
                "/Objects/Medium/Props/Grave_0"), core));
    }

    [Fact]
    public void For_NormalisesTheOverridePath()
    {
        using var dir = new TempDir();
        ContentSource core = CoreOf(dir);

        // The game writes these with a leading slash and mixed case; the container table is lowercase and
        // unprefixed, so both have to go.
        Assert.Equal("objects/large/barn_0", PrefabKey.For(
            Asset(Path.Combine(core.ObjectsDir, "X"), "Large", "/Objects/Large/Barn_0/"), core));
    }
}
