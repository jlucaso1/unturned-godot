using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The crosshair's own textures, out of the real core bundle.
//
// The container path is assembled from a prefix and a folder name, and a path off by a case or a folder
// produces a perfectly valid-looking request whose extraction returns nothing — a crosshair that is simply
// absent, with no error anywhere. This is the only place that catches it.
public class HudIconsRealTests : TestClass
{
    public HudIconsRealTests(Node testScene) : base(testScene) { }

    private static string? Install => UnturnedInstall.Find();

    [Test]
    public void TheCrosshairIconsComeOutOfTheRealBundle()
    {
        if (Install == null)
            return; // no game installed; the real-data job is what turns this into a failure

        ContentSource? core = null;
        foreach (ContentSource source in ContentSource.Discover(Install))
            if (source.IsCore)
                core = source;
        Assert.NotNull(core);

        string prefix = MasterBundleConfig.Load(core!.Root)?.AssetPrefix ?? string.Empty;
        Assert.NotEqual(string.Empty, prefix);

        string cache = Path.Combine(Path.GetTempPath(),
            "ug-hud-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            int written = ImpactDecalExtractor.Extract(
                HudIcons.RequestFor(core.BundlePath, "core", prefix, cache));

            // All four: the dot and the three hit icons. A partial extraction means the folder moved.
            Assert.Equal(HudIcons.Files.Count, written);

            var icons = new HudIconSet(cache, "core", prefix);
            foreach (string file in HudIcons.Files)
                Assert.True(icons.Get(file) != null, $"{file} extracted but did not load");
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); }
            catch (IOException) { }
        }
    }

    // With no icons at all the HUD builds and draws nothing, rather than throwing or standing in a
    // placeholder — which is the state a session is in before the deferred extraction has finished.
    [Test]
    public void WithNoIconsTheHudIsSilentlyEmpty()
    {
        var hud = new PlayerHud(new HudIconSet("/nowhere", "core", "Assets/X"));
        TestScene.AddChild(hud);

        Assert.False(hud.DotVisible);
        hud.Hitmark(UnturnedGodot.UI.EPlayerHit.Critical);
        hud.Hitmark(UnturnedGodot.UI.EPlayerHit.None);

        var wedges = new List<Node>();
        foreach (Node child in hud.GetChildren())
            wedges.Add(child);
        Assert.NotEmpty(wedges); // the root Control exists; the icons under it do not

        hud.QueueFree();
    }
}
