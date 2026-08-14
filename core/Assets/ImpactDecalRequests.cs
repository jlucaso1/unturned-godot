using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;

namespace UnturnedGodot;

// What each bundle owes the decal cache, in the one shape both consumers want it in — the object
// streamer plans it into its cold-load bundle pass, and the session's deferred extraction takes whatever
// that pass did not cover.
//
// The same split, and the same reason, as MovementAudioRequests: two lists that disagreed would send the
// fallback back into a second whole-bundle decode for the difference.
public static class ImpactDecalRequests
{
    // The banks a session needs to resolve a surface to its mark. Scanned from the .dat trees, which is
    // a few hundred small files rather than a bundle read.
    public static (PhysicsMaterialBank Materials, ImpactEffectBank Effects) Banks(
        IReadOnlyList<ContentSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var materialRoots = new List<string>(sources.Count);
        var effectSources = new List<(string, string)>(sources.Count);
        foreach (ContentSource source in sources)
        {
            materialRoots.Add(Path.Combine(source.AssetsDir, "PhysicsMaterials"));
            // A source's Root IS its bundles directory: the game's own is <install>/Bundles, and a
            // workshop item's is the folder its MasterBundle.dat sits in.
            effectSources.Add((source.Root, MasterBundleConfig.Load(source.Root)?.AssetPrefix
                ?? string.Empty));
        }

        return (PhysicsMaterialBank.ScanDirectories(materialRoots),
            ImpactEffectBank.ScanSources(effectSources));
    }

    // Every decal texture the session's surfaces name, plus the crosshair icons, by bundle.
    public static List<ImpactDecalExtractor.Request> For(IReadOnlyList<ContentSource> sources,
        string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(sources);
        (PhysicsMaterialBank materials, ImpactEffectBank effects) = Banks(sources);
        return For(sources, materials, effects, cacheDirectory);
    }

    public static List<ImpactDecalExtractor.Request> For(IReadOnlyList<ContentSource> sources,
        PhysicsMaterialBank materials, ImpactEffectBank effects, string cacheDirectory)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var byDirectory = new Dictionary<string, ContentSource>(StringComparer.Ordinal);
        foreach (ContentSource source in sources)
            byDirectory[source.Root] = source;

        // An effect's textures are packaged in the bundle of whichever source SHIPPED it, and the effect
        // remembers which that was — keying by asset prefix instead let two sources declaring the same
        // one silently own each other's effects.
        ContentSource? SourceOf(ImpactEffectAsset effect) =>
            byDirectory.TryGetValue(effect.SourceDirectory, out ContentSource? owner) ? owner : null;

        var wanted = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach ((string bundlePath, HashSet<string> paths) in ImpactDecalPlan.TexturesByBundle(
            materials, effects, effect => SourceOf(effect)?.BundlePath ?? string.Empty))
        {
            wanted[bundlePath] = paths;
        }

        // The crosshair's own icons ride the same cache and the same pass. They are only ever in the
        // game's core bundle — StaticIconRef resolves them through its Resources folder, which is what
        // the core bundle carries.
        foreach (ContentSource source in sources)
        {
            if (!source.IsCore || source.BundlePath.Length == 0)
                continue;
            string prefix = MasterBundleConfig.Load(source.Root)?.AssetPrefix ?? string.Empty;
            if (prefix.Length == 0)
                continue;
            if (!wanted.TryGetValue(source.BundlePath, out HashSet<string>? paths))
                wanted[source.BundlePath] = paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string icon in HudIcons.ContainerPaths(prefix))
                paths.Add(icon);
            break;
        }

        var requests = new List<ImpactDecalExtractor.Request>(wanted.Count);
        foreach ((string bundlePath, HashSet<string> paths) in wanted)
        {
            if (bundlePath.Length == 0 || paths.Count == 0)
                continue;
            requests.Add(new ImpactDecalExtractor.Request(bundlePath, TagFor(sources, bundlePath), paths,
                cacheDirectory));
        }

        return requests;
    }

    // The cache tag a bundle's textures are filed under. Both the extraction and the runtime take it
    // through here: a key written under one tag and read under another is a texture that exists on disk
    // and is never found.
    public static string TagFor(IReadOnlyList<ContentSource> sources, string bundlePath)
    {
        ArgumentNullException.ThrowIfNull(sources);
        foreach (ContentSource source in sources)
            if (string.Equals(source.BundlePath, bundlePath, StringComparison.Ordinal))
                return source.CacheTag;
        return UnturnedGodot.Unity.TextureKey.TagFor(Path.GetFileNameWithoutExtension(bundlePath));
    }
}
