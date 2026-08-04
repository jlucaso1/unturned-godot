using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Pure planning for a map's asset extraction. Keeping this outside the Godot layer makes bundle
// ownership, cache completeness and foliage routing independently testable without starting the engine.
public static class ContentExtraction
{
    public sealed record BundlePlan(ContentSource Source, HashSet<Guid> Needed, HashSet<Guid> Missing,
        HashSet<long> MissingTextures, List<FoliageAsset> Foliage)
    {
        public bool NeedsExtraction => Missing.Count > 0 || MissingTextures.Count > 0;
    }

    // Every asset directory that could hold objects, trees or vehicles, for the asset-database scan. First
    // registration wins: Discover orders core before workshop content, matching asset resolution.
    public static ObjectAssetDatabase ScanAssets(IReadOnlyList<ContentSource> sources) =>
        ScanAssets(sources, ObjectAssetDatabase.ScanDirectory);

    // Scanner injection keeps the failure boundary deterministic in tests. A subscribed workshop item is
    // unrelated to the map currently being loaded, so an unreadable or concurrently removed asset tree
    // must cost only that tree, never the whole installed library.
    internal static ObjectAssetDatabase ScanAssets(IReadOnlyList<ContentSource> sources,
        Func<string, IReadOnlyList<string>, ObjectAssetDatabase> scanDirectory)
    {
        var db = new ObjectAssetDatabase();
        foreach (ContentSource source in sources)
            // Vehicle redirectors ship as ".asset", so that tree needs the wider glob; see
            // ObjectAssetDatabase.DatAndAsset.
            foreach ((string directory, IReadOnlyList<string> patterns) in new[]
            {
                (source.ObjectsDir, ObjectAssetDatabase.DatOnly),
                (source.TreesDir, ObjectAssetDatabase.DatOnly),
                (source.VehiclesDir, ObjectAssetDatabase.DatAndAsset),
                // NPC characters: no prefab of their own, but a map places them by GUID like any object,
                // and without this every one of Russia's forty resolves to nothing at all.
                (source.NpcsDir, ObjectAssetDatabase.DatOnly),
            })
            {
                ObjectAssetDatabase scanned;
                try
                {
                    scanned = scanDirectory(directory, patterns);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (ObjectAsset asset in scanned.All)
                    db.AddIfAbsent(asset);
            }

        return db;
    }

    // Assigns each needed GUID to the bundle that owns its asset declaration, then compares that
    // bundle's mesh and texture dependencies with the shared caches. Unknown GUIDs stay in the core
    // plan so a completed attempt can record them as misses instead of retrying every load.
    public static List<BundlePlan> Plan(IReadOnlyList<ContentSource> sources, string cacheDir,
        string textureCacheDir, IReadOnlyCollection<Guid> neededGuids, ObjectAssetDatabase db,
        IReadOnlyDictionary<Guid, FoliageAsset.Owned> foliage,
        IReadOnlySet<string>? bundlesOwingLayers = null)
    {
        var plans = new List<BundlePlan>();
        var byGuid = new Dictionary<Guid, int>();

        foreach (Guid guid in neededGuids)
        {
            if (guid == Guid.Empty)
                continue;

            int index = 0;
            if (db.ResolveByGuid(guid) is { Directory.Length: > 0 } asset)
                for (int i = 0; i < sources.Count; i++)
                    if (sources[i].Owns(asset.Directory))
                    {
                        index = i;
                        break;
                    }

            byGuid[guid] = index;
        }

        for (int i = 0; i < sources.Count; i++)
        {
            var needed = new HashSet<Guid>();
            foreach ((Guid guid, int index) in byGuid)
                if (index == i)
                    needed.Add(guid);

            var foliageAssets = new List<FoliageAsset>();
            foreach (FoliageAsset.Owned owned in foliage.Values)
                if (owned.SourceIndex == i)
                {
                    foliageAssets.Add(owned.Asset);
                    needed.Add(owned.Asset.Guid);
                }

            bool owesLayers = bundlesOwingLayers?.Contains(sources[i].BundlePath) == true;
            if ((needed.Count == 0 && !owesLayers) || sources[i].BundlePath.Length == 0)
                continue;

            long sourceStamp = ExtractionIndex.StampFor(sources[i].BundlePath);
            HashSet<Guid> misses = ExtractionIndex.Load(
                Path.Combine(cacheDir, ExtractionIndex.FileNameFor(sources[i].BundlePath)), sourceStamp);
            plans.Add(new BundlePlan(sources[i], needed,
                ExtractionIndex.MissingMeshes(cacheDir, needed, misses, sources[i].BundlePath, sourceStamp),
                TextureDependencyIndex.MissingTextureIds(cacheDir, textureCacheDir,
                    sources[i].CacheTag, needed, sources[i].BundlePath, sourceStamp), foliageAssets));
        }

        return plans;
    }

    public static int TotalMissing(IReadOnlyList<BundlePlan> plans)
    {
        int total = 0;
        foreach (BundlePlan plan in plans)
            total += plan.Missing.Count + plan.MissingTextures.Count;
        return total;
    }

    // The engine layer decides where to emit these messages; formatting the facts is still pure logic.
    public static List<string> PendingReports(IReadOnlyList<BundlePlan> plans)
    {
        var reports = new List<string>();
        foreach (BundlePlan plan in plans)
            if (plan.NeedsExtraction)
                reports.Add($"[extract] {plan.Source.Name}: {plan.Missing.Count}/{plan.Needed.Count} "
                    + $"meshes and {plan.MissingTextures.Count} textures not cached");
        return reports;
    }
}
