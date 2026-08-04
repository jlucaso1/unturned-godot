using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Scans a bundle tree for object .dat files and indexes them by GUID (modern) and legacy id.
public sealed class ObjectAssetDatabase
{
    private readonly Dictionary<Guid, ObjectAsset> _byGuid = new();
    private readonly Dictionary<ushort, ObjectAsset> _byId = new();
    // Vehicles and their redirectors carry legacy ids from their own namespace (EAssetType.VEHICLE), which
    // overlaps the object one freely — vehicle 40 and object 40 are unrelated. Keeping them out of _byId
    // is what lets both live in this one GUID-keyed table.
    private readonly Dictionary<ushort, ObjectAsset> _vehiclesById = new();
    // Resources (the harvestables under Bundles/Trees) are a third namespace, EAssetType.RESOURCE, and it
    // overlaps the object one just as freely: 69 of the game's own resource ids also name an object, and
    // every id Monolith's trees are placed with is one of them. A tree resolved out of _byId would render
    // as somebody's house.
    private readonly Dictionary<ushort, ObjectAsset> _resourcesById = new();

    // How far a redirector chain is followed before giving up. One hop is all the shipped content uses;
    // the bound is only here so a self-referencing mod asset cannot spin.
    private const int MaxRedirectDepth = 8;

    public int Count => _byGuid.Count;

    public IEnumerable<ObjectAsset> All => _byGuid.Values;

    public ObjectAsset? ResolveByGuid(Guid guid) =>
        _byGuid.TryGetValue(guid, out ObjectAsset? a) ? a : null;

    public ObjectAsset? ResolveById(ushort id) =>
        id != 0 && _byId.TryGetValue(id, out ObjectAsset? a) ? a : null;

    // The resource namespace's own lookup, for what a pre-GUID Trees.dat places.
    public ObjectAsset? ResolveResourceById(ushort id) =>
        id != 0 && _resourcesById.TryGetValue(id, out ObjectAsset? a) ? a : null;

    // GUID wins for modern placements; the legacy id is the fallback for old ones.
    public ObjectAsset? Resolve(Guid guid, ushort id) =>
        guid != Guid.Empty ? ResolveByGuid(guid) ?? ResolveById(id) : ResolveById(id);

    // A vehicle by either identifier, following the redirector chain to the asset that owns the prefab.
    // A map's vehicle tables and the spawn tables reference redirectors far more often than vehicles
    // themselves, so resolving through them is the normal path, not the exception.
    public ObjectAsset? ResolveVehicle(Guid guid, ushort id)
    {
        ObjectAsset? asset = guid != Guid.Empty ? ResolveByGuid(guid) : null;
        asset ??= id != 0 && _vehiclesById.TryGetValue(id, out ObjectAsset? byId) ? byId : null;

        for (int depth = 0; depth < MaxRedirectDepth && asset is { Type: EObjectType.VehicleRedirector }; depth++)
            asset = ResolveByGuid(asset.RedirectTargetGuid);

        return asset is { Type: EObjectType.Vehicle } ? asset : null;
    }

    public void Add(ObjectAsset asset)
    {
        _byGuid[asset.Guid] = asset;
        if (asset.Id != 0)
            IdIndexFor(asset)[asset.Id] = asset;
    }

    // The legacy-id table this asset's namespace owns. Three of them, because Unturned's EAssetType
    // separates OBJECT, VEHICLE and RESOURCE and lets their ids collide.
    private Dictionary<ushort, ObjectAsset> IdIndexFor(ObjectAsset asset) => asset.Type switch
    {
        EObjectType.Vehicle or EObjectType.VehicleRedirector => _vehiclesById,
        EObjectType.Resource => _resourcesById,
        _ => _byId,
    };

    // Adds only what nothing has claimed yet, so a merge of several bundles keeps whichever registered
    // first. Sources are merged with the game's own content first: a workshop item is free to reuse an
    // official legacy id (they were never unique across mods), and letting it overwrite that entry made
    // an old placement resolve to an unrelated mod object. The GUID and the id are claimed
    // independently — a mod asset with a fresh GUID and a colliding id still gets its GUID indexed.
    public void AddIfAbsent(ObjectAsset asset)
    {
        _byGuid.TryAdd(asset.Guid, asset);
        if (asset.Id != 0)
            IdIndexFor(asset).TryAdd(asset.Id, asset);
    }

    // Vehicle redirectors are ".asset" files rather than ".dat", so the vehicle tree is scanned for both.
    // The object and tree trees stay on ".dat" alone: nothing in them ships as ".asset", and widening the
    // glob there would pull unrelated files into the by-id index.
    public static IReadOnlyList<string> DatOnly { get; } = new[] { "*.dat" };
    public static IReadOnlyList<string> DatAndAsset { get; } = new[] { "*.dat", "*.asset" };

    public static ObjectAssetDatabase ScanDirectory(string root) => ScanDirectory(root, DatOnly);

    public static ObjectAssetDatabase ScanDirectory(string root, IReadOnlyList<string> searchPatterns)
    {
        var db = new ObjectAssetDatabase();
        if (!Directory.Exists(root))
            return db;

        // Read + parse the (typically thousands of tiny) .dat files across cores — each file is independent
        // and the parse is pure CPU. Results are merged serially afterwards in file order, so the by-GUID /
        // by-id last-write-wins index is identical to a sequential scan (Add is not thread-safe).
        var files = new List<string>();
        foreach (string pattern in searchPatterns)
            files.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories));
        return ScanFiles(files, File.ReadAllText);
    }

    internal static ObjectAssetDatabase ScanFiles(IReadOnlyList<string> files, Func<string, string> readText)
    {
        var db = new ObjectAssetDatabase();
        var parsedAssets = new ObjectAsset?[files.Count];
        System.Threading.Tasks.Parallel.For(0, files.Count, i =>
        {
            // Localization files (English.dat, ...) carry no GUID/Type and are skipped by TryParse.
            DatDictionary parsed;
            try
            {
                parsed = DatParser.Parse(readText(files[i]));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            // The localized name isn't read here — no production code uses ObjectAsset.Name, so its
            // English.dat is read lazily only if Name is ever accessed, saving a File.Exists +
            // ReadAllText + parse per asset across every scan.
            if (ObjectAsset.TryParse(parsed, null, out var asset))
            {
                asset.Directory = Path.GetDirectoryName(files[i])!; // never null for an enumerated file path
                parsedAssets[i] = asset;
            }
        });

        foreach (ObjectAsset? asset in parsedAssets)
            if (asset != null)
                db.Add(asset);
        return db;
    }

    internal static string? ReadLocalizedName(string? directory)
    {
        if (directory == null)
            return null;
        string english = Path.Combine(directory, "English.dat");
        if (!File.Exists(english))
            return null;
        return LegacyData.Parse(File.ReadAllText(english)).GetString("Name");
    }
}
