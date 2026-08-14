using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// The on-disk cache of splat layer textures, one .tex per landscape material GUID.
//
// Two readers share it: the terrain build, which needs the textures to make its tile materials, and the
// object extraction pass, which is already decoding the same bundles and so produces them on the way past.
public static class TerrainLayerCache
{
    public static string Directory => ProjectSettings.GlobalizePath("user://terrain_cache");

    private static string PathFor(Guid material, string directory) =>
        Path.Combine(directory, material.ToString("N") + ".tex");

    private static string StampPathFor(Guid material, string directory) =>
        Path.Combine(directory, material.ToString("N") + ".stamp");

    private static string SourceStamp(string bundlePath)
    {
        try
        {
            FileInfo info = new(bundlePath);
            return info.Exists
                ? $"{Path.GetFullPath(bundlePath)}|{info.Length:x}|{info.LastWriteTimeUtc.Ticks:x}"
                : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool MatchesSource(Guid material, string bundlePath, string directory)
    {
        string expected = SourceStamp(bundlePath);
        if (expected.Length == 0)
            return false;

        try
        {
            return TextFile.ReadAllText(StampPathFor(material, directory)) == expected;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The GUIDs among `needed` that are not cached yet, which is exactly what a bundle pass has to produce.
    public static HashSet<Guid> Missing(IEnumerable<Guid> needed,
        IReadOnlyDictionary<Guid, string> bundlePaths) => Missing(needed, bundlePaths, Directory);

    public static HashSet<Guid> Missing(IEnumerable<Guid> needed,
        IReadOnlyDictionary<Guid, string> bundlePaths, string cacheDirectory)
    {
        var missing = new HashSet<Guid>();
        foreach (Guid guid in needed)
        {
            string path = PathFor(guid, cacheDirectory);
            if (!bundlePaths.TryGetValue(guid, out string? bundlePath)
                || !File.Exists(path) || !TextureCache.IsCurrent(path)
                || !MatchesSource(guid, bundlePath, cacheDirectory))
                missing.Add(guid);
        }
        return missing;
    }

    public static CachedTexture? Read(Guid material, string bundlePath)
    {
        string path = PathFor(material, Directory);
        if (!File.Exists(path) || !TextureCache.IsCurrent(path)
            || !MatchesSource(material, bundlePath, Directory))
            return null;

        try
        {
            using FileStream stream = File.OpenRead(path);
            return TextureCache.Read(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // corrupt entry: re-extract
        }
    }

    // Best-effort: a write failure just means the next boot extracts this texture again.
    public static void Write(Guid material, CachedTexture texture, string bundlePath) =>
        Write(material, texture, bundlePath, Directory);

    public static void Write(Guid material, CachedTexture texture, string bundlePath,
        string cacheDirectory)
    {
        string path = PathFor(material, cacheDirectory);
        // Unique per writer, because two processes may be writing the same GUID at the same time — a
        // dedicated server and a BOT_JOIN client share one user://, which is the shape this repo's own
        // end-to-end runs take.
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string stamp = SourceStamp(bundlePath);
            if (stamp.Length == 0)
                return;
            System.IO.Directory.CreateDirectory(cacheDirectory);
            // Written through a temporary and renamed into place, like every other cache file here
            // (ModelExtractor.WriteAtomically, TextureCache.RecordSource, FoliageResidencyIndex.Write).
            // Truncating the .tex in place was safe against a crash — the stamp is written afterwards, so
            // an interrupted write leaves no stamp and the entry reads as missing — but not against a
            // REWRITE by a second process: A finishing its .tex and .stamp while B has just truncated the
            // same .tex leaves a reader passing A's stamp and then reading B's half-written payload.
            using (FileStream stream = File.Create(temporary))
                TextureCache.Write(stream, texture);
            File.Move(temporary, path, overwrite: true);
            File.WriteAllText(StampPathFor(material, cacheDirectory), stamp);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }
}
