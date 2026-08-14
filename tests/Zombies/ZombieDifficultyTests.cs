using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnturnedGodot.Assets;
using UnturnedGodot.Dat;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieDifficultyTests
{
    private const string MilitaryAsset = """
    Metadata
    {
    	GUID 646b4cdcc9c547b0a24528f8acccc8e8
    	Type SDG.Unturned.ZombieDifficultyAsset, Assembly-CSharp, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
    }
    Asset
    {
    	ID 0
    	Crawler_Chance 0.2
    	Sprinter_Chance 0.2
    	Flanker_Chance 0.075
    	Burner_Chance 0.075
    	Acid_Chance 0.075
    	Boss_Electric_Chance 0
    	Boss_Wind_Chance 0
    	Boss_Fire_Chance 0
    }
    """;

    private static ZombieDifficultyAsset Parse(string text)
    {
        Assert.True(ZombieDifficultyAsset.TryParse(DatParser.Parse(text), out ZombieDifficultyAsset? asset));
        return asset!;
    }

    [Fact]
    public void Parses_EveryChance()
    {
        ZombieDifficultyAsset asset = Parse(MilitaryAsset);

        Assert.Equal(Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8"), asset.Guid);
        Assert.Equal(0.2f, asset.CrawlerChance);
        Assert.Equal(0.2f, asset.SprinterChance);
        Assert.Equal(0.075f, asset.FlankerChance);
        Assert.Equal(0.075f, asset.BurnerChance);
        Assert.Equal(0.075f, asset.AcidChance);
        Assert.Equal(0f, asset.BossElectricChance);
        Assert.Equal(0f, asset.BossWindChance);
        Assert.Equal(0f, asset.BossFireChance);
        // Keys the file omits parse as zero, which is what ParseFloat yields for an absent key.
        Assert.Equal(0f, asset.SpiritChance);
        Assert.Equal(0f, asset.RedVolatileChance);
        Assert.Equal(0f, asset.BlueVolatileChance);
        Assert.Equal(0f, asset.BossElverStomperChance);
        Assert.Equal(0f, asset.BossKuwaitChance);
    }

    // "Previously difficulty assets were only used to override spawn chance, so we default to
    // overriding if this is an older asset." Both of PEI's omit the key.
    [Fact]
    public void OverridesSpawnChance_DefaultsToTrue() =>
        Assert.True(Parse(MilitaryAsset).OverridesSpawnChance);

    [Fact]
    public void OverridesSpawnChance_CanBeTurnedOff() =>
        Assert.False(Parse(MilitaryAsset.Replace("ID 0", "ID 0\n\tOverrides_Spawn_Chance False",
            StringComparison.Ordinal)).OverridesSpawnChance);

    [Fact]
    public void AllowHordeBeacon_DefaultsToTrue() =>
        Assert.True(Parse(MilitaryAsset).AllowHordeBeacon);

    [Fact]
    public void AllowHordeBeacon_CanBeTurnedOff() =>
        Assert.False(Parse(MilitaryAsset.Replace("ID 0", "ID 0\n\tAllow_Horde_Beacon False",
            StringComparison.Ordinal)).AllowHordeBeacon);

    // "if (threshold < 1) threshold = -1" — the asset's own normalization of "unset".
    [Theory]
    [InlineData("", -1)]
    [InlineData("\n\tMega_Stun_Threshold 0", -1)]
    [InlineData("\n\tMega_Stun_Threshold -4", -1)]
    [InlineData("\n\tMega_Stun_Threshold 1", 1)]
    [InlineData("\n\tMega_Stun_Threshold 900", 900)]
    public void StunThreshold_NormalizesBelowOneToMinusOne(string extra, int expected) =>
        Assert.Equal(expected,
            Parse(MilitaryAsset.Replace("ID 0", "ID 0" + extra, StringComparison.Ordinal))
                .MegaStunThreshold);

    [Fact]
    public void NormalStunThreshold_IsReadSeparately() =>
        Assert.Equal(5, Parse(MilitaryAsset.Replace("ID 0", "ID 0\n\tNormal_Stun_Threshold 5",
            StringComparison.Ordinal)).NormalStunThreshold);

    [Theory]
    [InlineData("Asset\n{\n\tCrawler_Chance 1\n}")]                       // no Metadata
    [InlineData("Metadata\n{\n\tGUID 646b4cdc\n}\nAsset\n{\n}")]          // unparseable GUID
    [InlineData("Metadata\n{\n\tType SDG.Unturned.ObjectAsset\n}\nAsset\n{\n}")] // no GUID at all
    public void NonDifficultyDocuments_AreRejected(string text) =>
        Assert.False(ZombieDifficultyAsset.TryParse(DatParser.Parse(text), out _));

    [Fact]
    public void WrongAssetType_IsRejected() =>
        Assert.False(ZombieDifficultyAsset.TryParse(
            DatParser.Parse(MilitaryAsset.Replace("ZombieDifficultyAsset", "ObjectAsset",
                StringComparison.Ordinal)),
            out _));

    // ---- the weighted table ----------------------------------------------------------------------

    [Fact]
    public void Weights_CarryEveryChanceAndTheNormalRemainder()
    {
        ZombieSpecialityWeights weights = Parse(MilitaryAsset).Weights(isNighttime: false);

        // 0.2 + 0.2 + 0.075 * 3 = 0.625, leaving 0.375 for normal.
        Assert.Equal(1f, weights.TotalWeight, 5);
        Assert.Equal(0.375f, Weight(weights, EZombieSpeciality.Normal), 5);
        Assert.Equal(0.2f, Weight(weights, EZombieSpeciality.Crawler), 5);
        Assert.Equal(0.075f, Weight(weights, EZombieSpeciality.Burner), 5);
    }

    // "Only spawn volatiles at nighttime, otherwise they explode immediately."
    [Fact]
    public void Weights_AddTheVolatilesOnlyAtNight()
    {
        ZombieDifficultyAsset asset = Parse(
            MilitaryAsset.Replace("ID 0", "ID 0\n\tDL_Red_Volatile_Chance 0.1", StringComparison.Ordinal));

        Assert.Equal(0f, Weight(asset.Weights(isNighttime: false), EZombieSpeciality.RedVolatile));
        Assert.Equal(0.1f, Weight(asset.Weights(isNighttime: true), EZombieSpeciality.RedVolatile), 5);
    }

    private static float Weight(ZombieSpecialityWeights weights, EZombieSpeciality kind)
    {
        foreach (ZombieSpecialityWeights.Entry entry in weights.Entries)
            if (entry.Value == kind)
                return entry.Weight;
        return 0f;
    }

    // ---- the bank --------------------------------------------------------------------------------

    [Fact]
    public void Bank_IndexesByGuidAndIgnoresEverythingElse()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Zombie_Difficulty", "Peaks_Military.asset"), MilitaryAsset);
        dir.Write(Path.Combine("Zombie_Difficulty", "NotOne.asset"), "Metadata\n{\n}\nAsset\n{\n}");
        dir.Write(Path.Combine("Zombie_Difficulty", "Ignored.txt"), MilitaryAsset);

        ZombieDifficultyBank bank = ZombieDifficultyBank.ScanDirectory(dir.Path);

        Assert.Equal(1, bank.Count);
        Assert.NotNull(bank.Find(Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8")));
        Assert.Null(bank.Find(Guid.NewGuid()));
        Assert.Null(bank.Find(Guid.Empty)); // an unset reference resolves to nothing, not to entry zero
    }

    [Fact]
    public void Bank_MissingDirectoryIsEmpty() =>
        Assert.Equal(0, ZombieDifficultyBank.ScanDirectory("/no/such/place").Count);

    // First claimant wins, and the roots arrive with the game's own first — so a workshop asset reusing
    // an official GUID cannot take over an official map's difficulty.
    [Fact]
    public void Bank_FirstRootWinsAContestedGuid()
    {
        using var official = new TempDir();
        using var mod = new TempDir();
        official.Write("a.asset", MilitaryAsset);
        mod.Write("b.asset", MilitaryAsset.Replace("Crawler_Chance 0.2", "Crawler_Chance 0.9",
            StringComparison.Ordinal));

        ZombieDifficultyBank bank =
            ZombieDifficultyBank.ScanDirectories(new[] { official.Path, mod.Path });

        Assert.Equal(1, bank.Count);
        Assert.Equal(0.2f, bank.Find(Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8"))!.CrawlerChance);
    }

    // ---- against the real files ------------------------------------------------------------------

    // The whole point of this change is that these numbers are on disk. Assert them against the file.
    [RealDataFact]
    public void RealPeaksMilitary_CarriesTheMapsOwnChances()
    {
        ZombieDifficultyBank bank = ZombieDifficultyBank.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Assets", "Zombie_Difficulty"));

        ZombieDifficultyAsset? military = bank.Find(Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8"));
        Assert.NotNull(military);
        Assert.Equal(0.2f, military!.CrawlerChance);
        Assert.Equal(0.2f, military.SprinterChance);
        Assert.Equal(0.075f, military.FlankerChance);
        Assert.Equal(0.075f, military.BurnerChance);
        Assert.Equal(0.075f, military.AcidChance);
        Assert.True(military.OverridesSpawnChance);
    }

    [RealDataFact]
    public void RealPeaksBurnedTown_IsMostlyBurners()
    {
        ZombieDifficultyBank bank = ZombieDifficultyBank.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Assets", "Zombie_Difficulty"));

        ZombieDifficultyAsset? town = bank.Find(Guid.Parse("49068fc187ed4ad28f92ea9a81fe590c"));
        Assert.NotNull(town);
        Assert.Equal(0f, town!.CrawlerChance);
        Assert.Equal(0.4f, town.BurnerChance);
        Assert.Equal(0.2f, town.FlankerChance);
        Assert.Equal(0.025f, town.BossFireChance); // a boss the port could not previously even name
    }

    // PEI itself names NO difficulty asset — not in its navigation bounds and not in its zombie tables
    // — so on PEI the roll falls through to Provider.modeConfigData, which is the branch
    // generateZombieSpeciality's `else` takes. Recorded here because it is easy to assume the opposite
    // from the two Peaks_* assets sitting in Bundles/Assets: those ship with the game, but this map does
    // not reference them, and a test asserting PEI resolves one would be asserting a fiction.
    //
    // The change is still a behaviour change on PEI: the mode block weights flanker, burner and acid at
    // 0.025 each and draws once over the whole table, where the ladder it replaces could produce neither
    // and gave a sprinter 0.85 x 0.15 rather than 0.15.
    [RealDataFact(Map = "PEI")]
    public void RealPei_NamesNoDifficultyAsset_SoTheRollUsesTheModeConfig()
    {
        List<UnturnedGodot.Data.NavBound> bounds = UnturnedGodot.Data.LevelNavigationData.Load(
            Path.Combine(GameData.Map("PEI")!, "Environment"));
        List<UnturnedGodot.Data.ZombieTable> tables = UnturnedGodot.Data.LevelZombiesData.LoadTables(
            Path.Combine(GameData.Map("PEI")!, "Spawns", "Zombies.dat"));

        Assert.NotEmpty(bounds);
        Assert.NotEmpty(tables);
        Assert.All(bounds, b => Assert.Equal(Guid.Empty, b.DifficultyGuid));
        Assert.All(tables, t => Assert.Equal(Guid.Empty, t.DifficultyGuid));
    }

    // The other field Zombies.dat was parsing and dropping: every PEI table names a loot spawn table.
    [RealDataFact(Map = "PEI")]
    public void RealPeiTables_CarryTheirLootTableId()
    {
        List<UnturnedGodot.Data.ZombieTable> tables = UnturnedGodot.Data.LevelZombiesData.LoadTables(
            Path.Combine(GameData.Map("PEI")!, "Spawns", "Zombies.dat"));

        Assert.NotEmpty(tables);
        Assert.All(tables, t => Assert.True(t.LootId > 0, $"{t.Name} has no loot table"));
        // The mega table is the one with thousands of health and its own experience value.
        UnturnedGodot.Data.ZombieTable mega = Assert.Single(tables, t => t.IsMega);
        Assert.Equal(40u, mega.Xp);
    }

    // A bound that DOES name one resolves through the bank, which is the link the roll depends on.
    [RealDataFact]
    public void BoundNamingADifficulty_ResolvesThroughTheBank()
    {
        ZombieDifficultyBank bank = ZombieDifficultyBank.ScanDirectory(
            Path.Combine(GameData.Install!, "Bundles", "Assets", "Zombie_Difficulty"));
        var bound = new UnturnedGodot.Data.NavBound
        {
            DifficultyGuid = Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8"),
        };

        Assert.NotNull(bank.Find(bound.DifficultyGuid));
    }
}
