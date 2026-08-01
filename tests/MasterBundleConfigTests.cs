using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class MasterBundleConfigTests
{
    [Fact]
    public void Load_ReadsNameAndPrefix_AndBuildsContainerKeys()
    {
        using var dir = new TempDir();
        dir.Write("MasterBundle.dat", """
            Asset_Bundle_Name core.masterbundle

            // Path to the asset bundle within Unity.
            Asset_Prefix Assets/CoreMasterBundle

            Asset_Bundle_Version 6
            """);

        MasterBundleConfig? config = MasterBundleConfig.Load(dir.Path);

        Assert.NotNull(config);
        Assert.Equal("core.masterbundle", config!.BundleName);
        Assert.Equal("Assets/CoreMasterBundle", config.AssetPrefix);
        Assert.Equal(dir.Path, config.Directory);

        // Unity's container keys are the lowercase full path.
        Assert.Equal("assets/coremasterbundle/terrain/materials/pei/grass_00.png",
            config.ContainerPath("Terrain/Materials/PEI/Grass_00.png"));
        Assert.Equal("assets/coremasterbundle/terrain/materials/pei/grass_00.png",
            config.ContainerPath("/Terrain\\Materials/PEI/Grass_00.png"));
    }

    [Fact]
    public void ContainerPath_WithoutAPrefix_IsThePathItself()
    {
        using var dir = new TempDir();
        dir.Write("MasterBundle.dat", "Asset_Bundle_Name mod.masterbundle\n");

        MasterBundleConfig config = MasterBundleConfig.Load(dir.Path)!;

        Assert.Equal("", config.AssetPrefix);
        Assert.Equal("objects/sign/sign.prefab", config.ContainerPath("Objects/Sign/Sign.prefab"));
    }

    [Fact]
    public void Matches_AcceptsTheNameWithOrWithoutTheExtension()
    {
        using var dir = new TempDir();
        dir.Write("MasterBundle.dat", "Asset_Bundle_Name California2.masterbundle\n");

        MasterBundleConfig config = MasterBundleConfig.Load(dir.Path)!;

        Assert.True(config.Matches("california2.masterbundle"));
        Assert.True(config.Matches("California2"));
        Assert.False(config.Matches("core.masterbundle"));
        Assert.False(config.Matches(""));
    }

    [Fact]
    public void Matches_ANameWrittenWithoutTheExtension_MatchesEitherForm()
    {
        using var dir = new TempDir();
        dir.Write("MasterBundle.dat", "Asset_Bundle_Name mymod\n");

        MasterBundleConfig config = MasterBundleConfig.Load(dir.Path)!;

        Assert.True(config.Matches("mymod"));
        Assert.True(config.Matches("mymod.masterbundle"));
        Assert.False(config.Matches("othermod"));
    }

    [Fact]
    public void Load_UnreadableConfig_IsNull()
    {
        using var dir = new TempDir();
        string path = dir.Write("MasterBundle.dat", "Asset_Bundle_Name mod.masterbundle\n");

        using var handle = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.None);
        Assert.Null(MasterBundleConfig.Load(dir.Path));
    }

    [Fact]
    public void Load_MissingOrNamelessConfig_IsNull()
    {
        using var dir = new TempDir();
        Assert.Null(MasterBundleConfig.Load(dir.Path));

        dir.Write("MasterBundle.dat", "Asset_Prefix Assets/Whatever\n");
        Assert.Null(MasterBundleConfig.Load(dir.Path));
    }

    [Fact]
    public void Load_RealInstall_MatchesTheShippedConfig()
    {
        if (GameData.Install is not { } install)
            return;

        MasterBundleConfig? config = MasterBundleConfig.Load(Path.Combine(install, "Bundles"));

        Assert.NotNull(config);
        Assert.Equal("core.masterbundle", config!.BundleName);
        Assert.Equal("Assets/CoreMasterBundle", config.AssetPrefix);
    }
}
