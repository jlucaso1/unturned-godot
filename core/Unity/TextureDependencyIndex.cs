using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace UnturnedGodot.Unity;

// Answers whether the texture half of the shared model cache is complete for one map and bundle.
// A mesh can be current while one of its textures is absent (the game may have quit halfway through the
// streaming tail), so mesh presence alone is not a valid warm-cache signal.
public static class TextureDependencyIndex
{
    public static HashSet<long> NeededTextureIds(string meshCacheDir, string bundleTag,
        IEnumerable<Guid> neededGuids)
    {
        var ids = new HashSet<long>();
        foreach (string key in NeededTextureKeys(meshCacheDir, bundleTag, neededGuids, includeSecondary: false))
            if (TextureKey.TryParse(key, out _, out long id))
                ids.Add(id);
        return ids;
    }

    private static HashSet<string> NeededTextureKeys(string meshCacheDir, string bundleTag,
        IEnumerable<Guid> neededGuids, bool includeSecondary)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Guid guid in neededGuids)
        {
            if (guid == Guid.Empty)
                continue;
            // Both cached levels: a texture worn only by the authored lower level is just as needed, and
            // leaving it out of the plan is what would make that level render untextured at distance.
            string stem = Path.Combine(meshCacheDir, guid.ToString("N"));
            AddKeysFrom(stem + ".mesh", keys, bundleTag, includeSecondary);
            AddKeysFrom(stem + MeshCache.Lod1Suffix, keys, bundleTag, includeSecondary);
        }
        return keys;
    }

    private static void AddKeysFrom(string path, HashSet<string> keys, string bundleTag,
        bool includeSecondary)
    {
        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        if (!MeshCache.IsCurrent(data))
            return;
        try
        {
            (_, _, _, List<CachedSubmesh> submeshes) = MeshCache.Read(data);
            foreach (CachedSubmesh sm in submeshes)
                if (TextureKey.TryParse(sm.TextureKey, out string owner, out _)
                    && (string.Equals(owner, bundleTag, StringComparison.Ordinal)
                        || (includeSecondary && IsBundleFileTag(owner, bundleTag))))
                {
                    keys.Add(sm.TextureKey);
                }
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentOutOfRangeException
            or IndexOutOfRangeException or OverflowException)
        {
            // A corrupt cache entry is already unusable as a mesh. Let mesh completeness drive its
            // replacement; it must not prevent the remaining valid entries from being inspected.
        }
    }

    public static HashSet<long> MissingTextureIds(string meshCacheDir, string textureCacheDir,
        string bundleTag, IEnumerable<Guid> neededGuids, string bundlePath, long sourceStamp)
    {
        HashSet<string> keys = NeededTextureKeys(meshCacheDir, bundleTag, neededGuids, includeSecondary: true);
        keys.RemoveWhere(key => TextureCache.IsCurrentForSource(
            Path.Combine(textureCacheDir, key + ".tex"), bundlePath, sourceStamp));
        var ids = new HashSet<long>();
        foreach (string key in keys)
            if (TextureKey.TryParse(key, out _, out long id))
                ids.Add(id);
        return ids;
    }

    // The fallback two-pass extractor signals its scene between the mesh and texture passes. Remove only
    // dependencies whose source proof is stale before that signal, so the main thread cannot briefly
    // upload pixels from the previous bundle revision while the replacement pass is starting.
    public static int RemoveStaleTextures(string meshCacheDir, string textureCacheDir, string bundleTag,
        IEnumerable<Guid> neededGuids, string bundlePath, long sourceStamp)
    {
        int removed = 0;
        foreach (string key in NeededTextureKeys(meshCacheDir, bundleTag, neededGuids, includeSecondary: true))
        {
            string path = Path.Combine(textureCacheDir, key + ".tex");
            if (!File.Exists(path) || TextureCache.IsCurrentForSource(path, bundlePath, sourceStamp))
                continue;
            TextureCache.Remove(path);
            removed++;
        }
        return removed;
    }

    // StreamExtract names the first SerializedFile with the base tag and later files with -2, -3, ... .
    // Match only those generated tags, not another bundle whose name merely shares the same prefix.
    private static bool IsBundleFileTag(string owner, string bundleTag)
    {
        if (string.Equals(owner, bundleTag, StringComparison.Ordinal))
            return true;

        string prefix = bundleTag + "-";
        return owner.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(owner.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture,
                out int fileNumber)
            && fileNumber >= 2;
    }
}
