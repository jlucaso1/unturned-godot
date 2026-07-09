using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Dat;

namespace UnturnedGodot.Assets;

// Mirrors SDG.Unturned.PhysicsMaterialAsset: maps the Unity PhysicMaterial names a surface can carry to
// per-event audio definitions (FootstepWalk/FootstepRun/BipedLand/...), with a Fallback chain to a base
// material (e.g. Peaks_Grass_Dry -> Foliage).
public sealed class PhysicsMaterialAsset
{
    public Guid Guid { get; }
    public IReadOnlyList<string> UnityNames { get; }
    public Guid Fallback { get; }
    public IReadOnlyDictionary<string, string> AudioDefs { get; } // event key -> masterbundle .asset path

    private PhysicsMaterialAsset(Guid guid, List<string> unityNames, Guid fallback,
        Dictionary<string, string> audioDefs)
    {
        Guid = guid;
        UnityNames = unityNames;
        Fallback = fallback;
        AudioDefs = audioDefs;
    }

    public static bool TryParse(DatDictionary root, out PhysicsMaterialAsset asset)
    {
        asset = null!;
        if (!root.TryGetDictionary("Metadata", out DatDictionary meta) ||
            !root.TryGetDictionary("Asset", out DatDictionary data))
            return false;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return false;
        string type = meta.GetString("Type") ?? string.Empty;
        if (!type.Contains("PhysicsMaterialAsset", StringComparison.Ordinal))
            return false;

        var names = new List<string>();
        if (data.TryGetList("UnityNames", out DatList list))
            foreach (DatNode node in list.Items)
                if (node is DatValue v && v.Value.Length > 0)
                    names.Add(v.Value);

        data.TryGetGuid("Fallback", out Guid fallback);

        var audioDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetDictionary("AudioDefs", out DatDictionary defs))
            foreach (string key in defs.Keys)
                if (defs.GetString(key) is { Length: > 0 } path)
                    audioDefs[key] = path;

        asset = new PhysicsMaterialAsset(guid, names, fallback, audioDefs);
        return true;
    }
}

// The registry PhysicMaterialCustomData builds: physic-material NAME (case-insensitive, from UnityNames)
// -> asset, with audio lookups walking the Fallback chain until a definition carries the requested key.
public sealed class PhysicsMaterialBank
{
    private readonly Dictionary<string, PhysicsMaterialAsset> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, PhysicsMaterialAsset> _byGuid = new();

    public int Count => _byGuid.Count;

    public void Add(PhysicsMaterialAsset asset)
    {
        _byGuid[asset.Guid] = asset;
        foreach (string name in asset.UnityNames)
            _byName[name] = asset;
    }

    // Scans a directory tree of physics-material .assets (Bundles/Assets/PhysicsMaterials).
    public static PhysicsMaterialBank ScanDirectory(string root)
    {
        var bank = new PhysicsMaterialBank();
        if (!Directory.Exists(root))
            return bank;
        foreach (string file in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
        {
            DatDictionary parsed;
            try { parsed = DatParser.Parse(File.ReadAllText(file)); }
            catch (IOException) { continue; }
            if (TryParseFile(parsed, out PhysicsMaterialAsset asset))
                bank.Add(asset);
        }
        return bank;
    }

    private static bool TryParseFile(DatDictionary parsed, out PhysicsMaterialAsset asset) =>
        PhysicsMaterialAsset.TryParse(parsed, out asset);

    // PhysicMaterialCustomData.GetAudioDef: resolve the material by name, then walk the fallback chain
    // until some asset defines the event key. Null when the name is unknown or nothing defines the key.
    public string? FindAudioDefPath(string materialName, string key)
    {
        if (!_byName.TryGetValue(materialName, out PhysicsMaterialAsset? asset))
            return null;
        for (int hops = 0; asset != null && hops < 8; hops++) // hop cap guards a fallback cycle
        {
            if (asset.AudioDefs.TryGetValue(key, out string? path))
                return path;
            asset = asset.Fallback != Guid.Empty && _byGuid.TryGetValue(asset.Fallback, out PhysicsMaterialAsset? fb)
                ? fb
                : null;
        }
        return null;
    }
}
