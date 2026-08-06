using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Assets;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The whole decal chain against the real install: a surface names an effect, the effect names candidate
// textures, and the master bundle actually gives one of them up.
//
// This is the only place that proves the container paths are right. Everything upstream is string
// assembly, and a path off by a folder or a case produces a perfectly valid-looking plan whose extraction
// silently returns nothing — a punch that lands, sounds, and leaves no mark.
public class ImpactDecalExtractionRealTests : TestClass
{
    public ImpactDecalExtractionRealTests(Node testScene) : base(testScene) { }

    private static string? Install => UnturnedInstall.Find();

    private static (PhysicsMaterialBank Materials, ImpactEffectBank Effects, string Bundle) Banks()
    {
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(Install!);
        var materialRoots = new List<string>();
        var effectSources = new List<(string, string)>();
        foreach (ContentSource source in sources)
        {
            materialRoots.Add(Path.Combine(source.AssetsDir, "PhysicsMaterials"));
            effectSources.Add((source.Root,
                MasterBundleConfig.Load(source.Root)?.AssetPrefix ?? string.Empty));
        }

        return (PhysicsMaterialBank.ScanDirectories(materialRoots),
            ImpactEffectBank.ScanSources(effectSources),
            UnturnedInstall.MasterBundlePath(Install!));
    }

    // Wood is the surface most of the shipped props are made of, and its effect carries a single
    // Texture.png. Extracting it proves the container path, the bundle read and the cache write.
    [Test]
    public void TheWoodDecalComesOutOfTheRealBundle()
    {
        if (Install == null)
            return; // no game installed: the real-data job is what turns this into a failure

        (PhysicsMaterialBank materials, ImpactEffectBank effects, string bundle) = Banks();
        ImpactEffectAsset? effect = effects.Find(materials.FindImpactEffect("Wood_Static"));
        Assert.NotNull(effect);

        string cache = Path.Combine(Path.GetTempPath(),
            "ug-decal-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var request = new ImpactDecalExtractor.Request(bundle, "core",
                effect!.DecalTextureCandidates, cache);

            Assert.False(ImpactDecalExtractor.IsSatisfied(request), "the temp cache started non-empty");
            int written = ImpactDecalExtractor.Extract(request);

            Assert.True(written > 0,
                $"nothing came out of {bundle} for {string.Join(", ", effect.DecalTextureCandidates)}");
            Assert.True(ImpactDecalExtractor.IsSatisfied(request));

            // And what came out has to load as a texture, not merely exist as bytes.
            var decals = new ImpactDecals(materials, effects, _ => "core", cache);
            TestScene.AddChild(decals);
            Assert.True(decals.Mark("Wood_Static", Vector3.Zero, Vector3.Up),
                "the extracted decal did not load");
            decals.QueueFree();
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); }
            catch (IOException) { }
        }
    }

    // Foliage names a particles-only effect, so it is expected to leave no mark. Asserted so a later
    // change that started inventing marks for those surfaces is caught.
    [Test]
    public void AParticlesOnlySurfaceLeavesNoMark()
    {
        if (Install == null)
            return;

        (PhysicsMaterialBank materials, ImpactEffectBank effects, string bundle) = Banks();
        ImpactEffectAsset? effect = effects.Find(materials.FindImpactEffect("Foliage"));
        if (effect == null)
            return; // the shipped assets changed; the bank tests are what report that

        string cache = Path.Combine(Path.GetTempPath(),
            "ug-decal-test-" + System.Guid.NewGuid().ToString("N"));
        try
        {
            ImpactDecalExtractor.Extract(new ImpactDecalExtractor.Request(bundle, "core",
                effect.DecalTextureCandidates, cache));

            var decals = new ImpactDecals(materials, effects, _ => "core", cache);
            TestScene.AddChild(decals);
            Assert.False(decals.Mark("Foliage", Vector3.Zero, Vector3.Up));
            decals.QueueFree();
        }
        finally
        {
            try { Directory.Delete(cache, recursive: true); }
            catch (IOException) { }
        }
    }
}
