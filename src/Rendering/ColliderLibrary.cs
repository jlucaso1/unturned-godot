using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads the per-GUID collider caches ModelExtractor wrote (<guid>.collider) into memory, so ObjectsBuilder
// can build each placed object's collision body without touching the masterbundle again.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
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
        bool deduplicate = System.Environment.GetEnvironmentVariable("UG_DEDUP_COLLIDERS") != "0";
        foreach (ExactFileGroups.Group<Guid> group in ExactFileGroups.Build(sources, deduplicate))
        {
            using FileStream stream = File.OpenRead(group.Path);
            List<CachedCollider> colliders = ColliderCache.Read(stream);
            foreach (Guid guid in group.Items) result[guid] = colliders;
        }
        return result;
    }
}
