using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class TerrainLayerPlanTests
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

    private static readonly Guid Grass = new("11111111111111111111111111111111");
    private static readonly Guid Sand = new("22222222222222222222222222222222");
    private static readonly Guid Mod = new("33333333333333333333333333333333");
    private static readonly Guid NoTexture = new("44444444444444444444444444444444");

    // An install with the game's bundle and one workshop item that ships its own.
    private static (string Install, IReadOnlyList<ContentSource> Sources) Library(TempDir dir)
    {
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string bundles = Path.Combine("steamapps", "common", "Unturned", "Bundles");
        dir.Write(Path.Combine(bundles, "MasterBundle.dat"), CoreConfig);
        dir.Write(Path.Combine(bundles, "core_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(bundles, "Objects", "Crate", "Crate.dat"), "GUID 0\nType Small\n");

        string item = Path.Combine("steamapps", "workshop", "content", "304930", "3707778928");
        dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
        dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(item, "Objects", "Sign", "Sign.dat"), "GUID 0\nType Medium\n");

        return (install, ContentSource.Discover(install, UnturnedInstall.Platform.Linux));
    }

    // A landscape material naming a texture inside `bundle`, written where ScanDirectory will find it.
    private static void WriteMaterial(TempDir dir, string relativeDir, Guid guid, string bundle, string path)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("Metadata");
        text.AppendLine("{");
        text.AppendLine($"    GUID {guid:N}");
        text.AppendLine("    Type SDG.Unturned.LandscapeMaterialAsset, Assembly-CSharp");
        text.AppendLine("}");
        text.AppendLine("Asset");
        text.AppendLine("{");
        text.AppendLine("    ID 0");
        if (path.Length > 0)
        {
            text.AppendLine("    Texture");
            text.AppendLine("    {");
            text.AppendLine($"        Name {bundle}");
            text.AppendLine($"        Path {path}");
            text.AppendLine("    }");
        }
        text.AppendLine("    Physics_Material FOLIAGE_STATIC");
        text.AppendLine("}");

        dir.Write(Path.Combine(relativeDir, guid.ToString("N") + ".asset"), text.ToString());
    }

    private static Dictionary<Guid, LandscapeMaterialAsset> Materials(TempDir dir, string install)
    {
        var materials = new Dictionary<Guid, LandscapeMaterialAsset>();
        foreach (string root in new[]
        {
            Path.Combine(install, "Bundles", "Assets", "Landscapes"),
            Path.Combine(dir.Path, "steamapps", "workshop", "content", "304930", "3707778928", "Assets",
                "Landscapes"),
        })
        {
            foreach (KeyValuePair<Guid, LandscapeMaterialAsset> entry in
                LandscapeMaterialAsset.ScanDirectory(root))
            {
                materials[entry.Key] = entry.Value;
            }
        }
        return materials;
    }

    [Fact]
    public void ByBundle_SendsEachMaterialToTheBundleItsTextureLivesIn()
    {
        using var dir = new TempDir();
        (string install, IReadOnlyList<ContentSource> sources) = Library(dir);
        string coreLandscapes = Path.Combine("steamapps", "common", "Unturned", "Bundles", "Assets",
            "Landscapes");
        string modLandscapes = Path.Combine("steamapps", "workshop", "content", "304930", "3707778928",
            "Assets", "Landscapes");

        WriteMaterial(dir, coreLandscapes, Grass, "core.masterbundle", "Terrain/Materials/PEI/Grass_00.png");
        WriteMaterial(dir, coreLandscapes, Sand, "core.masterbundle", "Terrain/Materials/PEI/Sand_00.png");
        WriteMaterial(dir, modLandscapes, Mod, "california2.masterbundle", "Terrain/CA_Dirt.png");

        Dictionary<string, TerrainLayerPlan.BundleWants> byBundle = TerrainLayerPlan.ByBundle(
            new[] { Grass, Sand, Mod }, Materials(dir, install), sources, MasterBundleConfig.Load);

        Assert.Equal(2, byBundle.Count);

        TerrainLayerPlan.BundleWants core =
            byBundle[Path.Combine(install, "Bundles", "core_linux.masterbundle")];
        Assert.Equal(new[] { Grass },
            core.ByContainerPath["assets/coremasterbundle/terrain/materials/pei/grass_00.png"]);
        Assert.Equal(new[] { Sand },
            core.ByContainerPath["assets/coremasterbundle/terrain/materials/pei/sand_00.png"]);

        TerrainLayerPlan.BundleWants mod = byBundle[Path.Combine(dir.Path, "steamapps", "workshop",
            "content", "304930", "3707778928", "california2_linux.masterbundle")];
        Assert.Equal(new[] { Mod }, Assert.Single(mod.ByContainerPath).Value);
        Assert.Equal("assets/californiamasterbundle/terrain/ca_dirt.png",
            Assert.Single(mod.ByContainerPath).Key);
    }

    [Fact]
    public void ByBundle_PreservesEveryMaterialSharingAContainerPath()
    {
        using var dir = new TempDir();
        (string install, IReadOnlyList<ContentSource> sources) = Library(dir);
        string coreLandscapes = Path.Combine("steamapps", "common", "Unturned", "Bundles", "Assets",
            "Landscapes");
        const string sharedPath = "Terrain/Materials/PEI/Shared.png";

        WriteMaterial(dir, coreLandscapes, Grass, "core.masterbundle", sharedPath);
        WriteMaterial(dir, coreLandscapes, Sand, "core.masterbundle", sharedPath);

        Dictionary<string, TerrainLayerPlan.BundleWants> byBundle = TerrainLayerPlan.ByBundle(
            new[] { Grass, Sand }, Materials(dir, install), sources, MasterBundleConfig.Load);

        TerrainLayerPlan.BundleWants core = Assert.Single(byBundle).Value;
        Assert.Equal(new[] { Grass, Sand },
            core.ByContainerPath["assets/coremasterbundle/terrain/materials/pei/shared.png"]);
    }

    [Fact]
    public void ByBundle_LeavesOutWhatNoPassCouldProduce()
    {
        using var dir = new TempDir();
        (string install, IReadOnlyList<ContentSource> sources) = Library(dir);
        string coreLandscapes = Path.Combine("steamapps", "common", "Unturned", "Bundles", "Assets",
            "Landscapes");

        // A material with no texture at all, and one naming a bundle no source ships: waiting on either
        // would keep a pass reading for bytes that are not there.
        WriteMaterial(dir, coreLandscapes, NoTexture, "core.masterbundle", "");
        WriteMaterial(dir, coreLandscapes, Sand, "russia.masterbundle", "Terrain/Snow.png");
        WriteMaterial(dir, coreLandscapes, Grass, "core.masterbundle", "Terrain/Grass.png");

        Dictionary<string, TerrainLayerPlan.BundleWants> byBundle = TerrainLayerPlan.ByBundle(
            // The unknown GUID is one the hierarchy names but no asset defines.
            new[] { Grass, Sand, NoTexture, Guid.NewGuid() },
            Materials(dir, install), sources, MasterBundleConfig.Load);

        TerrainLayerPlan.BundleWants core = Assert.Single(byBundle).Value;
        Assert.Equal(new[] { Grass }, Assert.Single(core.ByContainerPath).Value);
    }

    [Fact]
    public void ByBundle_ReadsEachSourceConfigOnce()
    {
        using var dir = new TempDir();
        (string install, IReadOnlyList<ContentSource> sources) = Library(dir);
        string coreLandscapes = Path.Combine("steamapps", "common", "Unturned", "Bundles", "Assets",
            "Landscapes");
        WriteMaterial(dir, coreLandscapes, Grass, "core.masterbundle", "Terrain/Grass.png");
        WriteMaterial(dir, coreLandscapes, Sand, "core.masterbundle", "Terrain/Sand.png");

        var reads = new List<string>();
        TerrainLayerPlan.ByBundle(new[] { Grass, Sand }, Materials(dir, install), sources, root =>
        {
            reads.Add(root);
            return MasterBundleConfig.Load(root);
        });

        // Two materials, one source each: the .dat behind a source is parsed once, not per material.
        Assert.Equal(reads.Count, new HashSet<string>(reads).Count);
    }

    [Fact]
    public void ByBundle_PrefersTheGuidClaimantWhenWorkshopBundleNamesCollide()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string workshop = Path.Combine("steamapps", "workshop", "content", "304930");
        string first = Path.Combine(workshop, "1000000001");
        string second = Path.Combine(workshop, "1000000002");
        const string firstConfig = """
            Asset_Bundle_Name shared.masterbundle
            Asset_Prefix Assets/First
            Asset_Bundle_Version 1
            """;
        const string secondConfig = """
            Asset_Bundle_Name shared.masterbundle
            Asset_Prefix Assets/Second
            Asset_Bundle_Version 1
            """;

        dir.Write(Path.Combine(first, "MasterBundle.dat"), firstConfig);
        dir.Write(Path.Combine(first, "shared_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(first, "Objects", "First", "First.dat"), "GUID 0\nType Small\n");
        dir.Write(Path.Combine(second, "MasterBundle.dat"), secondConfig);
        dir.Write(Path.Combine(second, "shared_linux.masterbundle"), new byte[] { 2 });
        dir.Write(Path.Combine(second, "Objects", "Second", "Second.dat"), "GUID 0\nType Small\n");
        WriteMaterial(dir, Path.Combine(second, "Assets", "Landscapes"), Grass,
            "shared.masterbundle", "Terrain/Grass.png");

        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(install, UnturnedInstall.Platform.Linux);
        var materials = new Dictionary<Guid, LandscapeMaterialAsset>();
        var claimantRoots = new Dictionary<Guid, string>();
        foreach (ContentSource source in sources)
            LandscapeMaterialAsset.MergeFirstClaimants(materials, claimantRoots, source.Root,
                LandscapeMaterialAsset.ScanDirectory(Path.Combine(source.AssetsDir, "Landscapes")));

        Dictionary<string, TerrainLayerPlan.BundleWants> byBundle = TerrainLayerPlan.ByBundle(
            new[] { Grass }, materials, claimantRoots, sources, MasterBundleConfig.Load);

        TerrainLayerPlan.BundleWants wants = Assert.Single(byBundle).Value;
        Assert.Equal(Path.Combine(dir.Path, second, "shared_linux.masterbundle"), wants.BundlePath);
        Assert.Equal("assets/second/terrain/grass.png", Assert.Single(wants.ByContainerPath).Key);
    }

    [Fact]
    public void ByBundle_SourceWithoutABundle_IsSkipped()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string bundles = Path.Combine("steamapps", "common", "Unturned", "Bundles");
        dir.Write(Path.Combine(bundles, "MasterBundle.dat"), CoreConfig); // no .masterbundle file next to it
        dir.Write(Path.Combine(bundles, "Objects", "Crate", "Crate.dat"), "GUID 0\nType Small\n");
        WriteMaterial(dir, Path.Combine(bundles, "Assets", "Landscapes"), Grass, "core.masterbundle",
            "Terrain/Grass.png");

        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(install, UnturnedInstall.Platform.Linux);

        Assert.Empty(TerrainLayerPlan.ByBundle(new[] { Grass }, Materials(dir, install), sources,
            MasterBundleConfig.Load));
    }

    [Fact]
    public void ByBundle_SourceWithoutAReadableConfig_IsSkipped()
    {
        // The game's Bundles folder is a source even with no MasterBundle.dat of its own, and without one
        // there is no way to turn a texture path into a container key.
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        string bundles = Path.Combine("steamapps", "common", "Unturned", "Bundles");
        dir.Write(Path.Combine(bundles, "core_linux.masterbundle"), new byte[] { 1 });
        dir.Write(Path.Combine(bundles, "Objects", "Crate", "Crate.dat"), "GUID 0\nType Small\n");
        WriteMaterial(dir, Path.Combine(bundles, "Assets", "Landscapes"), Grass, "core.masterbundle",
            "Terrain/Grass.png");

        IReadOnlyList<ContentSource> sources =
            ContentSource.Discover(install, UnturnedInstall.Platform.Linux);
        Assert.NotEmpty(sources);

        Assert.Empty(TerrainLayerPlan.ByBundle(new[] { Grass }, Materials(dir, install), sources,
            MasterBundleConfig.Load));
    }

    [Fact]
    public void For_BundleThatOwesNothing_IsAnEmptySet()
    {
        var byBundle = new Dictionary<string, TerrainLayerPlan.BundleWants>();
        TerrainLayerPlan.BundleWants wants = TerrainLayerPlan.For(byBundle, "core.masterbundle");

        Assert.Empty(wants.ByContainerPath);
        Assert.Equal("core.masterbundle", wants.BundlePath);
    }

    [Fact]
    public void For_BundleThatOwes_IsItsOwnSet()
    {
        var wants = new TerrainLayerPlan.BundleWants("core_linux.masterbundle");
        wants.ByContainerPath["assets/x.png"] = new[] { Grass };

        Assert.Same(wants, TerrainLayerPlan.For(
            new Dictionary<string, TerrainLayerPlan.BundleWants> { ["core_linux.masterbundle"] = wants },
            "core_linux.masterbundle"));
    }
}
