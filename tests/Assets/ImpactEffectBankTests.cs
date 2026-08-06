using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// Turning a surface's effect GUID into the container path of the mark it leaves.
public class ImpactEffectBankTests
{
    private const string WoodEffect = """
        GUID b2bbae34370e493fb03f9042dd6a6acf
        Type Effect
        ID 161

        Preload 4
        """;

    private static (string Bundles, string Prefix) Source(TempDir dir, string effectFolder, string dat)
    {
        string folder = Path.Combine(dir.Path, "Bundles", "Effects", "Impacts", effectFolder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"{effectFolder}.dat"), dat);
        return (Path.Combine(dir.Path, "Bundles"), "Assets/CoreMasterBundle");
    }

    [Fact]
    public void ReadsAnEffectByGuidAndNamesItsFolderInTheBundle()
    {
        using var dir = new TempDir();
        (string bundles, string prefix) = Source(dir, "Wood_WithDecal_NoAudio", WoodEffect);

        ImpactEffectBank bank = ImpactEffectBank.ScanDirectory(bundles, prefix);
        ImpactEffectAsset? asset = bank.Find(Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf"));

        Assert.NotNull(asset);
        Assert.Equal("assets/coremasterbundle/effects/impacts/wood_withdecal_noaudio",
            asset!.BundleDirectory);
    }

    // The container table is keyed by the lowercase Unity path, so anything that reaches it has to be
    // lowered — a candidate with a capital letter simply never matches and the mark silently vanishes.
    [Fact]
    public void EveryCandidateIsALowercaseContainerPath()
    {
        using var dir = new TempDir();
        (string bundles, string prefix) = Source(dir, "Wood_WithDecal_NoAudio", WoodEffect);

        ImpactEffectAsset asset = ImpactEffectBank.ScanDirectory(bundles, prefix)
            .Find(Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf"))!;

        foreach (string candidate in asset.DecalTextureCandidates)
        {
            Assert.Equal(candidate.ToLowerInvariant(), candidate);
            Assert.StartsWith(asset.BundleDirectory, candidate, StringComparison.Ordinal);
        }
    }

    // Both shapes are offered for every effect, because only the bundle knows which exists: the single
    // hard-surface Texture.png first, then the gore Texture_N.png.
    [Fact]
    public void OffersTheSingleDecalBeforeTheGoreOnes()
    {
        using var dir = new TempDir();
        (string bundles, string prefix) = Source(dir, "Wood_WithDecal_NoAudio", WoodEffect);

        ImpactEffectAsset asset = ImpactEffectBank.ScanDirectory(bundles, prefix)
            .Find(Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf"))!;

        Assert.EndsWith("/texture.png", asset.DecalTextureCandidates[0], StringComparison.Ordinal);
        Assert.EndsWith("/texture_0.png", asset.DecalTextureCandidates[1], StringComparison.Ordinal);
        Assert.Equal(1 + ImpactEffectBank.MaxGoreTextures, asset.DecalTextureCandidates.Count);
    }

    // A .dat that is not an effect shares the folder tree with ones that are.
    [Fact]
    public void IgnoresDatFilesThatAreNotEffects()
    {
        using var dir = new TempDir();
        (string bundles, string prefix) = Source(dir, "NotAnEffect", """
            GUID 11111111111111111111111111111111
            Type Object
            """);

        Assert.Equal(0, ImpactEffectBank.ScanDirectory(bundles, prefix).Count);
    }

    // A .dat with no GUID at all — a truncated or hand-edited file — is skipped rather than registered
    // under Guid.Empty, which would make it answer for every surface that names no effect.
    [Fact]
    public void IgnoresDatFilesWithNoGuid()
    {
        using var dir = new TempDir();
        (string bundles, string prefix) = Source(dir, "Nameless", "Type Effect");

        Assert.Equal(0, ImpactEffectBank.ScanDirectory(bundles, prefix).Count);
    }

    // No asset prefix means no container path, and an effect whose textures cannot be addressed offers
    // none — it is in the bank (its GUID still resolves) but it leaves no mark.
    [Fact]
    public void AnEffectWithNoAssetPrefixOffersNoTextures()
    {
        using var dir = new TempDir();
        (string bundles, _) = Source(dir, "Wood_WithDecal_NoAudio", WoodEffect);

        ImpactEffectAsset asset = ImpactEffectBank.ScanDirectory(bundles, string.Empty)
            .Find(Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf"))!;

        Assert.Equal(string.Empty, asset.BundleDirectory);
        Assert.Empty(asset.DecalTextureCandidates);
    }

    [Fact]
    public void AMissingEffectsFolderIsAnEmptyBank() =>
        Assert.Equal(0, ImpactEffectBank.ScanDirectory("/nowhere/at/all", "Assets/X").Count);

    [Fact]
    public void FindingNothingIsNullRatherThanAThrow()
    {
        var bank = new ImpactEffectBank();

        Assert.Null(bank.Find(Guid.Empty));
        Assert.Null(bank.Find(Guid.NewGuid()));
    }

    // First claimant wins, the game's own sources first: a workshop item reusing an official GUID must not
    // redirect an official map's impacts into the mod's bundle.
    [Fact]
    public void TheFirstSourceToClaimAGuidKeepsIt()
    {
        using var core = new TempDir();
        using var mod = new TempDir();
        (string coreBundles, string prefix) = Source(core, "Wood_WithDecal_NoAudio", WoodEffect);
        (string modBundles, string modPrefix) = Source(mod, "Mod_Wood", WoodEffect);

        ImpactEffectBank merged = ImpactEffectBank.ScanSources(new[]
        {
            (coreBundles, prefix),
            (modBundles, modPrefix),
        });

        Assert.Contains("wood_withdecal_noaudio",
            merged.Find(Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf"))!.BundleDirectory);
    }

    [Fact]
    public void AFolderOutsideTheBundlesTreeNamesNoContainerPath() =>
        Assert.Equal(string.Empty,
            ImpactEffectBank.ContainerDirectory("/elsewhere/Effects/X", "/bundles", "Assets/X"));

    // An Asset_Prefix is whatever an author typed. Anything that KEYS by a prefix has to key by the same
    // normalised form the container paths are built from, or a workshop effect resolves to the wrong
    // bundle — extracted under one tag and looked for under another, so the mark never appears.
    [Theory]
    [InlineData("Assets/CoreMasterBundle")]
    [InlineData("Assets/CoreMasterBundle/")]
    [InlineData("Assets\\CoreMasterBundle")]
    [InlineData("ASSETS/CoreMasterBundle")]
    public void EveryShapeOfPrefixNormalisesToOne(string prefix) =>
        Assert.Equal("assets/coremasterbundle", ImpactEffectBank.NormalizePrefix(prefix));

    // And a container path built from any of them is the same path, with no doubled separator.
    [Theory]
    [InlineData("Assets/CoreMasterBundle")]
    [InlineData("Assets/CoreMasterBundle/")]
    [InlineData("Assets\\CoreMasterBundle")]
    public void EveryShapeOfPrefixBuildsTheSameContainerPath(string prefix)
    {
        using var dir = new TempDir();
        (string bundles, string _) = Source(dir, "Wood_WithDecal_NoAudio", WoodEffect);

        ImpactEffectAsset asset = ImpactEffectBank.ScanDirectory(bundles, prefix)
            .Find(Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf"))!;

        Assert.Equal("assets/coremasterbundle/effects/impacts/wood_withdecal_noaudio",
            asset.BundleDirectory);
        Assert.DoesNotContain("//", asset.BundleDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void NoAssetPrefixNamesNoContainerPath() =>
        Assert.Equal(string.Empty,
            ImpactEffectBank.ContainerDirectory("/bundles/Effects/X", "/bundles", string.Empty));
}

// The real chain, end to end: a surface a fist lands on names an effect, that effect is in the bank, and
// it offers a mark to draw. This is the one that breaks when Valve reorganises Bundles/Effects.
[Trait("Category", "RealData")]
public class ImpactEffectBankRealTests
{
    private static (PhysicsMaterialBank Materials, ImpactEffectBank Effects) Banks()
    {
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(GameData.Install!);
        var materialRoots = new List<string>();
        var effectSources = new List<(string, string)>();
        foreach (ContentSource source in sources)
        {
            materialRoots.Add(Path.Combine(source.AssetsDir, "PhysicsMaterials"));
            // A source's Root IS its bundles directory: the game's own is <install>/Bundles, and a
            // workshop item's is the folder its MasterBundle.dat sits in.
            effectSources.Add((source.Root,
                MasterBundleConfig.Load(source.Root)?.AssetPrefix ?? string.Empty));
        }

        return (PhysicsMaterialBank.ScanDirectories(materialRoots),
            ImpactEffectBank.ScanSources(effectSources));
    }

    // Wood, metal, concrete and gravel all name an effect whose folder carries a decal. These are what a
    // fist meets, and a mark is the whole point of the feature.
    [RealDataFact]
    public void TheHardSurfacesAPunchLandsOnAllNameAnEffect()
    {
        (PhysicsMaterialBank materials, ImpactEffectBank effects) = Banks();

        foreach (string surface in new[]
                 { "Wood_Static", "Metal_Static", "Concrete_Static", "Gravel_Static", "Tile_Static" })
        {
            Guid guid = materials.FindImpactEffect(surface);
            Assert.True(guid != Guid.Empty, $"{surface} names no impact effect");

            ImpactEffectAsset? effect = effects.Find(guid);
            Assert.True(effect != null, $"{surface} names effect {guid}, which is in no bundle");
            Assert.Contains("effects/impacts/", effect!.BundleDirectory, StringComparison.Ordinal);
        }
    }

    // Flesh is the zombie's surface, and its effect is a gore one — so the candidates it offers have to
    // include the numbered splatter textures, not only the single hard-surface one.
    [RealDataFact]
    public void FleshNamesAGoreEffect()
    {
        (PhysicsMaterialBank materials, ImpactEffectBank effects) = Banks();

        ImpactEffectAsset? effect = effects.Find(materials.FindImpactEffect("Flesh"));

        Assert.NotNull(effect);
        Assert.Contains("flesh_dynamic", effect!.BundleDirectory, StringComparison.Ordinal);
        Assert.Contains(effect.DecalTextureCandidates,
            c => c.EndsWith("/texture_3.png", StringComparison.Ordinal));
    }

    // The scan has to be cheap enough to run at load: a few hundred small .dat files, not a bundle decode.
    [RealDataFact]
    public void TheBankCoversTheShippedEffects()
    {
        (_, ImpactEffectBank effects) = Banks();

        Assert.True(effects.Count > 50, $"only {effects.Count} effects were scanned");
    }
}
