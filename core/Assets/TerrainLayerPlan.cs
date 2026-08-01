using System;
using System.Collections.Generic;

namespace UnturnedGodot.Assets;

// Which texture each of a map's landscape materials needs, grouped by the master bundle that carries it.
//
// A bundle costs one LZMA pass to read whatever is wanted from it, so the terrain layers and the object
// meshes have to agree on that list up front: whoever opens the bundle can then serve both. The grouping
// is pure data (the hierarchy's material GUIDs, the landscape assets, and each source's MasterBundle.dat),
// which is why it lives here rather than inside the loader that consumes it.
public static class TerrainLayerPlan
{
    // What one bundle owes: the container path to pull out, and every material GUID that uses it.
    public sealed class BundleWants
    {
        // The .masterbundle file to read.
        public string BundlePath { get; }

        // Container path (the lowercase Unity path inside the bundle) to every material that wants it.
        // Several LandscapeMaterialAssets may intentionally share one texture asset.
        public Dictionary<string, Guid[]> ByContainerPath { get; } = new(StringComparer.Ordinal);

        // Keep lists while the plan is being assembled so duplicate paths do not repeatedly copy arrays.
        private readonly Dictionary<string, List<Guid>> _pending = new(StringComparer.Ordinal);

        public BundleWants(string bundlePath) => BundlePath = bundlePath;

        internal void Add(string containerPath, Guid material)
        {
            if (!_pending.TryGetValue(containerPath, out List<Guid>? materials))
                _pending[containerPath] = materials = new List<Guid>();
            materials.Add(material);
        }

        internal void Seal()
        {
            foreach ((string path, List<Guid> materials) in _pending)
                ByContainerPath[path] = materials.ToArray();
            _pending.Clear();
        }
    }

    // Groups `needed` material GUIDs by the bundle their texture lives in. Materials with no texture, no
    // matching source, or whose source ships no bundle are left out: nothing can be read for them, and
    // counting them would keep a pass reading for bytes that are not there.
    public static Dictionary<string, BundleWants> ByBundle(IEnumerable<Guid> needed,
        IReadOnlyDictionary<Guid, LandscapeMaterialAsset> materials,
        IReadOnlyList<ContentSource> sources,
        Func<string, MasterBundleConfig?> configFor)
    {
        return ByBundle(needed, materials, new Dictionary<Guid, string>(), sources, configFor);
    }

    // `claimantRoots` identifies the source whose Assets/Landscapes tree supplied each GUID. Prefer that
    // source before matching by bundle name, because separate workshop items may legitimately reuse the
    // same Asset_Bundle_Name. If the claimant does not carry the named bundle, scan all sources as a
    // fallback so intentional cross-bundle references keep working.
    public static Dictionary<string, BundleWants> ByBundle(IEnumerable<Guid> needed,
        IReadOnlyDictionary<Guid, LandscapeMaterialAsset> materials,
        IReadOnlyDictionary<Guid, string> claimantRoots,
        IReadOnlyList<ContentSource> sources,
        Func<string, MasterBundleConfig?> configFor)
    {
        var byBundle = new Dictionary<string, BundleWants>(StringComparer.Ordinal);
        var configs = new Dictionary<string, MasterBundleConfig?>(StringComparer.Ordinal);
        var sourcesByRoot = new Dictionary<string, ContentSource>(StringComparer.Ordinal);
        foreach (ContentSource source in sources)
            sourcesByRoot.TryAdd(source.Root, source);

        MasterBundleConfig? ConfigFor(ContentSource source)
        {
            if (!configs.TryGetValue(source.Root, out MasterBundleConfig? config))
                configs[source.Root] = config = configFor(source.Root);
            return config;
        }

        bool Assign(ContentSource source, LandscapeMaterialAsset asset, Guid guid)
        {
            if (source.BundlePath.Length == 0)
                return false;

            MasterBundleConfig? config = ConfigFor(source);
            if (config == null || !config.Matches(asset.TextureBundle))
                return false;

            if (!byBundle.TryGetValue(source.BundlePath, out BundleWants? wants))
                byBundle[source.BundlePath] = wants = new BundleWants(source.BundlePath);

            wants.Add(config.ContainerPath(asset.TexturePath), guid);
            return true;
        }

        foreach (Guid guid in needed)
        {
            if (!materials.TryGetValue(guid, out LandscapeMaterialAsset? asset)
                || asset.TexturePath.Length == 0)
            {
                continue;
            }

            ContentSource? claimant = null;
            if (claimantRoots.TryGetValue(guid, out string? claimantRoot))
                sourcesByRoot.TryGetValue(claimantRoot, out claimant);

            if (claimant != null && Assign(claimant, asset, guid))
                continue;

            foreach (ContentSource source in sources)
            {
                if (ReferenceEquals(source, claimant))
                    continue;
                if (Assign(source, asset, guid))
                    break;
            }
        }

        foreach (BundleWants wants in byBundle.Values)
            wants.Seal();

        return byBundle;
    }

    // The terrain cache is keyed by material GUID, while the source bundle is what determines whether
    // the cached pixels are still valid. Keep that association from the same plan that resolved the
    // material, so rendering and extraction do not independently guess an owner.
    public static Dictionary<Guid, string> MaterialBundlePaths(
        IReadOnlyDictionary<string, BundleWants> byBundle)
    {
        var result = new Dictionary<Guid, string>();
        foreach ((string bundlePath, BundleWants wants) in byBundle)
            foreach (Guid[] materials in wants.ByContainerPath.Values)
                foreach (Guid material in materials)
                    result.TryAdd(material, bundlePath);
        return result;
    }

    // The wants for one bundle, or an empty set when it owes nothing.
    public static BundleWants For(IReadOnlyDictionary<string, BundleWants> byBundle, string bundlePath) =>
        byBundle.TryGetValue(bundlePath, out BundleWants? wants) ? wants : new BundleWants(bundlePath);
}
