using System;
using System.Collections.Generic;
using UnturnedGodot.Assets;
using Xunit;

namespace UnturnedGodot.Tests.Assets;

// Which decal textures a session asks each bundle for. The extraction and the runtime both read this, and
// a disagreement between them is the failure it exists to prevent: a texture written under one key and
// looked for under another exists on disk and is never found.
public class ImpactDecalPlanTests
{
    private static readonly Guid WoodEffect = Guid.Parse("b2bbae34370e493fb03f9042dd6a6acf");
    private static readonly Guid FleshEffect = Guid.Parse("cea791255ba74b43a20e511a52ebcbec");

    private static PhysicsMaterialAsset Material(string name, Guid effect, Guid fallback = default,
        Guid? guid = null)
    {
        string effectLine = effect == Guid.Empty
            ? string.Empty
            : $"WipDoNotUseTemp_BulletImpactEffect \"{effect:N}\"";
        string fallbackLine = fallback == Guid.Empty ? string.Empty : $"Fallback {fallback:N}";

        Assert.True(PhysicsMaterialAsset.TryParse(UnturnedGodot.Dat.DatParser.Parse($$"""
            Metadata
            {
                GUID {{(guid ?? Guid.NewGuid()):N}}
                Type SDG.Unturned.PhysicsMaterialAsset, Assembly-CSharp
            }
            Asset
            {
                UnityNames
                [
                    {{name}}
                ]
                {{fallbackLine}}
                {{effectLine}}
            }
            """), out PhysicsMaterialAsset? asset));
        return asset;
    }

    private static ImpactEffectAsset Effect(Guid guid, string folder, params string[] textures) =>
        new(guid, folder, textures);

    [Fact]
    public void PlansTheTexturesOfEverySurfaceThatNamesAnEffect()
    {
        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Wood_Static", WoodEffect));
        var effects = new ImpactEffectBank();
        effects.Add(Effect(WoodEffect, "assets/core/effects/impacts/wood", "wood/texture.png"));

        Dictionary<string, HashSet<string>> planned =
            ImpactDecalPlan.TexturesByBundle(materials, effects, _ => "core.masterbundle");

        Assert.Equal(new[] { "wood/texture.png" }, Assert.Single(planned).Value);
    }

    // Several surfaces share one effect — Cloth, Concrete and Tile all name Concrete_WithDecal_NoAudio.
    // Planning it once is what keeps the request small.
    [Fact]
    public void AnEffectSharedBySeveralSurfacesIsPlannedOnce()
    {
        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Concrete", WoodEffect));
        materials.Add(Material("Cloth", WoodEffect));
        materials.Add(Material("Tile", WoodEffect));
        var effects = new ImpactEffectBank();
        effects.Add(Effect(WoodEffect, "d", "d/texture.png"));

        Dictionary<string, HashSet<string>> planned =
            ImpactDecalPlan.TexturesByBundle(materials, effects, _ => "core.masterbundle");

        Assert.Single(Assert.Single(planned).Value);
    }

    // The fallback chain is the same one the audio walks: a surface that names no effect of its own takes
    // its parent's.
    [Fact]
    public void ASurfaceInheritsItsFallbacksEffect()
    {
        Guid baseGuid = Guid.NewGuid();
        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Foliage", WoodEffect, guid: baseGuid));
        materials.Add(Material("Peaks_Grass_Dry", Guid.Empty, fallback: baseGuid));
        var effects = new ImpactEffectBank();
        effects.Add(Effect(WoodEffect, "d", "d/texture.png"));

        Assert.Equal(WoodEffect, materials.FindImpactEffect("Peaks_Grass_Dry"));
        Assert.Single(ImpactDecalPlan.TexturesByBundle(materials, effects, _ => "b"));
    }

    [Fact]
    public void ASurfaceThatNamesNoEffectPlansNothing()
    {
        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Water", Guid.Empty));

        Assert.Empty(ImpactDecalPlan.TexturesByBundle(materials, new ImpactEffectBank(), _ => "b"));
    }

    // An effect the banks know about but whose bundle could not be resolved is dropped rather than filed
    // under an empty key, which would produce a request against no bundle at all.
    [Fact]
    public void AnEffectWithNoResolvableBundleIsDropped()
    {
        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Wood_Static", WoodEffect));
        var effects = new ImpactEffectBank();
        effects.Add(Effect(WoodEffect, "d", "d/texture.png"));

        Assert.Empty(ImpactDecalPlan.TexturesByBundle(materials, effects, _ => string.Empty));
    }

    [Fact]
    public void AnEffectWithNoTexturesIsDropped()
    {
        var materials = new PhysicsMaterialBank();
        materials.Add(Material("Foliage", FleshEffect));
        var effects = new ImpactEffectBank();
        effects.Add(Effect(FleshEffect, "d"));

        Assert.Empty(ImpactDecalPlan.TexturesByBundle(materials, effects, _ => "b"));
    }

    // The key has to survive being a filename: a container path is full of separators and dots, and one
    // that reached the filesystem unescaped would land in directories nothing ever creates.
    [Fact]
    public void TheCacheKeyIsSafeToUseAsAFilename()
    {
        string key = ImpactDecalPlan.CacheKey("core",
            "assets/coremasterbundle/effects/impacts/wood_withdecal_noaudio/texture.png");

        Assert.DoesNotContain('/', key);
        Assert.DoesNotContain('\\', key);
        Assert.DoesNotContain('.', key);
        Assert.DoesNotContain(':', key);
    }

    // Two bundles can ship the same container path with different pixels, so the tag has to be part of
    // the key — the same rule the texture cache follows.
    [Fact]
    public void TheCacheKeyDistinguishesBundles()
    {
        const string path = "assets/x/effects/impacts/wood/texture.png";

        Assert.NotEqual(ImpactDecalPlan.CacheKey("core", path), ImpactDecalPlan.CacheKey("mod", path));
    }

    [Fact]
    public void TheCacheKeyDistinguishesTextures()
    {
        Assert.NotEqual(ImpactDecalPlan.CacheKey("core", "a/texture_0.png"),
            ImpactDecalPlan.CacheKey("core", "a/texture_1.png"));
    }
}
