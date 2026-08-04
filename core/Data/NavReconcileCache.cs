using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace UnturnedGodot.Data;

// Persistent result of the expensive collision-vs-navmesh probe. The cache stores only the final triangle
// indices; it never substitutes a different algorithm. Its fingerprint covers every file in the map plus
// every realised collider cache, and AlgorithmVersion invalidates results when collision/reachability code
// changes. A miss or malformed entry simply falls back to the full reconciliation.
public static class NavReconcileCache
{
    private const uint Magic = 0x524E4755; // "UGNR" little-endian
    private const int FormatVersion = 2;
    // 3: the surface probe moved off the physics server onto CollisionField for everything but the faces
    // whose verdict is close enough to the step threshold to need confirming.
    // 4: MEDIUM fence assets joined the World field. An old cache was measured without those barriers
    // and would restore walkable faces through them, so it cannot be reused even though collider bytes
    // and map placements are otherwise unchanged.
    // 5: the fingerprint also carries each selected asset's effective collision layer. Fence category
    // metadata can change while its collider cache stays byte-for-byte identical.
    public const int AlgorithmVersion = 5;

    public static string Fingerprint(string levelDir, string colliderCacheDir)
        => Fingerprint(levelDir, colliderCacheDir, selectedColliders: null);

    // The two-argument overload is retained for tools that intentionally fingerprint the whole shared
    // collider cache. Runtime reconciliation passes the map's selected GUIDs so another map's cache entries
    // cannot invalidate this map's expensive probe result.
    public static string Fingerprint(string levelDir, string colliderCacheDir,
        IReadOnlySet<Guid>? selectedColliders,
        IReadOnlyDictionary<Guid, uint>? effectiveCollisionPolicies = null)
    {
        var manifest = new StringBuilder();
        manifest.Append("nav-reconcile:").Append(AlgorithmVersion).Append('\n');
        AppendTree(manifest, "level", levelDir, "*");
        if (selectedColliders == null)
            AppendTree(manifest, "collider", colliderCacheDir, "*.collider");
        else
            AppendSelectedColliders(manifest, colliderCacheDir, selectedColliders);
        if (selectedColliders != null)
            AppendCollisionPolicies(manifest, selectedColliders, effectiveCollisionPolicies);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest.ToString()))).ToLowerInvariant();
    }

    public static string MapKey(string levelDir)
    {
        string canonical = Path.GetFullPath(levelDir).TrimEnd(Path.DirectorySeparatorChar);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
    }

    public static string WithStepOffset(string fingerprint, float stepOffset) =>
        $"{fingerprint}:step={BitConverter.SingleToInt32Bits(stepOffset):x8}";

    // How the surfaces behind a cached verdict were measured. Neither of these is an input to the
    // geometry, but both decide which faces the physics server settled and which the CPU field did, so a
    // cache written under one and read under the other describes a measurement that was never taken.
    // Widening the margin to check a suspicion, or turning the field off to compare against it, has to
    // recompute rather than hand back the answer from the other setting.
    public static string WithProbeSettings(string fingerprint, bool cpuField, float confirmMargin) =>
        cpuField
            ? $"{fingerprint}:margin={BitConverter.SingleToInt32Bits(confirmMargin):x8}"
            : $"{fingerprint}:server-only";

    private static void AppendTree(StringBuilder manifest, string label, string root, string pattern)
    {
        if (!Directory.Exists(root))
        {
            manifest.Append(label).Append(":missing\n");
            return;
        }

        string[] files = Directory.GetFiles(root, pattern, SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string path in files)
        {
            var info = new FileInfo(path);
            string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            manifest.Append(label).Append('/').Append(relative).Append('|')
                .Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
    }

    private static void AppendSelectedColliders(StringBuilder manifest, string root,
        IReadOnlySet<Guid> selected)
    {
        var guids = new List<Guid>(selected);
        guids.Sort();
        foreach (Guid guid in guids)
        {
            if (guid == Guid.Empty)
                continue;
            string path = Path.Combine(root, guid.ToString("N") + ".collider");
            manifest.Append("collider/").Append(guid.ToString("N")).Append('|');
            if (!File.Exists(path))
            {
                manifest.Append("missing\n");
                continue;
            }

            var info = new FileInfo(path);
            manifest.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
        }
    }

    private static void AppendCollisionPolicies(StringBuilder manifest, IReadOnlySet<Guid> selected,
        IReadOnlyDictionary<Guid, uint>? policies)
    {
        var guids = new List<Guid>(selected);
        guids.Sort();
        foreach (Guid guid in guids)
        {
            if (guid == Guid.Empty)
                continue;
            manifest.Append("policy/").Append(guid.ToString("N")).Append('|');
            if (policies != null && policies.TryGetValue(guid, out uint layer))
                manifest.Append(layer.ToString("x8"));
            else
                manifest.Append("missing");
            manifest.Append('\n');
        }
    }

    public static void Write(Stream stream, string fingerprint, IReadOnlyList<HashSet<int>> unreachable,
        IReadOnlyList<int> triangleCounts)
    {
        var complete = new HashSet<int>?[unreachable.Count];
        for (int i = 0; i < unreachable.Count; i++) complete[i] = unreachable[i];
        WritePartial(stream, fingerprint, complete, triangleCounts);
    }

    // A null flag has not been reconciled yet. Checkpoints use the same validation as complete caches,
    // but TryRead intentionally rejects them until every flag is present.
    public static void WritePartial(Stream stream, string fingerprint,
        IReadOnlyList<HashSet<int>?> unreachable, IReadOnlyList<int> triangleCounts)
    {
        if (unreachable.Count != triangleCounts.Count)
            throw new ArgumentException("Flag result count must match triangle count metadata.");

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(fingerprint);
        writer.Write(unreachable.Count);
        for (int flag = 0; flag < unreachable.Count; flag++)
        {
            writer.Write(triangleCounts[flag]);
            HashSet<int>? completed = unreachable[flag];
            writer.Write(completed != null);
            if (completed == null)
                continue;
            int[] sorted = new int[completed.Count];
            completed.CopyTo(sorted);
            Array.Sort(sorted);
            writer.Write(sorted.Length);
            foreach (int triangle in sorted)
            {
                if ((uint)triangle >= (uint)triangleCounts[flag])
                    throw new ArgumentOutOfRangeException(nameof(unreachable), triangle,
                        "Dropped triangle is outside its source flag.");
                writer.Write(triangle);
            }
        }
    }

    // Reads the self-describing portion needed by diagnostics before they call TryRead. Partial entries
    // omit the rejected-count field entirely, so this must walk the completion marker rather than treating
    // every flag as a completed one. It also validates skipped indices while avoiding retained HashSets.
    public static bool TryReadMetadata(Stream stream, out string fingerprint, out int[] triangleCounts)
    {
        fingerprint = "";
        triangleCounts = Array.Empty<int>();
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion)
                return false;
            string readFingerprint = reader.ReadString();
            int flags = reader.ReadInt32();
            if (flags < 0)
                return false;

            var counts = new int[flags];
            for (int flag = 0; flag < flags; flag++)
            {
                int triangles = reader.ReadInt32();
                if (triangles < 0)
                    return false;
                counts[flag] = triangles;

                bool completed = reader.ReadBoolean();
                if (!completed)
                    continue;

                int rejected = reader.ReadInt32();
                if (rejected < 0 || rejected > triangles)
                    return false;
                for (int i = 0; i < rejected; i++)
                    if ((uint)reader.ReadInt32() >= (uint)triangles)
                        return false;
            }

            fingerprint = readFingerprint;
            triangleCounts = counts;
            return true;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or InvalidDataException
            or ArgumentException)
        {
            fingerprint = "";
            triangleCounts = Array.Empty<int>();
            return false;
        }
    }

    public static bool TryRead(Stream stream, string fingerprint, IReadOnlyList<int> triangleCounts,
        out List<HashSet<int>> unreachable)
    {
        unreachable = new List<HashSet<int>>();
        if (!TryReadPartial(stream, fingerprint, triangleCounts, out List<HashSet<int>?> partial))
            return false;
        var complete = new List<HashSet<int>>(partial.Count);
        foreach (HashSet<int>? flag in partial)
        {
            if (flag == null)
                return false;
            complete.Add(flag);
        }
        unreachable = complete;
        return true;
    }

    public static bool TryReadPartial(Stream stream, string fingerprint, IReadOnlyList<int> triangleCounts,
        out List<HashSet<int>?> unreachable)
    {
        unreachable = new List<HashSet<int>?>();
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != Magic || reader.ReadInt32() != FormatVersion
                || reader.ReadString() != fingerprint)
                return false;
            int flags = reader.ReadInt32();
            if (flags != triangleCounts.Count)
                return false;

            var result = new List<HashSet<int>?>(flags);
            for (int flag = 0; flag < flags; flag++)
            {
                int triangles = reader.ReadInt32();
                bool completed = reader.ReadBoolean();
                if (triangles != triangleCounts[flag])
                    return false;
                if (!completed)
                {
                    result.Add(null);
                    continue;
                }
                int count = reader.ReadInt32();
                if (count < 0 || count > triangles)
                    return false;
                var dropped = new HashSet<int>();
                for (int i = 0; i < count; i++)
                {
                    int triangle = reader.ReadInt32();
                    if ((uint)triangle >= (uint)triangles || !dropped.Add(triangle))
                        return false;
                }
                result.Add(dropped);
            }
            unreachable = result;
            return true;
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or InvalidDataException
            or ArgumentException)
        {
            unreachable = new List<HashSet<int>?>();
            return false;
        }
    }
}
