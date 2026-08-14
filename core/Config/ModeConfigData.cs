using System;
using System.IO;
using System.Text.Json;
using UnturnedGodot.Damage;

namespace UnturnedGodot.Config;

// SDG.Unturned.EGameMode. The port runs NORMAL; the other two exist because the numbers below differ
// between them, and a server operator picks one.
public enum EGameMode
{
    Easy = 0,
    Normal = 1,
    Hard = 2,
}

// Provider.modeConfigData.Zombies (PlayConfigData.ZombiesConfigData), for the fields this port reads.
// Every default is the one ZombiesConfigData's constructor assigns for the given mode — see
// PlayConfigData.cs:1105-1200.
public sealed record ZombiesConfig
{
    public float SpawnChance { get; init; } = 0.25f;
    public float LootChance { get; init; } = 0.5f;

    // The speciality weights. NORMAL does roll flankers, burners and acid zombies — at 2.5% each — which
    // is why the port's reduced enum was wrong even before a difficulty asset overrode anything.
    public float CrawlerChance { get; init; } = 0.15f;
    public float SprinterChance { get; init; } = 0.15f;
    public float FlankerChance { get; init; } = 0.025f;
    public float BurnerChance { get; init; } = 0.025f;
    public float AcidChance { get; init; } = 0.025f;
    public float BossElectricChance { get; init; }
    public float BossWindChance { get; init; }
    public float BossFireChance { get; init; }
    public float SpiritChance { get; init; }
    public float RedVolatileChance { get; init; }
    public float BlueVolatileChance { get; init; }
    public float BossElverStomperChance { get; init; }
    public float BossKuwaitChance { get; init; }

    // Respawn cadence. The night value is the one a FULL MOON selects, not merely darkness
    // (ZombieManager.cs:1360 tests LightingManager.isFullMoon).
    public float RespawnDayTime { get; init; } = 360f;
    public float RespawnNightTime { get; init; } = 30f;
    public float RespawnBeaconTime { get; init; }

    public float DamageMultiplier { get; init; } = 1f;
    public float ArmorMultiplier { get; init; } = 1f;
    public float BackstabMultiplier { get; init; } = 1.25f;
    public float NonHeadshotArmorMultiplier { get; init; } = 1f;

    public float BeaconExperienceMultiplier { get; init; } = 1f;
    public float FullMoonExperienceMultiplier { get; init; } = 2f;

    // getZombieSpeed's dial: EASY slows every zombie down.
    public bool SlowMovement { get; init; }
    public bool CanStun { get; init; } = true;
    public bool OnlyCriticalStuns { get; init; }

    public static ZombiesConfig For(EGameMode mode) => mode switch
    {
        EGameMode.Easy => new ZombiesConfig
        {
            SpawnChance = 0.2f,
            LootChance = 0.55f,
            CrawlerChance = 0f,
            SprinterChance = 0f,
            FlankerChance = 0f,
            BurnerChance = 0f,
            AcidChance = 0f,
            DamageMultiplier = 0.75f,
            ArmorMultiplier = 1.25f,
            SlowMovement = true,
        },
        EGameMode.Hard => new ZombiesConfig
        {
            SpawnChance = 0.3f,
            LootChance = 0.3f,
            CrawlerChance = 0.125f,
            SprinterChance = 0.175f,
            FlankerChance = 0.05f,
            BurnerChance = 0.05f,
            AcidChance = 0.05f,
            DamageMultiplier = 1.5f,
            ArmorMultiplier = 0.75f,
            CanStun = false,
            OnlyCriticalStuns = true,
        },
        _ => new ZombiesConfig(),
    };
}

// Provider.modeConfigData.Vehicles, for the one field the spawn planner needs.
public sealed record VehiclesConfig
{
    // "VehicleManager.SumNaturalVehicleCount() < Provider.modeConfigData.Vehicles.Min_Natural_Vehicles"
    // (VehicleManager.cs:2454) — the floor the map's spawn tables are rolled up to. Mode-independent.
    public uint MinNaturalVehicles { get; init; } = 16;

    public float RespawnTime { get; init; } = 900f;
    public float ArmorMultiplier { get; init; } = 1f;
}

// The melee multipliers the punch path reads, shared in shape by barricades and structures.
public sealed record BuildableConfig
{
    public float MeleeDamageMultiplier { get; init; } = 1f;
    public float MeleeRepairMultiplier { get; init; } = 1f;
    public float ArmorLowtierMultiplier { get; init; } = 1f;
    public float ArmorHightierMultiplier { get; init; } = 0.5f;
}

// Provider.modeConfigData.Objects (ObjectConfigData).
public sealed record ObjectsConfig
{
    // How many items a felled resource yields.
    public float ResourceDropsMultiplier { get; init; } = 1f;
}

// Provider.modeConfigData: the whole per-mode block a server runs under. Unturned writes this to the
// server's own Config.json and reloads it from there; the map may then override individual keys through
// its Config.json's Mode_Config_Overrides. This is the loader ModeDamageConfig said did not exist yet.
public sealed record ModeConfigData
{
    public EGameMode Mode { get; init; } = EGameMode.Normal;
    public ZombiesConfig Zombies { get; init; } = new();
    public VehiclesConfig Vehicles { get; init; } = new();
    public BuildableConfig Barricades { get; init; } = new();
    public BuildableConfig Structures { get; init; } = new();
    public ObjectsConfig Objects { get; init; } = new();

    public static ModeConfigData For(EGameMode mode) => new()
    {
        Mode = mode,
        Zombies = ZombiesConfig.For(mode),
    };

    // What the port runs, and what everything falls back to when no server config is on disk.
    public static readonly ModeConfigData Normal = For(EGameMode.Normal);

    // The damage slice, so the punch maths keeps taking one small record rather than the whole block.
    public ModeDamageConfig Damage => new()
    {
        ZombieDamage = Zombies.DamageMultiplier,
        ZombieArmor = Zombies.ArmorMultiplier,
        ZombieNonHeadshotArmor = Zombies.NonHeadshotArmorMultiplier,
        ZombieBackstab = Zombies.BackstabMultiplier,
        BarricadeMelee = Barricades.MeleeDamageMultiplier,
        StructureMelee = Structures.MeleeDamageMultiplier,
        VehicleMelee = 1f,
        ResourceDrops = Objects.ResourceDropsMultiplier,
    };

    // Reads a server Config.json (the file PlayConfigData round-trips) and applies the keys it names on
    // top of the mode's own defaults. Everything absent keeps the ported default, which is exactly how
    // Unturned treats a config that predates a field.
    //
    // `serverConfigPath` is <server>/Config.json. A missing or malformed file yields the mode defaults:
    // the game constructs the defaults first and only then deserializes over them.
    public static ModeConfigData Load(string serverConfigPath, EGameMode mode = EGameMode.Normal)
    {
        string text;
        try
        {
            text = File.ReadAllText(serverConfigPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return For(mode);
        }
        return Parse(text, mode);
    }

    public static ModeConfigData Parse(string json, EGameMode mode = EGameMode.Normal)
    {
        ModeConfigData defaults = For(mode);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json,
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            return defaults;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return defaults;

            // ConfigData holds one ModeConfigData per mode side by side, named for the mode.
            string section = mode switch
            {
                EGameMode.Easy => "Easy",
                EGameMode.Hard => "Hard",
                _ => "Normal",
            };
            if (!root.TryGetProperty(section, out JsonElement modeElement)
                || modeElement.ValueKind != JsonValueKind.Object)
                return defaults;

            return defaults with
            {
                Zombies = ReadZombies(modeElement, defaults.Zombies),
                Vehicles = ReadVehicles(modeElement, defaults.Vehicles),
                Barricades = ReadBuildable(modeElement, "Barricades", defaults.Barricades),
                Structures = ReadBuildable(modeElement, "Structures", defaults.Structures),
                Objects = ReadObjects(modeElement, defaults.Objects),
            };
        }
    }

    private static ZombiesConfig ReadZombies(JsonElement mode, ZombiesConfig d)
    {
        if (!Section(mode, "Zombies", out JsonElement z))
            return d;
        return d with
        {
            SpawnChance = Float(z, "Spawn_Chance", d.SpawnChance),
            LootChance = Float(z, "Loot_Chance", d.LootChance),
            CrawlerChance = Float(z, "Crawler_Chance", d.CrawlerChance),
            SprinterChance = Float(z, "Sprinter_Chance", d.SprinterChance),
            FlankerChance = Float(z, "Flanker_Chance", d.FlankerChance),
            BurnerChance = Float(z, "Burner_Chance", d.BurnerChance),
            AcidChance = Float(z, "Acid_Chance", d.AcidChance),
            BossElectricChance = Float(z, "Boss_Electric_Chance", d.BossElectricChance),
            BossWindChance = Float(z, "Boss_Wind_Chance", d.BossWindChance),
            BossFireChance = Float(z, "Boss_Fire_Chance", d.BossFireChance),
            SpiritChance = Float(z, "Spirit_Chance", d.SpiritChance),
            RedVolatileChance = Float(z, "DL_Red_Volatile_Chance", d.RedVolatileChance),
            BlueVolatileChance = Float(z, "DL_Blue_Volatile_Chance", d.BlueVolatileChance),
            BossElverStomperChance = Float(z, "Boss_Elver_Stomper_Chance", d.BossElverStomperChance),
            BossKuwaitChance = Float(z, "Boss_Kuwait_Chance", d.BossKuwaitChance),
            RespawnDayTime = Float(z, "Respawn_Day_Time", d.RespawnDayTime),
            RespawnNightTime = Float(z, "Respawn_Night_Time", d.RespawnNightTime),
            RespawnBeaconTime = Float(z, "Respawn_Beacon_Time", d.RespawnBeaconTime),
            DamageMultiplier = Float(z, "Damage_Multiplier", d.DamageMultiplier),
            ArmorMultiplier = Float(z, "Armor_Multiplier", d.ArmorMultiplier),
            BackstabMultiplier = Float(z, "Backstab_Multiplier", d.BackstabMultiplier),
            NonHeadshotArmorMultiplier =
                Float(z, "NonHeadshot_Armor_Multiplier", d.NonHeadshotArmorMultiplier),
            BeaconExperienceMultiplier =
                Float(z, "Beacon_Experience_Multiplier", d.BeaconExperienceMultiplier),
            FullMoonExperienceMultiplier =
                Float(z, "Full_Moon_Experience_Multiplier", d.FullMoonExperienceMultiplier),
            SlowMovement = Bool(z, "Slow_Movement", d.SlowMovement),
            CanStun = Bool(z, "Can_Stun", d.CanStun),
            OnlyCriticalStuns = Bool(z, "Only_Critical_Stuns", d.OnlyCriticalStuns),
        };
    }

    private static VehiclesConfig ReadVehicles(JsonElement mode, VehiclesConfig d) =>
        Section(mode, "Vehicles", out JsonElement v)
            ? d with
            {
                MinNaturalVehicles = UInt(v, "Min_Natural_Vehicles", d.MinNaturalVehicles),
                RespawnTime = Float(v, "Respawn_Time", d.RespawnTime),
                ArmorMultiplier = Float(v, "Armor_Multiplier", d.ArmorMultiplier),
            }
            : d;

    private static BuildableConfig ReadBuildable(JsonElement mode, string key, BuildableConfig d) =>
        Section(mode, key, out JsonElement b)
            ? d with
            {
                MeleeDamageMultiplier = Float(b, "Melee_Damage_Multiplier", d.MeleeDamageMultiplier),
                MeleeRepairMultiplier = Float(b, "Melee_Repair_Multiplier", d.MeleeRepairMultiplier),
                ArmorLowtierMultiplier = Float(b, "Armor_Lowtier_Multiplier", d.ArmorLowtierMultiplier),
                ArmorHightierMultiplier = Float(b, "Armor_Hightier_Multiplier", d.ArmorHightierMultiplier),
            }
            : d;

    private static ObjectsConfig ReadObjects(JsonElement mode, ObjectsConfig d) =>
        Section(mode, "Objects", out JsonElement o)
            ? d with
            {
                ResourceDropsMultiplier =
                    Float(o, "Resource_Drops_Multiplier", d.ResourceDropsMultiplier),
            }
            : d;

    private static bool Section(JsonElement parent, string key, out JsonElement section) =>
        parent.TryGetProperty(key, out section) && section.ValueKind == JsonValueKind.Object;

    private static float Float(JsonElement o, string key, float fallback) =>
        o.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetSingle(out float parsed)
            ? parsed
            : fallback;

    private static uint UInt(JsonElement o, string key, uint fallback) =>
        o.TryGetProperty(key, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetUInt32(out uint parsed)
            ? parsed
            : fallback;

    private static bool Bool(JsonElement o, string key, bool fallback) =>
        o.TryGetProperty(key, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => fallback,
            }
            : fallback;
}
