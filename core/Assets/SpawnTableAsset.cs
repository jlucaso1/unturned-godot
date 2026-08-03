using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Ports SDG.Unturned.SpawnAsset: a weighted list of children, each of which is either the asset to spawn
// or another spawn table to descend into. Unturned resolves items, animals, carepackages and vehicles
// through the same type, and their legacy ids share one namespace (EAssetType.SPAWN).
//
// Roots (a table asking to be inserted into a parent's list, optionally zeroing the parent's own children)
// are deliberately not ported: no shipped spawn table declares one, and honouring them would mean loading
// every category's tables to find out who wants into whose list.
public sealed class SpawnTableAsset
{
    // One child. Exactly one of the three targets is set: a GUID (which may itself name a spawn table), a
    // legacy id in the spawned category's namespace, or a legacy id in the spawn-table namespace.
    public readonly record struct Entry(Guid TargetGuid, ushort AssetId, ushort SpawnId, int Weight);

    public Guid Guid { get; }
    public ushort Id { get; }

    // Children with the cumulative share of a [0,1) roll each one claims, ascending, so a roll picks the
    // first child it falls under. Zero-weight and target-less children are dropped at parse time, exactly
    // as Unturned rejects them.
    public IReadOnlyList<Entry> Entries { get; }
    public IReadOnlyList<float> CumulativeWeights { get; }

    private SpawnTableAsset(Guid guid, ushort id, List<Entry> entries)
    {
        Guid = guid;
        Id = id;
        Entries = entries;

        // Ports sortAndNormalizeWeights: heaviest first, then the running share of the total. The order
        // only matters for how far a roll walks, but keeping it makes the ladder identical to the game's.
        entries.Sort(static (a, b) => b.Weight.CompareTo(a.Weight));
        float total = 0f;
        foreach (Entry entry in entries)
            total += entry.Weight;
        var cumulative = new float[entries.Count];
        float running = 0f;
        for (int i = 0; i < entries.Count; i++)
        {
            running += entries[i].Weight;
            cumulative[i] = total > 0f ? running / total : 1f;
        }
        CumulativeWeights = cumulative;
    }

    // The child a [0,1) roll lands on, or null for an empty table (which curated maps do ship).
    public Entry? Pick(float roll)
    {
        for (int i = 0; i < Entries.Count; i++)
            if (roll < CumulativeWeights[i] || i == Entries.Count - 1)
                return Entries[i];
        return null;
    }

    public static bool TryParse(DatDictionary root, [MaybeNullWhen(false)] out SpawnTableAsset asset)
    {
        asset = null;
        DatDictionary identity = root.TryGetDictionary("Metadata", out DatDictionary? md) ? md : root;
        DatDictionary data = root.TryGetDictionary("Asset", out DatDictionary? a) ? a : root;

        if (!identity.TryGetGuid("GUID", out Guid guid))
            return false;
        string type = identity.GetString("Type") ?? string.Empty;
        if (!type.Equals("Spawn", StringComparison.OrdinalIgnoreCase)
            && !type.Contains("SpawnAsset", StringComparison.Ordinal))
        {
            return false;
        }

        data.TryGetUInt16("ID", out ushort id);

        var entries = new List<Entry>();
        if (data.TryGetList("Tables", out DatList? list))
        {
            foreach (DatNode node in list.Items)
                if (node is DatDictionary child && TryReadEntry(child, "Guid", "LegacyAssetId",
                        "LegacySpawnId", "Weight", out Entry entry))
                {
                    entries.Add(entry);
                }
        }
        else if (data.TryGetUInt16("Tables", out ushort count))
        {
            for (int i = 0; i < count; i++)
                if (TryReadEntry(data, $"Table_{i}_GUID", $"Table_{i}_Asset_ID", $"Table_{i}_Spawn_ID",
                        $"Table_{i}_Weight", out Entry entry))
                {
                    entries.Add(entry);
                }
        }

        asset = new SpawnTableAsset(guid, id, entries);
        return true;
    }

    private static bool TryReadEntry(DatDictionary data, string guidKey, string assetIdKey,
        string spawnIdKey, string weightKey, out Entry entry)
    {
        data.TryGetGuid(guidKey, out Guid target);
        data.TryGetUInt16(assetIdKey, out ushort assetId);
        data.TryGetUInt16(spawnIdKey, out ushort spawnId);
        data.TryGetInt32(weightKey, out int weight);

        entry = new Entry(target, assetId, spawnId, weight);
        return weight > 0 && (target != Guid.Empty || assetId != 0 || spawnId != 0);
    }
}

// The spawn tables of one content tree, indexed the two ways a reference can name them.
public sealed class SpawnTableDatabase
{
    private readonly Dictionary<Guid, SpawnTableAsset> _byGuid = new();
    private readonly Dictionary<ushort, SpawnTableAsset> _byId = new();

    // Recursion bound, as SpawnTableTool.Resolve applies it: a table that names itself (directly or round
    // a cycle) would otherwise never settle.
    private const int MaxDepth = 32;

    public int Count => _byGuid.Count;

    public IEnumerable<SpawnTableAsset> All => _byGuid.Values;

    public SpawnTableAsset? ByGuid(Guid guid) =>
        guid != Guid.Empty && _byGuid.TryGetValue(guid, out SpawnTableAsset? t) ? t : null;

    public SpawnTableAsset? ById(ushort id) =>
        id != 0 && _byId.TryGetValue(id, out SpawnTableAsset? t) ? t : null;

    // First registration wins, matching how sources are merged everywhere else: the game's own content is
    // scanned first, so a workshop item cannot claim an official table's id out from under a map.
    public void AddIfAbsent(SpawnTableAsset table)
    {
        _byGuid.TryAdd(table.Guid, table);
        if (table.Id != 0)
            _byId.TryAdd(table.Id, table);
    }

    // Ports SpawnTableTool.Resolve: descend through spawn tables until a child names something that is not
    // one, and return that. `roll` supplies the [0,1) value for each level. The result is whatever the
    // table pointed at — for vehicles that is a vehicle asset or one of its redirectors.
    public (Guid Guid, ushort AssetId) Resolve(SpawnTableAsset table, Func<float> roll)
    {
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (table.Pick(roll()) is not { } entry)
                return (Guid.Empty, 0);

            if (entry.SpawnId != 0)
            {
                if (ById(entry.SpawnId) is not { } next)
                    return (Guid.Empty, 0);
                table = next;
            }
            else if (entry.AssetId != 0)
            {
                return (Guid.Empty, entry.AssetId);
            }
            else if (ByGuid(entry.TargetGuid) is { } nested)
            {
                table = nested; // a GUID naming another table descends like a legacy spawn id
            }
            else
            {
                return (entry.TargetGuid, 0);
            }
        }

        return (Guid.Empty, 0); // a cycle: the original gives up here too
    }

    // A table written in the newer layout ships as ".asset" rather than ".dat" — the game's own fishing
    // tables already do — and TryParse reads both, so the scan has to see both. Enumerating only ".dat"
    // would leave such a table unfound, and a parent naming it by GUID would then take that GUID for the
    // asset to spawn rather than the table to descend into.
    private static readonly string[] SearchPatterns = { "*.dat", "*.asset" };

    // Scans a spawn-table tree. Every category's tables share one id namespace, so a caller that only
    // needs one of them may scan just that subtree — which is what the vehicle path does, because the
    // shipped vehicle tables only ever reference each other.
    public static SpawnTableDatabase ScanDirectory(string root)
    {
        var db = new SpawnTableDatabase();
        if (!Directory.Exists(root))
            return db;

        try
        {
            foreach (string file in EnumerateTableFiles(root))
            {
                DatDictionary parsed;
                try { parsed = DatParser.Parse(File.ReadAllText(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

                if (SpawnTableAsset.TryParse(parsed, out SpawnTableAsset? table))
                    db.AddIfAbsent(table);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Keep what was read; the caller carries on with the next source.
        }

        return db;
    }

    private static IEnumerable<string> EnumerateTableFiles(string root)
    {
        foreach (string pattern in SearchPatterns)
            foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                yield return file;
    }
}
