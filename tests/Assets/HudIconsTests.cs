using System;
using System.Collections.Generic;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// Where the crosshair's own icons sit inside the core bundle.
//
// Container paths are keyed lowercase in the bundle's own table, and the asset prefix comes out of
// MasterBundle.dat with whatever case somebody typed there. A prefix that reached the lookup uncased
// would find nothing — and a missing crosshair is drawn as nothing, so the only symptom would be a HUD
// that is silently short an icon.
public class HudIconsTests
{
    [Fact]
    public void APathIsThePrefixThenTheIconsFolderThenTheFile()
    {
        Assert.Equal("assets/coremasterbundle/ui/player/icons/playerlife/dot.png",
            HudIcons.ContainerPath("assets/coremasterbundle", HudIcons.Dot));
    }

    // The prefix is lowercased on the way in. MasterBundle.dat is authored by hand.
    [Theory]
    [InlineData("Assets/CoreMasterBundle")]
    [InlineData("ASSETS/COREMASTERBUNDLE")]
    [InlineData("assets/coremasterbundle")]
    public void ThePrefixIsLowercasedHoweverItWasSpelled(string prefix)
    {
        Assert.Equal("assets/coremasterbundle/ui/player/icons/playerlife/hit_entity.png",
            HudIcons.ContainerPath(prefix, HudIcons.HitEntity));
    }

    [Fact]
    public void EveryIconIsAskedFor()
    {
        List<string> paths = HudIcons.ContainerPaths("assets/coremasterbundle");

        Assert.Equal(HudIcons.Files.Count, paths.Count);
        foreach (string file in HudIcons.Files)
        {
            Assert.Contains(paths,
                path => path.EndsWith("/" + file, StringComparison.Ordinal));
        }
    }

    // The four are distinct, and each names a different mark: the dot is the crosshair itself and the
    // three hits are what it turns into. Two of them colliding would draw the wrong feedback for a hit.
    [Fact]
    public void TheFourIconsAreDistinct()
    {
        Assert.Equal(4, HudIcons.Files.Count);
        Assert.Equal(HudIcons.Files.Count, new HashSet<string>(HudIcons.Files, StringComparer.Ordinal).Count);
    }

    // The request rides ImpactDecalExtractor because the work is identical, so it has to arrive in that
    // shape: same bundle, same tag, same cache directory, and every icon path in it.
    [Fact]
    public void TheRequestCarriesEveryIconUnderTheGivenTagAndCache()
    {
        ImpactDecalExtractor.Request request = HudIcons.RequestFor("/bundles/core.masterbundle", "core",
            "Assets/CoreMasterBundle", "/tmp/decals");

        Assert.Equal("/bundles/core.masterbundle", request.BundlePath);
        Assert.Equal("core", request.BundleTag);
        Assert.Equal("/tmp/decals", request.CacheDirectory);
        Assert.Equal(HudIcons.Files.Count, request.ContainerPaths.Count);
        Assert.Contains("assets/coremasterbundle/ui/player/icons/playerlife/dot.png", request.ContainerPaths);
    }
}
