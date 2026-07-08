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

    public int Count => _byGuid.Count;

    public IEnumerable<ObjectAsset> All => _byGuid.Values;

    public ObjectAsset? ResolveByGuid(Guid guid) =>
        _byGuid.TryGetValue(guid, out ObjectAsset? a) ? a : null;

    public ObjectAsset? ResolveById(ushort id) =>
        id != 0 && _byId.TryGetValue(id, out ObjectAsset? a) ? a : null;

    // GUID wins for modern placements; the legacy id is the fallback for old ones.
    public ObjectAsset? Resolve(Guid guid, ushort id) =>
        guid != Guid.Empty ? ResolveByGuid(guid) ?? ResolveById(id) : ResolveById(id);

    public void Add(ObjectAsset asset)
    {
        _byGuid[asset.Guid] = asset;
        if (asset.Id != 0)
            _byId[asset.Id] = asset;
    }

    public static ObjectAssetDatabase ScanDirectory(string root)
    {
        var db = new ObjectAssetDatabase();
        if (!Directory.Exists(root))
            return db;

        // Read + parse the (typically thousands of tiny) .dat files across cores — each file is independent
        // and the parse is pure CPU. Results are merged serially afterwards in file order, so the by-GUID /
        // by-id last-write-wins index is identical to a sequential scan (Add is not thread-safe).
        var files = new List<string>(Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories));
        var parsedAssets = new ObjectAsset?[files.Count];
        System.Threading.Tasks.Parallel.For(0, files.Count, i =>
        {
            // Localization files (English.dat, ...) carry no GUID/Type and are skipped by TryParse.
            DatDictionary parsed;
            try
            {
                parsed = DatParser.Parse(File.ReadAllText(files[i]));
            }
            catch (IOException)
            {
                return;
            }

            // The localized name isn't read here — no production code uses ObjectAsset.Name, so its
            // English.dat is read lazily only if Name is ever accessed, saving a File.Exists +
            // ReadAllText + parse per asset across every scan.
            if (ObjectAsset.TryParse(parsed, null, out ObjectAsset asset))
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
