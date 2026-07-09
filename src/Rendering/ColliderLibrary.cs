using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Loads the per-GUID collider caches ModelExtractor wrote (<guid>.collider) into memory, so ObjectsBuilder
// can build each placed object's collision body without touching the masterbundle again.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class ColliderLibrary
{
    public static Dictionary<Guid, List<CachedCollider>> Load(string cacheDir)
    {
        var result = new Dictionary<Guid, List<CachedCollider>>();
        if (!Directory.Exists(cacheDir))
            return result;

        foreach (string path in Directory.EnumerateFiles(cacheDir, "*.collider"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid guid))
                continue;
            using FileStream s = File.OpenRead(path);
            result[guid] = ColliderCache.Read(s);
        }
        return result;
    }
}
