using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// What each bundle owes the decal cache.
//
// The object streamer plans this into its cold-load bundle pass and the session's deferred extraction
// takes whatever that pass did not cover, so the two have to derive the same list from the same inputs:
// two lists that disagreed would send the fallback back into a second whole-bundle decode for the
// difference. Everything here is about WHICH BUNDLE a texture is asked of, and under WHICH TAG it is
// filed — get either wrong and the texture is extracted, cached, and never found again.
public class ImpactDecalRequestsTests
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

    private static readonly Guid WoodEffect = Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf");
    private static readonly Guid ModEffect = Guid.Parse("cea791255ba74b43a20e511a52ebcbec");

    // A Steam library laid out like the real one, with the game and one workshop item that ships a bundle.
    private static string BuildLibrary(TempDir dir, bool withMod = true)
    {
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "MasterBundle.dat"), CoreConfig);
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "core_linux.masterbundle"),
            new byte[] { 1 });
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "core_windows.masterbundle"),
            new byte[] { 1 });
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "core_mac.masterbundle"),
            new byte[] { 1 });
        if (withMod)
        {
            string item = Path.Combine("steamapps", "workshop", "content", "304930", "1234");
            dir.Write(Path.Combine(item, "MasterBundle.dat"), ModConfig);
            dir.Write(Path.Combine(item, "california2_linux.masterbundle"), new byte[] { 1 });
            dir.Write(Path.Combine(item, "california2_windows.masterbundle"), new byte[] { 1 });
            dir.Write(Path.Combine(item, "california2_mac.masterbundle"), new byte[] { 1 });
            // A source is only a source when it ships assets to go with its bundle; a pure map or
            // localization item has no content to extract and is deliberately skipped.
            dir.Write(Path.Combine(item, "Objects", "CA_Sign", "CA_Sign.dat"),
                "GUID 0517b7a03b844929856fc4f72701fca9\nType Medium\n");
        }

        return install;
    }

    private static IReadOnlyList<ContentSource> Sources(string install) => ContentSource.Discover(install);

    private static PhysicsMaterialAsset Material(string name, Guid effect)
    {
        Assert.True(PhysicsMaterialAsset.TryParse(DatParser.Parse($$"""
            Metadata
            {
                GUID {{Guid.NewGuid():N}}
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    {{name}}
                ]
                WipDoNotUseTemp_BulletImpactEffect "{{effect:N}}"
            }
            """), out PhysicsMaterialAsset? asset));
        return asset;
    }

    private static ContentSource SourceNamed(IReadOnlyList<ContentSource> sources, string name)
    {
        foreach (ContentSource source in sources)
            if (source.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                return source;
        throw new InvalidOperationException($"no source named {name}");
    }

    [Fact]
    public void ASessionWithNoSurfacesStillAsksForTheCrosshairIcons()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = Sources(BuildLibrary(dir, withMod: false));

        List<ImpactDecalExtractor.Request> requests = ImpactDecalRequests.For(sources,
            new PhysicsMaterialBank(), new ImpactEffectBank(), "/tmp/decals");

        // The icons ride the same cache and the same pass, and they are only ever in the core bundle.
        ImpactDecalExtractor.Request only = Assert.Single(requests);
        Assert.Contains("assets/coremasterbundle/ui/player/icons/playerlife/dot.png", only.ContainerPaths);
    }

    [Fact]
    public void ASurfacesEffectTexturesAreAskedOfTheBundleThatShippedIt()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir, withMod: false);
        IReadOnlyList<ContentSource> sources = Sources(install);
        ContentSource core = SourceNamed(sources, "core");

        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Wood_Static", WoodEffect));
        var effects = new ImpactEffectBank();
        effects.Add(new ImpactEffectAsset(WoodEffect, "assets/core/effects/impacts/wood",
            new[] { "assets/core/effects/impacts/wood/texture.png" }, core.Root));

        List<ImpactDecalExtractor.Request> requests =
            ImpactDecalRequests.For(sources, materials, effects, "/tmp/decals");

        ImpactDecalExtractor.Request request = Assert.Single(requests);
        Assert.Equal(core.BundlePath, request.BundlePath);
        Assert.Contains("assets/core/effects/impacts/wood/texture.png", request.ContainerPaths);
    }

    // An effect's textures are packaged in the bundle of whichever source SHIPPED it, and the effect
    // remembers which that was. Keying by asset prefix instead let two sources declaring the same one
    // silently own each other's effects — extracted and looked for under the wrong tag, never drawn.
    [Fact]
    public void EachSourcesEffectGoesToItsOwnBundle()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir);
        IReadOnlyList<ContentSource> sources = Sources(install);
        ContentSource core = SourceNamed(sources, "core");
        ContentSource mod = SourceNamed(sources, "california2");

        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Wood_Static", WoodEffect));
        materials.Add(Material("CA_Asphalt", ModEffect));
        var effects = new ImpactEffectBank();
        effects.Add(new ImpactEffectAsset(WoodEffect, "d", new[] { "core/wood.png" }, core.Root));
        effects.Add(new ImpactEffectAsset(ModEffect, "d", new[] { "mod/asphalt.png" }, mod.Root));

        List<ImpactDecalExtractor.Request> requests =
            ImpactDecalRequests.For(sources, materials, effects, "/tmp/decals");

        var byBundle = new Dictionary<string, ImpactDecalExtractor.Request>(StringComparer.Ordinal);
        foreach (ImpactDecalExtractor.Request request in requests)
            byBundle[request.BundlePath] = request;

        Assert.Contains("core/wood.png", byBundle[core.BundlePath].ContainerPaths);
        Assert.Contains("mod/asphalt.png", byBundle[mod.BundlePath].ContainerPaths);
        Assert.DoesNotContain("mod/asphalt.png", byBundle[core.BundlePath].ContainerPaths);
    }

    // An effect whose source is not among the loaded ones has no bundle to be asked of, so it is dropped
    // rather than attributed to whichever source happened to be first.
    [Fact]
    public void AnEffectFromAnUnknownSourceIsDropped()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = Sources(BuildLibrary(dir, withMod: false));

        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Wood_Static", WoodEffect));
        var effects = new ImpactEffectBank();
        effects.Add(new ImpactEffectAsset(WoodEffect, "d", new[] { "orphan.png" },
            "/nowhere-this-source-is-not-loaded"));

        List<ImpactDecalExtractor.Request> requests =
            ImpactDecalRequests.For(sources, materials, effects, "/tmp/decals");

        foreach (ImpactDecalExtractor.Request request in requests)
            Assert.DoesNotContain("orphan.png", request.ContainerPaths);
    }

    // The tag is what both ends agree on. The extraction files a texture under it and the runtime reads
    // it back by it, so a source's own CacheTag has to win over the file-name fallback — the file name
    // carries a platform suffix and would key the same content differently per platform.
    [Fact]
    public void ASourcesOwnTagIsUsedRatherThanItsFileName()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = Sources(BuildLibrary(dir));
        ContentSource mod = SourceNamed(sources, "california2");

        Assert.Equal(mod.CacheTag, ImpactDecalRequests.TagFor(sources, mod.BundlePath));
        Assert.DoesNotContain("_linux", ImpactDecalRequests.TagFor(sources, mod.BundlePath),
            StringComparison.OrdinalIgnoreCase);
    }

    // A bundle none of the sources claims still needs a tag — the fallback derives one from its file name
    // so a texture is at least filed consistently within one platform.
    [Fact]
    public void AnUnclaimedBundleFallsBackToATagFromItsFileName()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = Sources(BuildLibrary(dir, withMod: false));

        string tag = ImpactDecalRequests.TagFor(sources, "/elsewhere/mystery.masterbundle");

        Assert.NotEmpty(tag);
        Assert.Equal(tag, ImpactDecalRequests.TagFor(sources, "/other/place/mystery.masterbundle"));
    }

    // The single-argument overload scans the banks itself, and has to reach the same answer as handing
    // them in — it is the one the object streamer calls.
    [Fact]
    public void ScanningTheBanksReachesTheSameAnswerAsSupplyingThem()
    {
        using var dir = new TempDir();
        string install = BuildLibrary(dir, withMod: false);
        IReadOnlyList<ContentSource> sources = Sources(install);

        List<ImpactDecalExtractor.Request> scanned = ImpactDecalRequests.For(sources, "/tmp/decals");
        (PhysicsMaterialBank materials, ImpactEffectBank effects) = ImpactDecalRequests.Banks(sources);
        List<ImpactDecalExtractor.Request> supplied =
            ImpactDecalRequests.For(sources, materials, effects, "/tmp/decals");

        Assert.Equal(supplied.Count, scanned.Count);
        for (int i = 0; i < supplied.Count; i++)
        {
            Assert.Equal(supplied[i].BundlePath, scanned[i].BundlePath);
            Assert.Equal(supplied[i].BundleTag, scanned[i].BundleTag);
            Assert.Equal(supplied[i].ContainerPaths.Count, scanned[i].ContainerPaths.Count);
        }
    }

    // Banks over an install with no PhysicsMaterials or Effects trees is the ordinary cold case, not an
    // error: a workshop item that defines no surfaces of its own reaches exactly this.
    [Fact]
    public void BanksOverAnInstallWithNoSurfaceTreesIsEmptyRatherThanAThrow()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = Sources(BuildLibrary(dir));

        (PhysicsMaterialBank materials, ImpactEffectBank effects) = ImpactDecalRequests.Banks(sources);

        Assert.NotNull(materials);
        Assert.NotNull(effects);
    }

    // A core source that ships no bundle for this platform has nowhere to fetch the icons from, and the
    // request has to be dropped rather than built against an empty path.
    [Fact]
    public void ACoreSourceWithNoBundleForThisPlatformAsksForNothing()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "MasterBundle.dat"), CoreConfig);

        List<ImpactDecalExtractor.Request> requests = ImpactDecalRequests.For(
            ContentSource.Discover(install), "/tmp/decals");

        Assert.Empty(requests);
    }

    // ...and one whose MasterBundle.dat declares no asset prefix cannot name a container path at all, so
    // the icons are skipped rather than asked for under an empty prefix that matches nothing.
    [Fact]
    public void ACoreSourceWithNoAssetPrefixAsksForNoIcons()
    {
        using var dir = new TempDir();
        string install = Path.Combine(dir.Path, "steamapps", "common", "Unturned");
        dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles", "MasterBundle.dat"),
            "Asset_Bundle_Name core.masterbundle\nAsset_Bundle_Version 6\n");
        foreach (string platform in new[] { "linux", "windows", "mac" })
        {
            dir.Write(Path.Combine("steamapps", "common", "Unturned", "Bundles",
                $"core_{platform}.masterbundle"), new byte[] { 1 });
        }

        List<ImpactDecalExtractor.Request> requests = ImpactDecalRequests.For(
            ContentSource.Discover(install), "/tmp/decals");

        Assert.Empty(requests);
    }

    [Fact]
    public void ANullSourceListIsRejectedRatherThanDereferenced()
    {
        Assert.Throws<ArgumentNullException>(() => ImpactDecalRequests.Banks(null!));
        Assert.Throws<ArgumentNullException>(() => ImpactDecalRequests.For(null!, "/tmp/decals"));
        Assert.Throws<ArgumentNullException>(() => ImpactDecalRequests.For(null!,
            new PhysicsMaterialBank(), new ImpactEffectBank(), "/tmp/decals"));
        Assert.Throws<ArgumentNullException>(() => ImpactDecalRequests.TagFor(null!, "/a.masterbundle"));
    }

    // The cache directory is handed in rather than resolved here — that is what let this move out of the
    // half of the tree that needs an engine to know what `user://` means. It has to reach every request.
    [Fact]
    public void TheCacheDirectoryReachesEveryRequest()
    {
        using var dir = new TempDir();
        IReadOnlyList<ContentSource> sources = Sources(BuildLibrary(dir));

        List<ImpactDecalExtractor.Request> requests =
            ImpactDecalRequests.For(sources, "/somewhere/decal_cache");

        Assert.NotEmpty(requests);
        foreach (ImpactDecalExtractor.Request request in requests)
            Assert.Equal("/somewhere/decal_cache", request.CacheDirectory);
    }
}
