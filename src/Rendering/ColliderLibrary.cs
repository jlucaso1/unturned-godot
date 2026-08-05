using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads the per-GUID collider caches ModelExtractor wrote (<guid>.collider) into memory, so ObjectsBuilder
// can build each placed object's collision body without touching the masterbundle again.
public static class ColliderLibrary
{
    // `only` — the GUIDs the map places — addresses the cache by name instead of scanning it, so the
    // collision shapes of every other map sharing this cache are never read or built.
    public static Dictionary<Guid, List<CachedCollider>> Load(string cacheDir, IReadOnlySet<Guid>? only = null)
    {
        var result = new Dictionary<Guid, List<CachedCollider>>();
        if (!Directory.Exists(cacheDir))
            return result;

        var sources = new List<(Guid Item, string Path)>();
        if (only != null)
        {
            foreach (Guid guid in only)
            {
                if (guid == Guid.Empty)
                    continue;
                string path = Path.Combine(cacheDir, guid.ToString("N") + ".collider");
                if (!File.Exists(path))
                    continue;
                sources.Add((guid, path));
            }
        }
        else
            foreach (string path in Directory.EnumerateFiles(cacheDir, "*.collider"))
            {
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid guid))
                    continue;
                sources.Add((guid, path));
            }
        bool deduplicate = EnvFlag.IsOn(System.Environment.GetEnvironmentVariable("UG_DEDUP_COLLIDERS"), whenUnset: true);
        int skipped = 0;
        foreach (ExactFileGroups.Group<Guid> group in ExactFileGroups.Build(sources, deduplicate))
        {
            // A cache file that is stale, truncated or unreadable must not take the map down with it. Skip
            // the GUID — it loads without collision this run. This mirrors what ModelLibrary.Prepare does
            // for a mesh; before it, an unguarded read here faulted the whole object build and dumped the
            // player back to the menu, permanently, because nothing invalidated the bad file.
            //
            // Invalidating is the other half, and it has to happen here rather than in the completeness
            // check. That check is a header test, so corruption that leaves the file's length intact — a
            // flipped count, a mangled collider body — still reads as current, and the GUID would be
            // skipped on every later load with nothing rewriting it. A read is the only thing that knows
            // the payload is bad, so the read is what marks the asset for re-extraction. The mesh goes
            // with it (RemoveCachedAsset drops the whole entry), which costs one extra re-extraction and
            // is safe here: ModelLibrary has already loaded its meshes into memory by this point.
            List<CachedCollider> colliders;
            try
            {
                using FileStream stream = File.OpenRead(group.Path);
                colliders = ColliderCache.Read(stream);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                or ArgumentException or OverflowException)
            {
                skipped += group.Items.Count;
                foreach (Guid guid in group.Items)
                    ExtractionIndex.RemoveCachedAsset(cacheDir, guid);
                continue;
            }

            foreach (Guid guid in group.Items) result[guid] = colliders;
        }

        if (skipped > 0)
            Log.Print($"[colliders] dropped {skipped} unreadable cache entr{(skipped == 1 ? "y" : "ies")}; " +
                "those objects load without collision this run and re-extract on the next.");
        return result;
    }
}
