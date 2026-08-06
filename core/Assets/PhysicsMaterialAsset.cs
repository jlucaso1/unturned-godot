using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;

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

    // WipDoNotUseTemp_BulletImpactEffect: the EffectAsset a hit on this surface spawns, by GUID. Named
    // exactly that in the shipped assets — Nelson's own note that the field is provisional — and it is
    // what PhysicMaterialCustomData.WipDoNotUseTemp_GetBulletImpactEffect reads, which a punch reaches
    // through ReceiveSpawnLegacyImpact.
    //
    // Guid.Empty for a surface that names none, and about half of them do not. That is not a gap: those
    // surfaces leave no mark in the game either.
    public Guid ImpactEffect { get; }

    // The directory this asset was scanned from. The audio it names lives in the bundle of whichever
    // content source owns that directory, which is not always the game's own.
    public string Directory { get; internal set; } = string.Empty;

    private PhysicsMaterialAsset(Guid guid, List<string> unityNames, Guid fallback,
        Dictionary<string, string> audioDefs, Guid impactEffect)
    {
        Guid = guid;
        UnityNames = unityNames;
        Fallback = fallback;
        AudioDefs = audioDefs;
        ImpactEffect = impactEffect;
    }

    public static bool TryParse(DatDictionary root, [MaybeNullWhen(false)] out PhysicsMaterialAsset asset)
    {
        asset = null;
        if (!root.TryGetDictionary("Metadata", out var meta) ||
            !root.TryGetDictionary("Asset", out var data))
            return false;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return false;
        string type = meta.GetString("Type") ?? string.Empty;
        if (!type.Contains("PhysicsMaterialAsset", StringComparison.Ordinal))
            return false;

        var names = new List<string>();
        if (data.TryGetList("UnityNames", out var list))
            foreach (DatNode node in list.Items)
                if (node is DatValue v && v.Value.Length > 0)
                    names.Add(v.Value);

        data.TryGetGuid("Fallback", out Guid fallback);

        var audioDefs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (data.TryGetDictionary("AudioDefs", out var defs))
            foreach (string key in defs.Keys)
                if (defs.GetString(key) is { Length: > 0 } path)
                    audioDefs[key] = path;

        data.TryGetGuid("WipDoNotUseTemp_BulletImpactEffect", out Guid impactEffect);

        asset = new PhysicsMaterialAsset(guid, names, fallback, audioDefs, impactEffect);
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

    // Every Unity material name the bank can resolve. The audio extraction walks these rather than a
    // fixed list of the game's own surfaces: a workshop landscape names its own material, and a name the
    // extraction never visited stayed out of the audio cache and played nothing.
    public IEnumerable<string> Names => _byName.Keys;

    // Adds only what nothing has claimed yet. The GUID and each Unity alias are claimed independently, so
    // a mod asset with a fresh GUID still registers under it even when one of its aliases is taken.
    public void AddIfAbsent(PhysicsMaterialAsset asset)
    {
        _byGuid.TryAdd(asset.Guid, asset);
        foreach (string name in asset.UnityNames)
            _byName.TryAdd(name, asset);
    }

    public void Add(PhysicsMaterialAsset asset)
    {
        _byGuid[asset.Guid] = asset;
        foreach (string name in asset.UnityNames)
            _byName[name] = asset;
    }

    // Scans a directory tree of physics-material .assets (Bundles/Assets/PhysicsMaterials).
    // Merged across several asset trees (the game's and each workshop mod's), so a mod's own surfaces
    // resolve to footstep sounds like the game's do.
    public static PhysicsMaterialBank ScanDirectories(IEnumerable<string> roots)
        => ScanDirectories(roots, ScanDirectory);

    internal static PhysicsMaterialBank ScanDirectories(IEnumerable<string> roots,
        Func<string, PhysicsMaterialBank> scanDirectory)
    {
        // First claimant wins, and the roots arrive with the game's own first: a workshop item is free to
        // reuse an official GUID or a Unity alias like "Concrete", and letting it take that registration
        // sent an official map's footsteps through the mod's asset — the wrong sound, looked for in the
        // wrong bundle. Same rule the object database follows.
        var merged = new PhysicsMaterialBank();
        foreach (string root in roots)
        {
            PhysicsMaterialBank scanned;
            try { scanned = scanDirectory(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (PhysicsMaterialAsset asset in scanned._byGuid.Values)
                merged.AddIfAbsent(asset);
        }

        return merged;
    }

    public static PhysicsMaterialBank ScanDirectory(string root)
    {
        var bank = new PhysicsMaterialBank();
        if (!Directory.Exists(root))
            return bank;
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                DatDictionary parsed;
                try { parsed = DatParser.Parse(TextFile.ReadAllText(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (TryParseFile(parsed, out var asset))
                {
                    asset.Directory = Path.GetDirectoryName(file) ?? root;
                    bank.Add(asset);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return bank;
    }

    private static bool TryParseFile(DatDictionary parsed,
        [MaybeNullWhen(false)] out PhysicsMaterialAsset asset) =>
        PhysicsMaterialAsset.TryParse(parsed, out asset);

    // PhysicMaterialCustomData.WipDoNotUseTemp_GetBulletImpactEffect: the same fallback walk the audio
    // takes, for the effect a hit on this surface spawns. Guid.Empty when the name is unknown or nothing
    // along the chain names an effect — a surface that leaves no mark.
    public Guid FindImpactEffect(string materialName)
    {
        if (!_byName.TryGetValue(materialName, out PhysicsMaterialAsset? asset))
            return Guid.Empty;
        for (int hops = 0; asset != null && hops < 8; hops++) // hop cap guards a fallback cycle
        {
            if (asset.ImpactEffect != Guid.Empty)
                return asset.ImpactEffect;
            asset = asset.Fallback != Guid.Empty && _byGuid.TryGetValue(asset.Fallback, out PhysicsMaterialAsset? fb)
                ? fb
                : null;
        }
        return Guid.Empty;
    }

    // PhysicMaterialCustomData.GetAudioDef: resolve the material by name, then walk the fallback chain
    // until some asset defines the event key. Null when the name is unknown or nothing defines the key.
    public string? FindAudioDefPath(string materialName, string key) =>
        FindAudioDef(materialName, key)?.Path;

    // Same walk, but names the asset that defined the key as well: the definition is packaged in the
    // bundle of whatever content source shipped THAT asset, and a workshop surface falling back to a
    // core material takes its audio from the core bundle, not the mod's.
    public (string Path, PhysicsMaterialAsset Owner)? FindAudioDef(string materialName, string key)
    {
        if (!_byName.TryGetValue(materialName, out PhysicsMaterialAsset? asset))
            return null;
        for (int hops = 0; asset != null && hops < 8; hops++) // hop cap guards a fallback cycle
        {
            if (asset.AudioDefs.TryGetValue(key, out string? path))
                return (path, asset);
            asset = asset.Fallback != Guid.Empty && _byGuid.TryGetValue(asset.Fallback, out PhysicsMaterialAsset? fb)
                ? fb
                : null;
        }
        return null;
    }
}
