using System;
using System.Collections.Generic;
using UnturnedGodot.Data;

namespace UnturnedGodot.Assets;

// Which decal textures a session owes each bundle, derived once from the two banks.
//
// Same shape and same reason as MovementAudioPlan: the extraction and the runtime have to agree about
// exactly which container paths are wanted, or the runtime looks for a texture nothing ever pulled out of
// the bundle and every punch lands unmarked.
//
// The set is small — the shipped game's surfaces name a dozen effects between them, and several share one
// — so this is a handful of paths rather than the hundreds the texture streaming deals in.
public static class ImpactDecalPlan
{
    // Every surface the bank knows, resolved through its fallback chain to an effect, resolved to that
    // effect's candidate textures. Keyed by the bundle those textures live in.
    //
    // `bundleOf` names the bundle an effect's textures were packaged in — the source that shipped the
    // effect, which for a workshop surface falling back to a core effect is the CORE bundle rather than
    // the mod's. Returning an empty string drops the effect, which is what happens for a source whose
    // bundle could not be resolved.
    public static Dictionary<string, HashSet<string>> TexturesByBundle(PhysicsMaterialBank materials,
        ImpactEffectBank effects, Func<ImpactEffectAsset, string> bundleOf)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(effects);
        ArgumentNullException.ThrowIfNull(bundleOf);

        var byBundle = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var seen = new HashSet<Guid>();
        foreach (string surface in materials.Names)
        {
            Guid guid = materials.FindImpactEffect(surface);
            if (guid == Guid.Empty || !seen.Add(guid))
                continue; // several surfaces share one effect; plan it once
            if (effects.Find(guid) is not { } effect || effect.DecalTextureCandidates.Count == 0)
                continue;

            string bundle = bundleOf(effect);
            if (bundle.Length == 0)
                continue;
            if (!byBundle.TryGetValue(bundle, out HashSet<string>? paths))
                byBundle[bundle] = paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in effect.DecalTextureCandidates)
                paths.Add(candidate);
        }

        return byBundle;
    }

    // The cache key one decal texture is filed under: a filename, so it has to survive being one, and it
    // has to name exactly one texture.
    //
    // A HASH of the container path rather than the path with its separators replaced. That was the first
    // version and it is not injective — "effects/a_b/texture.png" and "effects/a/b/texture.png" flatten
    // to the same key, so one .tex overwrites the other and both effects then draw whichever was written
    // last. Distinct paths have to stay distinct, and the path is not needed back again.
    //
    // The bundle TAG stays readable, for the same reason TextureKey keeps it: two sources can ship the
    // same path with different pixels, and a cache directory that can be read by eye is worth the bytes.
    public static string CacheKey(string bundleTag, string containerPath)
    {
        ArgumentNullException.ThrowIfNull(bundleTag);
        ArgumentNullException.ThrowIfNull(containerPath);

        // Truncated to 32 hex characters — 128 bits, which is far past any collision this could meet: a
        // bundle names a few dozen decal textures.
        return $"decal_{bundleTag}_{ExactContentKey.Bytes(System.Text.Encoding.UTF8.GetBytes(containerPath))[..32].ToLowerInvariant()}";
    }
}
