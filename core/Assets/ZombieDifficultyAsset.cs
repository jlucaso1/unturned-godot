using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Assets;

// Mirrors SDG.Unturned.ZombieDifficultyAsset: the per-speciality spawn WEIGHTS a map attaches to a
// navigation bound or to a zombie table, plus the stun thresholds and the horde-beacon permission.
//
// These are weights, not independent probabilities. ZombieManager.generateZombieSpeciality feeds every
// one of them into a single weighted-random table and takes one draw from it, with NORMAL absorbing
// whatever weight is left over — so raising one speciality's chance lowers every other's, which
// sequential `random < chance` rolls do not do.
public sealed class ZombieDifficultyAsset
{
    public Guid Guid { get; }

    public bool OverridesSpawnChance { get; }

    public float CrawlerChance { get; }
    public float SprinterChance { get; }
    public float FlankerChance { get; }
    public float BurnerChance { get; }
    public float AcidChance { get; }
    public float BossElectricChance { get; }
    public float BossWindChance { get; }
    public float BossFireChance { get; }
    public float SpiritChance { get; }
    public float RedVolatileChance { get; }
    public float BlueVolatileChance { get; }
    public float BossElverStomperChance { get; }
    public float BossKuwaitChance { get; }

    // Below 1 the asset means "no threshold", which PopulateAsset normalizes to -1.
    public int MegaStunThreshold { get; }
    public int NormalStunThreshold { get; }

    // "Can horde beacons be placed in the associated bounds?" Defaults to true when the key is absent.
    public bool AllowHordeBeacon { get; }

    private ZombieDifficultyAsset(Guid guid, bool overridesSpawnChance, float[] chances,
        int megaStun, int normalStun, bool allowHordeBeacon)
    {
        Guid = guid;
        OverridesSpawnChance = overridesSpawnChance;
        CrawlerChance = chances[0];
        SprinterChance = chances[1];
        FlankerChance = chances[2];
        BurnerChance = chances[3];
        AcidChance = chances[4];
        BossElectricChance = chances[5];
        BossWindChance = chances[6];
        BossFireChance = chances[7];
        SpiritChance = chances[8];
        RedVolatileChance = chances[9];
        BlueVolatileChance = chances[10];
        BossElverStomperChance = chances[11];
        BossKuwaitChance = chances[12];
        MegaStunThreshold = megaStun;
        NormalStunThreshold = normalStun;
        AllowHordeBeacon = allowHordeBeacon;
    }

    public static bool TryParse(DatDictionary root, [MaybeNullWhen(false)] out ZombieDifficultyAsset asset)
    {
        asset = null;
        if (!root.TryGetDictionary("Metadata", out var meta) ||
            !root.TryGetDictionary("Asset", out var data))
            return false;
        if (!meta.TryGetGuid("GUID", out Guid guid))
            return false;
        string type = meta.GetString("Type") ?? string.Empty;
        if (!type.Contains("ZombieDifficultyAsset", StringComparison.Ordinal))
            return false;

        // ParseFloat on an absent key yields 0, so an unlisted speciality simply weighs nothing.
        float[] chances =
        {
            Chance(data, "Crawler_Chance"),
            Chance(data, "Sprinter_Chance"),
            Chance(data, "Flanker_Chance"),
            Chance(data, "Burner_Chance"),
            Chance(data, "Acid_Chance"),
            Chance(data, "Boss_Electric_Chance"),
            Chance(data, "Boss_Wind_Chance"),
            Chance(data, "Boss_Fire_Chance"),
            Chance(data, "Spirit_Chance"),
            Chance(data, "DL_Red_Volatile_Chance"),
            Chance(data, "DL_Blue_Volatile_Chance"),
            Chance(data, "Boss_Elver_Stomper_Chance"),
            Chance(data, "Boss_Kuwait_Chance"),
        };

        asset = new ZombieDifficultyAsset(
            guid,
            DefaultsToTrue(data, "Overrides_Spawn_Chance"),
            chances,
            Threshold(data, "Mega_Stun_Threshold"),
            Threshold(data, "Normal_Stun_Threshold"),
            DefaultsToTrue(data, "Allow_Horde_Beacon"));
        return true;
    }

    // Both flags follow PopulateAsset's `ContainsKey ? ParseBool : true` shape, and PopulateAsset says
    // why for the first: "Previously difficulty assets were only used to override spawn chance, so we
    // default to overriding if this is an older asset." Both of PEI's omit them and so take true.
    //
    // Not `GetBool(key, true)`. Those two are not the same function once the key is PRESENT but does
    // not parse — a bare `Overrides_Spawn_Chance` with no value, which DatValue holds as null.
    // GetBool would hand back the caller's default (true); the game calls ParseBool with no default
    // there and gets false. The key's presence is therefore tested separately from its value, which is
    // exactly the split the original writes out.
    private static bool DefaultsToTrue(DatDictionary data, string key) =>
        !data.ContainsKey(key) || data.GetBool(key);

    private static float Chance(DatDictionary data, string key) =>
        data.TryGetSingle(key, out float value) ? value : 0f;

    // "if (threshold < 1) threshold = -1" — the asset's own normalization of "unset".
    private static int Threshold(DatDictionary data, string key)
    {
        int value = data.TryGetInt32(key, out int parsed) ? parsed : 0;
        return value < 1 ? -1 : value;
    }

    // The weighted table generateZombieSpeciality builds from this asset. `isNighttime` gates the two
    // volatile entries: "Only spawn volatiles at nighttime, otherwise they explode immediately."
    public ZombieSpecialityWeights Weights(bool isNighttime)
    {
        var table = new ZombieSpecialityWeights();
        table.Add(EZombieSpeciality.Crawler, CrawlerChance);
        table.Add(EZombieSpeciality.Sprinter, SprinterChance);
        table.Add(EZombieSpeciality.FlankerFriendly, FlankerChance);
        table.Add(EZombieSpeciality.Burner, BurnerChance);
        table.Add(EZombieSpeciality.Acid, AcidChance);
        table.Add(EZombieSpeciality.BossElectric, BossElectricChance);
        table.Add(EZombieSpeciality.BossWind, BossWindChance);
        table.Add(EZombieSpeciality.BossFire, BossFireChance);
        table.Add(EZombieSpeciality.Spirit, SpiritChance);
        if (isNighttime)
        {
            table.Add(EZombieSpeciality.RedVolatile, RedVolatileChance);
            table.Add(EZombieSpeciality.BlueVolatile, BlueVolatileChance);
        }
        table.Add(EZombieSpeciality.BossElverStomper, BossElverStomperChance);
        table.Add(EZombieSpeciality.BossKuwait, BossKuwaitChance);
        table.AddNormalRemainder();
        return table;
    }
}

// The registry the difficulty lookup needs: GUID -> asset, scanned out of Bundles/Assets. Maps name
// their difficulty by GUID from two places — the navigation bound (Bounds.dat) and the zombie table
// (Spawns/Zombies.dat) — and both resolve through here.
public sealed class ZombieDifficultyBank
{
    private readonly Dictionary<Guid, ZombieDifficultyAsset> _byGuid = new();

    public int Count => _byGuid.Count;

    public void AddIfAbsent(ZombieDifficultyAsset asset) => _byGuid.TryAdd(asset.Guid, asset);

    public ZombieDifficultyAsset? Find(Guid guid) =>
        guid != Guid.Empty && _byGuid.TryGetValue(guid, out ZombieDifficultyAsset? asset) ? asset : null;

    // The difficulty assets of every content source. A workshop map ships its own, and scanning the
    // game's alone would send its bounds to the mode config instead.
    public static ZombieDifficultyBank ScanContentSources(IEnumerable<ContentSource> sources)
    {
        var roots = new List<string>();
        foreach (ContentSource source in sources)
            roots.Add(Path.Combine(source.AssetsDir, "Zombie_Difficulty"));
        return ScanDirectories(roots);
    }

    // First claimant wins, and the roots arrive with the game's own first — the same rule the physics
    // material bank and the object database follow, so a workshop asset reusing an official GUID cannot
    // take over an official map's difficulty.
    public static ZombieDifficultyBank ScanDirectories(IEnumerable<string> roots)
    {
        var merged = new ZombieDifficultyBank();
        foreach (string root in roots)
            foreach (ZombieDifficultyAsset asset in ScanDirectory(root)._byGuid.Values)
                merged.AddIfAbsent(asset);
        return merged;
    }

    public static ZombieDifficultyBank ScanDirectory(string root)
    {
        var bank = new ZombieDifficultyBank();
        if (!Directory.Exists(root))
            return bank;
        try
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.asset", SearchOption.AllDirectories))
            {
                DatDictionary parsed;
                try { parsed = DatParser.Parse(TextFile.ReadAllText(file)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (TryParse(parsed, out ZombieDifficultyAsset? asset))
                    bank.AddIfAbsent(asset);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return bank;
    }

    private static bool TryParse(DatDictionary parsed,
        [MaybeNullWhen(false)] out ZombieDifficultyAsset asset) =>
        ZombieDifficultyAsset.TryParse(parsed, out asset);
}
