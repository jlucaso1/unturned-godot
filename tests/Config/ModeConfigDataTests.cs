using System.IO;
using UnturnedGodot.Config;
using UnturnedGodot.Damage;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Config;

public class ModeConfigDataTests
{
    // ZombiesConfigData's constructor, mode by mode (PlayConfigData.cs:1105-1200). These are the numbers
    // the difficulty dial actually moves, so they are asserted rather than assumed.
    [Fact]
    public void NormalDefaults_MatchThePortedConstructor()
    {
        ZombiesConfig z = ModeConfigData.Normal.Zombies;

        Assert.Equal(0.25f, z.SpawnChance);
        Assert.Equal(0.5f, z.LootChance);
        Assert.Equal(0.15f, z.CrawlerChance);
        Assert.Equal(0.15f, z.SprinterChance);
        // NORMAL does roll these, which is why the reduced enum was wrong before any asset overrode it.
        Assert.Equal(0.025f, z.FlankerChance);
        Assert.Equal(0.025f, z.BurnerChance);
        Assert.Equal(0.025f, z.AcidChance);
        Assert.Equal(1f, z.DamageMultiplier);
        Assert.Equal(1f, z.ArmorMultiplier);
        Assert.Equal(1.25f, z.BackstabMultiplier);
        Assert.Equal(1f, z.NonHeadshotArmorMultiplier);
        Assert.Equal(360f, z.RespawnDayTime);
        Assert.Equal(30f, z.RespawnNightTime);
        Assert.Equal(2f, z.FullMoonExperienceMultiplier);
        Assert.False(z.SlowMovement);
        Assert.True(z.CanStun);
        Assert.False(z.OnlyCriticalStuns);
    }

    [Fact]
    public void EasyDefaults_AreTheGentlerBlock()
    {
        ZombiesConfig z = ModeConfigData.For(EGameMode.Easy).Zombies;

        Assert.Equal(0.2f, z.SpawnChance);
        Assert.Equal(0.55f, z.LootChance);
        Assert.Equal(0f, z.CrawlerChance);
        Assert.Equal(0f, z.SprinterChance);
        Assert.Equal(0f, z.FlankerChance);
        Assert.Equal(0.75f, z.DamageMultiplier);
        Assert.Equal(1.25f, z.ArmorMultiplier);
        Assert.True(z.SlowMovement); // "Slow_Movement = mode == EGameMode.EASY"
    }

    [Fact]
    public void HardDefaults_AreTheHarsherBlock()
    {
        ZombiesConfig z = ModeConfigData.For(EGameMode.Hard).Zombies;

        Assert.Equal(0.3f, z.SpawnChance);
        Assert.Equal(0.3f, z.LootChance);
        Assert.Equal(0.125f, z.CrawlerChance);
        Assert.Equal(0.175f, z.SprinterChance);
        Assert.Equal(0.05f, z.FlankerChance);
        Assert.Equal(1.5f, z.DamageMultiplier);
        Assert.Equal(0.75f, z.ArmorMultiplier);
        Assert.False(z.CanStun);          // "Can_Stun = mode != HARD"
        Assert.True(z.OnlyCriticalStuns); // "Only_Critical_Stuns = mode == HARD"
    }

    // The constant VehicleSpawnPlan used to spell out.
    [Fact]
    public void MinNaturalVehicles_DefaultsToSixteen() =>
        Assert.Equal(16u, ModeConfigData.Normal.Vehicles.MinNaturalVehicles);

    // ModeDamageConfig is a projection now rather than a separate set of numbers, so the two cannot
    // drift: EASY's armor multiplier has to arrive in the damage record.
    [Fact]
    public void DamageProjection_FollowsTheMode()
    {
        ModeDamageConfig easy = ModeConfigData.For(EGameMode.Easy).Damage;
        Assert.Equal(1.25f, easy.ZombieArmor);
        Assert.Equal(0.75f, easy.ZombieDamage);
        Assert.Equal(1.25f, easy.ZombieBackstab); // mode-independent in PlayConfigData

        ModeDamageConfig hard = ModeConfigData.For(EGameMode.Hard).Damage;
        Assert.Equal(0.75f, hard.ZombieArmor);
        Assert.Equal(1.5f, hard.ZombieDamage);
    }

    // The default projection must equal the record everything already uses, or loading no config at all
    // would silently change the punch.
    [Fact]
    public void NormalDamageProjection_EqualsTheStandaloneNormal() =>
        Assert.Equal(ModeDamageConfig.Normal, ModeConfigData.Normal.Damage);

    // ---- the loader ------------------------------------------------------------------------------

    private const string ServerConfig = """
    {
        "Normal": {
            "Zombies": {
                "Spawn_Chance": 0.4,
                "Crawler_Chance": 0.3,
                "Armor_Multiplier": 0.5,
                "Damage_Multiplier": 3,
                "Slow_Movement": true
            },
            "Vehicles": { "Min_Natural_Vehicles": 40 },
            "Barricades": { "Melee_Damage_Multiplier": 2 },
            "Structures": { "Melee_Damage_Multiplier": 0.25 },
            "Objects": { "Resource_Drops_Multiplier": 5 }
        },
        "Hard": {
            "Zombies": { "Spawn_Chance": 0.9 }
        }
    }
    """;

    [Fact]
    public void Parse_AppliesTheNamedModeBlock()
    {
        ModeConfigData config = ModeConfigData.Parse(ServerConfig);

        Assert.Equal(0.4f, config.Zombies.SpawnChance);
        Assert.Equal(0.3f, config.Zombies.CrawlerChance);
        Assert.Equal(0.5f, config.Zombies.ArmorMultiplier);
        Assert.Equal(3f, config.Zombies.DamageMultiplier);
        Assert.True(config.Zombies.SlowMovement);
        Assert.Equal(40u, config.Vehicles.MinNaturalVehicles);
        Assert.Equal(2f, config.Barricades.MeleeDamageMultiplier);
        Assert.Equal(0.25f, config.Structures.MeleeDamageMultiplier);
        Assert.Equal(5f, config.Objects.ResourceDropsMultiplier);
    }

    // Keys the file does not name keep the mode's own default: the game constructs the defaults and only
    // then deserializes over them, so a config predating a field does not zero it.
    [Fact]
    public void Parse_LeavesUnnamedKeysAtTheModeDefault()
    {
        ModeConfigData config = ModeConfigData.Parse(ServerConfig);

        Assert.Equal(0.15f, config.Zombies.SprinterChance);
        Assert.Equal(0.025f, config.Zombies.FlankerChance);
        Assert.Equal(1.25f, config.Zombies.BackstabMultiplier);
        Assert.Equal(360f, config.Zombies.RespawnDayTime);
    }

    [Fact]
    public void Parse_ReadsTheModeItWasAskedFor()
    {
        ModeConfigData hard = ModeConfigData.Parse(ServerConfig, EGameMode.Hard);
        Assert.Equal(0.9f, hard.Zombies.SpawnChance);
        Assert.Equal(0.175f, hard.Zombies.SprinterChance); // HARD's own default, untouched by the file
        Assert.Equal(EGameMode.Hard, hard.Mode);
    }

    [Fact]
    public void Parse_ModeBlockAbsent_YieldsThatModesDefaults()
    {
        ModeConfigData easy = ModeConfigData.Parse(ServerConfig, EGameMode.Easy);
        Assert.Equal(0.2f, easy.Zombies.SpawnChance);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("[]")]
    [InlineData("{ \"Normal\": 7 }")]              // the mode key is not an object
    [InlineData("{ \"Normal\": { \"Zombies\": 7 } }")] // the section is not an object
    public void Parse_MalformedKeepsTheDefaults(string json) =>
        Assert.Equal(0.25f, ModeConfigData.Parse(json).Zombies.SpawnChance);

    [Fact]
    public void Parse_WrongTypedValuesKeepTheirDefaults()
    {
        ModeConfigData config = ModeConfigData.Parse("""
        { "Normal": {
            "Zombies": { "Spawn_Chance": "lots", "Slow_Movement": 3 },
            "Vehicles": { "Min_Natural_Vehicles": -5 }
        } }
        """);

        Assert.Equal(0.25f, config.Zombies.SpawnChance);
        Assert.False(config.Zombies.SlowMovement);
        Assert.Equal(16u, config.Vehicles.MinNaturalVehicles); // negative is not a uint
    }

    // Every key the reader knows, all present at once. The defaults test covers the other side of each
    // of these branches, so between the two every field is exercised both ways.
    [Fact]
    public void Parse_ReadsEveryKnownKey()
    {
        ModeConfigData config = ModeConfigData.Parse("""
        { "Normal": {
            "Zombies": {
                "Spawn_Chance": 0.11, "Loot_Chance": 0.12,
                "Crawler_Chance": 0.13, "Sprinter_Chance": 0.14, "Flanker_Chance": 0.15,
                "Burner_Chance": 0.16, "Acid_Chance": 0.17,
                "Boss_Electric_Chance": 0.18, "Boss_Wind_Chance": 0.19, "Boss_Fire_Chance": 0.2,
                "Spirit_Chance": 0.21,
                "DL_Red_Volatile_Chance": 0.22, "DL_Blue_Volatile_Chance": 0.23,
                "Boss_Elver_Stomper_Chance": 0.24, "Boss_Kuwait_Chance": 0.25,
                "Respawn_Day_Time": 26, "Respawn_Night_Time": 27, "Respawn_Beacon_Time": 28,
                "Damage_Multiplier": 2.9, "Armor_Multiplier": 3.0,
                "Backstab_Multiplier": 3.1, "NonHeadshot_Armor_Multiplier": 3.2,
                "Beacon_Experience_Multiplier": 3.3, "Full_Moon_Experience_Multiplier": 3.4,
                "Slow_Movement": true, "Can_Stun": false, "Only_Critical_Stuns": true
            },
            "Vehicles": { "Min_Natural_Vehicles": 7, "Respawn_Time": 8, "Armor_Multiplier": 9 },
            "Barricades": {
                "Melee_Damage_Multiplier": 1.1, "Melee_Repair_Multiplier": 1.2,
                "Armor_Lowtier_Multiplier": 1.3, "Armor_Hightier_Multiplier": 1.4
            },
            "Structures": {
                "Melee_Damage_Multiplier": 2.1, "Melee_Repair_Multiplier": 2.2,
                "Armor_Lowtier_Multiplier": 2.3, "Armor_Hightier_Multiplier": 2.4
            },
            "Objects": { "Resource_Drops_Multiplier": 4.5 }
        } }
        """);

        ZombiesConfig z = config.Zombies;
        Assert.Equal(0.11f, z.SpawnChance);
        Assert.Equal(0.12f, z.LootChance);
        Assert.Equal(0.13f, z.CrawlerChance);
        Assert.Equal(0.14f, z.SprinterChance);
        Assert.Equal(0.15f, z.FlankerChance);
        Assert.Equal(0.16f, z.BurnerChance);
        Assert.Equal(0.17f, z.AcidChance);
        Assert.Equal(0.18f, z.BossElectricChance);
        Assert.Equal(0.19f, z.BossWindChance);
        Assert.Equal(0.2f, z.BossFireChance);
        Assert.Equal(0.21f, z.SpiritChance);
        Assert.Equal(0.22f, z.RedVolatileChance);
        Assert.Equal(0.23f, z.BlueVolatileChance);
        Assert.Equal(0.24f, z.BossElverStomperChance);
        Assert.Equal(0.25f, z.BossKuwaitChance);
        Assert.Equal(26f, z.RespawnDayTime);
        Assert.Equal(27f, z.RespawnNightTime);
        Assert.Equal(28f, z.RespawnBeaconTime);
        Assert.Equal(2.9f, z.DamageMultiplier);
        Assert.Equal(3.0f, z.ArmorMultiplier);
        Assert.Equal(3.1f, z.BackstabMultiplier);
        Assert.Equal(3.2f, z.NonHeadshotArmorMultiplier);
        Assert.Equal(3.3f, z.BeaconExperienceMultiplier);
        Assert.Equal(3.4f, z.FullMoonExperienceMultiplier);
        Assert.True(z.SlowMovement);
        Assert.False(z.CanStun);
        Assert.True(z.OnlyCriticalStuns);

        Assert.Equal(7u, config.Vehicles.MinNaturalVehicles);
        Assert.Equal(8f, config.Vehicles.RespawnTime);
        Assert.Equal(9f, config.Vehicles.ArmorMultiplier);

        Assert.Equal(1.1f, config.Barricades.MeleeDamageMultiplier);
        Assert.Equal(1.2f, config.Barricades.MeleeRepairMultiplier);
        Assert.Equal(1.3f, config.Barricades.ArmorLowtierMultiplier);
        Assert.Equal(1.4f, config.Barricades.ArmorHightierMultiplier);

        Assert.Equal(2.1f, config.Structures.MeleeDamageMultiplier);
        Assert.Equal(2.2f, config.Structures.MeleeRepairMultiplier);
        Assert.Equal(2.3f, config.Structures.ArmorLowtierMultiplier);
        Assert.Equal(2.4f, config.Structures.ArmorHightierMultiplier);

        Assert.Equal(4.5f, config.Objects.ResourceDropsMultiplier);

        // And those melee multipliers reach the damage record the punch actually takes.
        Assert.Equal(1.1f, config.Damage.BarricadeMelee);
        Assert.Equal(2.1f, config.Damage.StructureMelee);
        Assert.Equal(4.5f, config.Damage.ResourceDrops);
    }

    [Fact]
    public void Load_MissingFileYieldsTheDefaults()
    {
        using var dir = new TempDir();
        ModeConfigData config = ModeConfigData.Load(Path.Combine(dir.Path, "absent.json"));
        Assert.Equal(0.25f, config.Zombies.SpawnChance);
    }

    [Fact]
    public void Load_ReadsTheFile()
    {
        using var dir = new TempDir();
        string path = dir.Write("Config.json", ServerConfig);
        Assert.Equal(0.4f, ModeConfigData.Load(path).Zombies.SpawnChance);
    }
}
