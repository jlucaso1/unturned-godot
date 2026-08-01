using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnturnedGodot.Unity;

// Records which object GUIDs an extraction pass already TRIED and could not turn into a mesh — an asset the
// masterbundle has no prefab for, a prefab with no renderable submesh, a foliage type whose mesh path does
// not resolve. Without it, "is this map's cache complete?" has no answer: those GUIDs never produce a
// <guid>.mesh, so a plain file check would report the cache cold on every single boot and re-decode the
// 1.4 GB bundle forever.
//
// The set is only meaningful for the bundle it was built from, so it carries that bundle's stamp (mtime
// ^ length, same scheme as TypeTreeCache): after a game update the misses are dropped and every GUID is
// tried again against the new data.
public static class ExtractionIndex
{
    private const uint Magic = 0x31584755; // "UGX1"
    private const uint OwnerMagic = 0x314F4755; // "UGO1"

    // Sits next to the per-GUID meshes. Keep one index per bundle: misses in the game's bundle say
    // nothing about the contents of a workshop bundle, and vice versa.
    public static string FileNameFor(string bundlePath) =>
        "extraction_" + Path.GetFileNameWithoutExtension(bundlePath) + ".index";

    // The misses recorded for this bundle, or an empty set when the file is missing, stale (a different
    // bundle), truncated or written by another format version. Never throws: a missing/garbage index only
    // means "nothing is known to be a miss", which costs one extra extraction pass at worst.
    public static HashSet<Guid> Load(string path, long stamp)
    {
        var misses = new HashSet<Guid>();
        try
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 16)
                return misses;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data) != Magic)
                return misses;
            if (BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4)) != stamp)
                return misses;

            int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            if (count < 0 || 16 + ((long)count * 16) > data.Length)
                return misses; // truncated: treat the whole index as unknown
            for (int i = 0; i < count; i++)
                misses.Add(new Guid(data.AsSpan(16 + (i * 16), 16)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            misses.Clear(); // unreadable index: fall back to "nothing known"
        }
        return misses;
    }

    // Overwrites the index with `misses` under `stamp`. Best-effort: a write failure just means the next
    // boot re-tries those GUIDs.
    public static void Save(string path, long stamp, IReadOnlyCollection<Guid> misses)
    {
        try
        {
            var data = new byte[16 + (misses.Count * 16)];
            BinaryPrimitives.WriteUInt32LittleEndian(data, Magic);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), stamp);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), misses.Count);
            int offset = 16;
            foreach (Guid guid in misses)
            {
                guid.TryWriteBytes(data.AsSpan(offset, 16));
                offset += 16;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            /* best-effort: the next load just re-tries these GUIDs */
        }
    }

    // The GUIDs this map needs that the cache cannot serve: no current-format <guid>.mesh on disk AND not
    // a known miss. A non-empty result is exactly the cold-load condition — it stays empty once a map has
    // been extracted, and becomes non-empty the moment a DIFFERENT map asks for objects nobody extracted
    // yet (which a "is the cache directory empty?" check can never notice).
    public static HashSet<Guid> MissingMeshes(string cacheDir, IEnumerable<Guid> needed,
        IReadOnlySet<Guid> misses)
        => MissingMeshes(cacheDir, needed, misses,
            guid => MeshCache.IsCurrent(Path.Combine(cacheDir, guid.ToString("N") + ".mesh")));

    // Same completeness check, but tied to the bundle that owns the GUID. Mesh GUIDs are not globally
    // unique across workshop content, so format magic alone cannot prove that a shared-cache entry came
    // from the source selected by first-claimant asset resolution.
    public static HashSet<Guid> MissingMeshes(string cacheDir, IEnumerable<Guid> needed,
        IReadOnlySet<Guid> misses, string bundlePath, long stamp)
        => MissingMeshes(cacheDir, needed, misses,
            guid => MeshBelongsTo(cacheDir, guid, bundlePath, stamp));

    private static HashSet<Guid> MissingMeshes(string cacheDir, IEnumerable<Guid> needed,
        IReadOnlySet<Guid> misses, Func<Guid, bool> isCached)
    {
        var missing = new HashSet<Guid>();
        foreach (Guid guid in needed)
        {
            if (guid == Guid.Empty || misses.Contains(guid))
                continue;
            if (!isCached(guid))
                missing.Add(guid);
        }
        return missing;
    }

    public static bool MeshBelongsTo(string cacheDir, Guid guid, string bundlePath, long stamp)
    {
        if (!MeshCache.IsCurrent(Path.Combine(cacheDir, guid.ToString("N") + ".mesh")))
            return false;

        try
        {
            byte[] data = File.ReadAllBytes(OwnerPath(cacheDir, guid));
            if (data.Length < 16 || BinaryPrimitives.ReadUInt32LittleEndian(data) != OwnerMagic ||
                BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(4)) != stamp)
                return false;

            int pathLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
            if (pathLength < 0 || 16L + pathLength != data.Length)
                return false;
            string recorded = Encoding.UTF8.GetString(data, 16, pathLength);
            return string.Equals(recorded, CanonicalBundlePath(bundlePath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    // Written only after a complete bundle scan. If the process dies between the mesh and this stamp,
    // the next boot safely treats the entry as cold and regenerates it.
    public static void RecordMeshOwner(string cacheDir, Guid guid, string bundlePath, long stamp)
    {
        try
        {
            byte[] path = Encoding.UTF8.GetBytes(CanonicalBundlePath(bundlePath));
            var data = new byte[16 + path.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(data, OwnerMagic);
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), stamp);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), path.Length);
            path.CopyTo(data.AsSpan(16));
            File.WriteAllBytes(OwnerPath(cacheDir, guid), data);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best-effort: an absent stamp causes a safe re-extraction on the next load.
        }
    }

    // A failed attempt must not leave another bundle's render mesh or collider visible under the same
    // GUID. The miss index prevents retries, while removal prevents runtime consumers loading stale data.
    public static void RemoveCachedAsset(string cacheDir, Guid guid)
    {
        foreach (string path in new[]
        {
            Path.Combine(cacheDir, guid.ToString("N") + ".mesh"),
            Path.Combine(cacheDir, guid.ToString("N") + ".collider"),
            OwnerPath(cacheDir, guid),
        })
            try { File.Delete(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static string OwnerPath(string cacheDir, Guid guid) =>
        Path.Combine(cacheDir, guid.ToString("N") + ".mesh.owner");

    private static string CanonicalBundlePath(string bundlePath) =>
        Path.GetFullPath(bundlePath);

    // The identity of the masterbundle the index was built from: its last-write time and size. Matches
    // TypeTreeCache's stamp so both caches invalidate together on a game update.
    public static long StampFor(string bundlePath)
    {
        // Exists loads the stat data and answers false for anything unreadable (missing, a directory, a
        // path the process cannot walk), so the two reads that follow cannot fail.
        var info = new FileInfo(bundlePath);
        return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
    }
}
