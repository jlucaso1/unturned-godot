using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Config;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// LightingManager's predicates, which gameplay reads and nothing in this port used to.
public class LightingTimeOfDayTests
{
    // "isDaytime => day < LevelLighting.bias".
    [Theory]
    [InlineData(0f, 0.6f, true)]
    [InlineData(0.59f, 0.6f, true)]
    [InlineData(0.6f, 0.6f, false)]   // strictly less than
    [InlineData(0.9f, 0.6f, false)]
    public void IsDaytime(float time, float bias, bool expected)
    {
        Assert.Equal(expected, LightingCycle.IsDaytime(time, bias));
        Assert.Equal(!expected, LightingCycle.IsNighttime(time, bias));
    }

    // "isCycled = day > LevelLighting.bias" — strictly GREATER, against isDaytime's strictly less. At
    // exactly the bias neither holds, and that asymmetry is the original's.
    [Fact]
    public void AtTheBias_IsNeitherDaytimeNorCycled()
    {
        Assert.False(LightingCycle.IsDaytime(0.6f, 0.6f));
        Assert.False(LightingCycle.IsCycled(0.6f, 0.6f));
        Assert.True(LightingCycle.IsNighttime(0.6f, 0.6f)); // nighttime is the negation, so it holds
    }

    [Theory]
    [InlineData(0.61f, 0.6f, true)]
    [InlineData(0.6f, 0.6f, false)]
    [InlineData(0.3f, 0.6f, false)]
    public void IsCycled(float time, float bias, bool expected) =>
        Assert.Equal(expected, LightingCycle.IsCycled(time, bias));

    // "isFullMoon = isCycled && LevelLighting.moon == 2": the phase AND the cycle having crossed dusk.
    [Theory]
    [InlineData(0.9f, 2, true)]
    [InlineData(0.9f, 1, false)]  // right time, wrong phase
    [InlineData(0.3f, 2, false)]  // right phase, still daytime
    [InlineData(0.6f, 2, false)]  // exactly at the bias: not cycled
    public void IsFullMoon(float time, int phase, bool expected) =>
        Assert.Equal(expected, LightingCycle.IsFullMoon(time, bias: 0.6f, phase));

    // A map saved at midday with the full phase is not a full moon until dusk, which is the whole
    // reason the phase alone was never enough to drive anything.
    [Fact]
    public void FullPhaseAtMidday_IsNotAFullMoon() =>
        Assert.False(LightingCycle.IsFullMoon(0.25f, 0.6f, LightingCycle.FullMoonPhase));
}

public class ZombieSpecialityRollTests
{
    private static ZombieTable Table(Guid difficulty = default) => new()
    {
        Name = "Civilian",
        Health = 100,
        Damage = 10,
        DifficultyGuid = difficulty,
    };

    private static List<NavBound> Bounds(Guid difficulty = default) => new()
    {
        new NavBound
        {
            Center = Vector3.Zero,
            Size = new Vector3(400, 400, 400),
            MaxZombies = byte.MaxValue,
            DifficultyGuid = difficulty,
        },
    };

    private static readonly Guid MilitaryGuid = Guid.Parse("646b4cdcc9c547b0a24528f8acccc8e8");
    private static readonly Guid PassiveGuid = Guid.Parse("111b4cdcc9c547b0a24528f8acccc8e8");

    // Peaks_Military's own numbers, and one asset that explicitly declines to override so the
    // fall-through path is reachable.
    private static ZombieDifficultyBank Bank()
    {
        var bank = new ZombieDifficultyBank();
        bank.AddIfAbsent(Asset(MilitaryGuid,
            "\tCrawler_Chance 0.2\n\tSprinter_Chance 0.2\n"
            + "\tFlanker_Chance 0.075\n\tBurner_Chance 0.075\n\tAcid_Chance 0.075\n"));
        bank.AddIfAbsent(Asset(PassiveGuid,
            "\tOverrides_Spawn_Chance False\n\tCrawler_Chance 0.9\n"));
        return bank;
    }

    private static ZombieDifficultyAsset Asset(Guid guid, string body) => Asset(
        "Metadata\n{\n\tGUID " + guid.ToString("N")
        + "\n\tType SDG.Unturned.ZombieDifficultyAsset\n}\nAsset\n{\n" + body + "}\n");

    private static ZombieDifficultyAsset Asset(string text)
    {
        Assert.True(ZombieDifficultyAsset.TryParse(DatParser.Parse(text), out ZombieDifficultyAsset? a));
        return a!;
    }

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static Dictionary<EZombieSpeciality, int> Roll(ZombieSystem system, ZombieTable table,
        int draws = 40000)
    {
        var counts = new Dictionary<EZombieSpeciality, int>();
        var random = new Random(3);
        for (int i = 0; i < draws; i++)
        {
            EZombieSpeciality kind = system.GenerateSpeciality(0, table, random);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }
        return counts;
    }

    private static ZombieSystem System(ZombieTable table, List<NavBound> bounds,
        ZombieDifficultyBank? bank = null) =>
        new(new[] { table }, bounds, FlatGround) { Difficulties = bank };

    // With no difficulty asset resolvable, the roll uses Provider.modeConfigData — which is what PEI
    // itself does, since PEI names none.
    [Fact]
    public void NoDifficultyAsset_UsesTheModeConfig()
    {
        ZombieTable table = Table();
        Dictionary<EZombieSpeciality, int> counts = Roll(System(table, Bounds()), table);

        Assert.InRange(counts[EZombieSpeciality.Crawler] / 40000.0, 0.14, 0.16);
        Assert.InRange(counts[EZombieSpeciality.Sprinter] / 40000.0, 0.14, 0.16);
        Assert.InRange(counts[EZombieSpeciality.Normal] / 40000.0, 0.61, 0.64);
        // NORMAL weights these at 0.025 each, so they DO appear — the reduced enum could not.
        Assert.InRange(counts[EZombieSpeciality.FlankerFriendly] / 40000.0, 0.02, 0.03);
        Assert.InRange(counts[EZombieSpeciality.Burner] / 40000.0, 0.02, 0.03);
        Assert.InRange(counts[EZombieSpeciality.Acid] / 40000.0, 0.02, 0.03);
    }

    // The navigation bound's asset overrides it: 0.2 crawler where the mode config says 0.15.
    [Fact]
    public void BoundsDifficultyAsset_OverridesTheModeConfig()
    {
        ZombieTable table = Table();
        Dictionary<EZombieSpeciality, int> counts =
            Roll(System(table, Bounds(MilitaryGuid), Bank()), table);

        Assert.InRange(counts[EZombieSpeciality.Crawler] / 40000.0, 0.19, 0.21);
        Assert.InRange(counts[EZombieSpeciality.Sprinter] / 40000.0, 0.19, 0.21);
        Assert.InRange(counts[EZombieSpeciality.Burner] / 40000.0, 0.07, 0.085);
        Assert.InRange(counts[EZombieSpeciality.Normal] / 40000.0, 0.36, 0.39); // 1 - 0.625
    }

    // GetDifficultyInBoundForTable's other half: the TABLE names one, and it applies when the bound
    // does not.
    [Fact]
    public void TableDifficultyAsset_AppliesWhenTheBoundNamesNone()
    {
        ZombieTable table = Table(MilitaryGuid);
        Dictionary<EZombieSpeciality, int> counts = Roll(System(table, Bounds(), Bank()), table);

        Assert.InRange(counts[EZombieSpeciality.Crawler] / 40000.0, 0.19, 0.21);
    }

    // NavmeshOverridesTable: when both name one, the bound wins.
    [Fact]
    public void BoundWinsOverTheTable()
    {
        ZombieTable table = Table(PassiveGuid);
        ZombieDifficultyAsset? resolved =
            System(table, Bounds(MilitaryGuid), Bank()).DifficultyFor(0, table, forSpawnOverrides: true);

        Assert.Equal(MilitaryGuid, resolved!.Guid);
    }

    // "result == null || (forSpawnOverrides && !result.Overrides_Spawn_Chance)" — a bound naming an
    // asset that declines to override falls through to the table's.
    [Fact]
    public void BoundAssetDecliningToOverride_FallsThroughToTheTable()
    {
        ZombieTable table = Table(MilitaryGuid);
        ZombieDifficultyAsset? resolved =
            System(table, Bounds(PassiveGuid), Bank()).DifficultyFor(0, table, forSpawnOverrides: true);

        Assert.Equal(MilitaryGuid, resolved!.Guid);
    }

    // And when nothing else names one either, the roll falls back to the mode config rather than using
    // the non-overriding asset's numbers.
    [Fact]
    public void NonOverridingAssetAlone_StillUsesTheModeConfig()
    {
        ZombieTable table = Table();
        Dictionary<EZombieSpeciality, int> counts =
            Roll(System(table, Bounds(PassiveGuid), Bank()), table);

        Assert.InRange(counts[EZombieSpeciality.Crawler] / 40000.0, 0.14, 0.16); // 0.15, not 0.9
    }

    [Fact]
    public void NoBankAtAll_UsesTheModeConfig()
    {
        ZombieTable table = Table(MilitaryGuid);
        Assert.Null(System(table, Bounds(MilitaryGuid))
            .DifficultyFor(0, table, forSpawnOverrides: true));
    }

    // A bound index past the list must not throw; the table's asset is still consulted.
    [Fact]
    public void BoundIndexOutOfRange_FallsBackToTheTable()
    {
        ZombieTable table = Table(MilitaryGuid);
        ZombieDifficultyAsset? resolved =
            System(table, Bounds(), Bank()).DifficultyFor(9, table, forSpawnOverrides: true);

        Assert.Equal(MilitaryGuid, resolved!.Guid);
    }

    // The mode config is a dial, not a constant: raising the crawler weight raises crawlers.
    [Fact]
    public void ModeConfig_DrivesTheFallbackWeights()
    {
        ZombieTable table = Table();
        ZombieSystem system = System(table, Bounds());
        system.ModeConfig = ModeConfigData.For(EGameMode.Easy);

        Dictionary<EZombieSpeciality, int> counts = Roll(system, table);

        // EASY zeroes every speciality, so every zombie is normal.
        Assert.Equal(40000, counts[EZombieSpeciality.Normal]);
    }

    // "Only spawn volatiles at nighttime, otherwise they explode immediately."
    [Fact]
    public void Volatiles_OnlyAppearAtNight()
    {
        ZombieTable table = Table();
        ZombieSystem system = System(table, Bounds());
        system.ModeConfig = ModeConfigData.Normal with
        {
            Zombies = ModeConfigData.Normal.Zombies with { RedVolatileChance = 0.5f },
        };

        Assert.False(Roll(system, table).ContainsKey(EZombieSpeciality.RedVolatile));

        system.IsNighttime = true;
        Assert.InRange(Roll(system, table)[EZombieSpeciality.RedVolatile] / 40000.0, 0.48, 0.52);
    }

    // A mega TABLE is a mega outright and is never rolled, whatever the difficulty says.
    [Fact]
    public void MegaTable_IsNeverRolled()
    {
        var table = new ZombieTable { Name = "Special", Health = 2999, Damage = 33, IsMega = true };
        ZombieSystem system = new(new[] { table }, Bounds(MilitaryGuid), FlatGround)
        {
            Difficulties = Bank(),
        };
        system.Spawn(Enumerable.Range(0, 40)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 80f, 0, 0))).ToList(),
            new Random(5));

        Assert.NotEmpty(system.Zombies);
        Assert.All(system.Zombies, z => Assert.Equal(EZombieSpeciality.Mega, z.Speciality));
    }

    // The full moon reaches the instance, which is what makes it hit harder and reach further.
    [Fact]
    public void FullMoon_MakesSpawnedZombiesHyper()
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround) { IsFullMoon = true };
        system.Spawn(new[] { new ZombieSpawnpointData(0, Vector3.Zero) }, new Random(1));

        ZombieInstance zombie = Assert.Single(system.Zombies);
        Assert.True(zombie.IsHyper);
        Assert.Equal(3.5f, zombie.VerticalAttackRange, 3);
    }

    [Fact]
    public void NoFullMoon_LeavesThemOrdinary()
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, Vector3.Zero) }, new Random(1));

        ZombieInstance zombie = Assert.Single(system.Zombies);
        Assert.False(zombie.IsHyper);
        Assert.Equal(2.1f, zombie.VerticalAttackRange, 3);
    }

    // Zombie.isHyper is LIVE: LightingManager broadcasts onMoonUpdated and ZombieRegion turns it into
    // onHyperUpdated, so a population spawned in daylight becomes hyper when the moon comes up. Stamped
    // at spawn — which is what this was — the full moon never reached a single zombie, because nothing
    // in this port respawns one.
    [Fact]
    public void TheMoonRisingMakesTheEXISTINGPopulationHyper()
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround);
        system.Spawn(Enumerable.Range(0, 12)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 20f, 0, 0))).ToList(),
            new Random(4));
        Assert.NotEmpty(system.Zombies);
        Assert.All(system.Zombies, z => Assert.False(z.IsHyper));

        system.IsFullMoon = true;
        Assert.All(system.Zombies, z => Assert.True(z.IsHyper));
        Assert.All(system.Zombies, z => Assert.Equal(3.5f, z.VerticalAttackRange, 3));

        // And dawn takes it back off them.
        system.IsFullMoon = false;
        Assert.All(system.Zombies, z => Assert.False(z.IsHyper));
    }

    // "isHyper => zombieRegion.isHyper && speciality != BOSS_ALL && speciality != BOSS_BUAK_FINAL": the
    // two exclusions survive the live update, not just the spawn stamp.
    [Fact]
    public void TheTwoNeverHyperBossesStayOrdinaryUnderTheMoon()
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround);
        system.Spawn(new[] { new ZombieSpawnpointData(0, Vector3.Zero) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);

        zombie.Speciality = EZombieSpeciality.BossAll;
        system.IsFullMoon = true;
        Assert.False(zombie.IsHyper);

        zombie.Speciality = EZombieSpeciality.BossBuakFinal;
        system.IsFullMoon = false;
        system.IsFullMoon = true;
        Assert.False(zombie.IsHyper);

        zombie.Speciality = EZombieSpeciality.BossFire; // an ordinary boss IS hyper
        system.IsFullMoon = false;
        system.IsFullMoon = true;
        Assert.True(zombie.IsHyper);
    }

    // Assigning the same value is a no-op, which is what lets a host push this from every tick.
    [Fact]
    public void AnUnchangedMoonDoesNotRestampThePopulation()
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround) { IsFullMoon = true };
        system.Spawn(new[] { new ZombieSpawnpointData(0, Vector3.Zero) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);

        zombie.IsHyper = false;  // a value only a re-stamp could put back
        system.IsFullMoon = true;
        Assert.False(zombie.IsHyper);
    }

    // ---- population size and the boss cap -------------------------------------------------------

    // "Mathf.CeilToInt(levelSpawnpoints.Count * Provider.modeConfigData.Zombies.Spawn_Chance)": the
    // MODE's chance, not a constant. EASY thins the population and HARD swells it, which is most of what
    // picking a difficulty is supposed to do to a map.
    [Theory]
    [InlineData(EGameMode.Normal, 5)] // ceil(20 * 0.25)
    [InlineData(EGameMode.Easy, 4)]   // ceil(20 * 0.2)
    [InlineData(EGameMode.Hard, 6)]   // ceil(20 * 0.3)
    public void SpawnChance_ComesFromTheModeConfig(EGameMode mode, int expected)
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround)
        {
            ModeConfig = ModeConfigData.For(mode),
        };
        system.Spawn(Enumerable.Range(0, 20)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 40f, 0, 0))).ToList(),
            new Random(9));

        Assert.Equal(expected, system.Zombies.Count);
    }

    // A custom Config.json is a dial, not a choice between three presets.
    [Fact]
    public void SpawnChance_HonoursACustomValue()
    {
        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, Bounds(), FlatGround)
        {
            ModeConfig = ModeConfigData.Normal with
            {
                Zombies = ModeConfigData.Normal.Zombies with { SpawnChance = 1f },
            },
        };
        system.Spawn(Enumerable.Range(0, 20)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 40f, 0, 0))).ToList(),
            new Random(9));

        Assert.Equal(20, system.Zombies.Count);
    }

    // "regionMaxBossZombies >= 0 && speciality.IsBoss() && region.aliveBossZombieCount >=
    // regionMaxBossZombies -> continue". A bound capped at one boss starts with exactly one however hard
    // its difficulty rolls them.
    [Fact]
    public void BossCap_LimitsHowManyBossesABoundStartsWith()
    {
        var bank = new ZombieDifficultyBank();
        bank.AddIfAbsent(Asset(MilitaryGuid, "\tBoss_Fire_Chance 1\n"));
        List<NavBound> bounds = Bounds(MilitaryGuid);
        bounds[0].MaxBossZombies = 1;

        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, bounds, FlatGround) { Difficulties = bank };
        system.Spawn(Enumerable.Range(0, 40)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 80f, 0, 0))).ToList(),
            new Random(6));

        // One boss, and nothing else at all: every remaining draw rolls a boss too, is rejected, and
        // spends its spawnpoint. Draining the pool is the original's own outcome — the `continue` costs
        // the spawnpoint and the while loop ends when there are none left.
        Assert.Single(system.Zombies);
        Assert.True(system.Zombies[0].Speciality.IsBoss());
    }

    // With bosses merely LIKELY rather than certain, the cap takes the surplus and the region still
    // fills to its ordinary size — a rejected boss costs a spawnpoint but not a slot.
    [Fact]
    public void BossCap_LeavesTheRegionRoomToFillUp()
    {
        var bank = new ZombieDifficultyBank();
        bank.AddIfAbsent(Asset(MilitaryGuid, "\tBoss_Fire_Chance 0.5\n"));
        List<NavBound> bounds = Bounds(MilitaryGuid);
        bounds[0].MaxBossZombies = 1;

        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, bounds, FlatGround) { Difficulties = bank };
        system.Spawn(Enumerable.Range(0, 40)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 80f, 0, 0))).ToList(),
            new Random(6));

        Assert.Equal(10, system.Zombies.Count); // ceil(40 * 0.25)
        Assert.Single(system.Zombies, z => z.Speciality.IsBoss());
    }

    // Negative is uncapped (FlagData's own default for a map saved before the field existed).
    [Fact]
    public void BossCap_NegativeMeansUncapped()
    {
        var bank = new ZombieDifficultyBank();
        bank.AddIfAbsent(Asset(MilitaryGuid, "\tBoss_Fire_Chance 1\n"));
        List<NavBound> bounds = Bounds(MilitaryGuid);
        Assert.Equal(-1, bounds[0].MaxBossZombies);

        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, bounds, FlatGround) { Difficulties = bank };
        system.Spawn(Enumerable.Range(0, 40)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 80f, 0, 0))).ToList(),
            new Random(6));

        Assert.Equal(10, system.Zombies.Count);
        Assert.All(system.Zombies, z => Assert.True(z.Speciality.IsBoss()));
    }

    // A cap of zero admits none at all, and the pool is drained trying — the bound ends up empty rather
    // than looping forever.
    [Fact]
    public void BossCap_OfZeroAdmitsNone()
    {
        var bank = new ZombieDifficultyBank();
        bank.AddIfAbsent(Asset(MilitaryGuid, "\tBoss_Fire_Chance 1\n"));
        List<NavBound> bounds = Bounds(MilitaryGuid);
        bounds[0].MaxBossZombies = 0;

        ZombieTable table = Table();
        ZombieSystem system = new(new[] { table }, bounds, FlatGround) { Difficulties = bank };
        system.Spawn(Enumerable.Range(0, 40)
            .Select(i => new ZombieSpawnpointData(0, new Vector3(i * 4f - 80f, 0, 0))).ToList(),
            new Random(6));

        Assert.Empty(system.Zombies);
    }

    // ---- the level asset's prioritization -------------------------------------------------------

    // TableOverridesNavmesh reverses the lookup: the table's asset wins and the bound's is the fallback.
    [Fact]
    public void TableOverridesNavmesh_ReversesTheLookup()
    {
        ZombieTable table = Table(MilitaryGuid);
        ZombieSystem system = System(table, Bounds(PassiveGuid), Bank());
        system.DifficultyPrioritization =
            EZombieDifficultyAssetPrioritization.TableOverridesNavmesh;

        Assert.Equal(MilitaryGuid, system.DifficultyFor(0, table, forSpawnOverrides: true)!.Guid);

        // And the default ordering, on the same pair, resolves the same asset only because the bound's
        // declines to override — swap the two round and the orderings genuinely disagree.
        ZombieTable other = Table(PassiveGuid);
        ZombieSystem navmeshFirst = System(other, Bounds(MilitaryGuid), Bank());
        Assert.Equal(MilitaryGuid, navmeshFirst.DifficultyFor(0, other, forSpawnOverrides: true)!.Guid);

        ZombieSystem tableFirst = System(other, Bounds(MilitaryGuid), Bank());
        tableFirst.DifficultyPrioritization =
            EZombieDifficultyAssetPrioritization.TableOverridesNavmesh;
        // The table's asset declines to override the spawn chance, so the spawn roll falls through to
        // the bound's — that is the `forSpawnOverrides` clause, not the ordering.
        Assert.Equal(MilitaryGuid, tableFirst.DifficultyFor(0, other, forSpawnOverrides: true)!.Guid);
        // ...but the STUN threshold does not ask for spawn overrides, so it keeps the table's.
        Assert.Equal(PassiveGuid, tableFirst.DifficultyFor(0, other, forSpawnOverrides: false)!.Guid);
    }

    // forSpawnOverrides false is Zombie.updateDifficulty's call: an asset that declines to override the
    // spawn chance is still this zombie's difficulty for everything else it decides.
    [Fact]
    public void NotForSpawnOverrides_KeepsANonOverridingAsset()
    {
        ZombieTable table = Table(MilitaryGuid);
        ZombieSystem system = System(table, Bounds(PassiveGuid), Bank());

        Assert.Equal(MilitaryGuid, system.DifficultyFor(0, table, forSpawnOverrides: true)!.Guid);
        Assert.Equal(PassiveGuid, system.DifficultyFor(0, table, forSpawnOverrides: false)!.Guid);
    }

    // ---- the speciality tables the expanded enum brought with it --------------------------------

    // Zombie.tellAlive's seeker.Speed assignment.
    [Theory]
    [InlineData(EZombieSpeciality.Normal, 5.5f)]
    [InlineData(EZombieSpeciality.Crawler, 3f)]
    [InlineData(EZombieSpeciality.Sprinter, 6.5f)]
    [InlineData(EZombieSpeciality.RedVolatile, 6.5f)]
    [InlineData(EZombieSpeciality.BlueVolatile, 6.5f)]
    [InlineData(EZombieSpeciality.FlankerFriendly, 6f)]
    [InlineData(EZombieSpeciality.FlankerStalk, 6f)]
    [InlineData(EZombieSpeciality.Mega, 6f)]
    [InlineData(EZombieSpeciality.Burner, 5.5f)]  // no branch of its own
    [InlineData(EZombieSpeciality.Acid, 5.5f)]
    public void Speed(EZombieSpeciality kind, float expected) =>
        Assert.Equal(expected, new ZombieInstance { Speciality = kind }.Speed);

    // Zombie.tellAlive's health assignment.
    [Theory]
    [InlineData(EZombieSpeciality.Normal, 150)]
    [InlineData(EZombieSpeciality.Crawler, 225)]
    [InlineData(EZombieSpeciality.RedVolatile, 225)]
    [InlineData(EZombieSpeciality.Sprinter, 75)]
    [InlineData(EZombieSpeciality.Burner, 150)]
    [InlineData(EZombieSpeciality.BossKuwait, 60000)]
    [InlineData(EZombieSpeciality.BossElverStomper, 4600)]
    [InlineData(EZombieSpeciality.BossAll, 12000)]
    [InlineData(EZombieSpeciality.BossMagma, 12000)]
    [InlineData(EZombieSpeciality.BossBuakWind, 6000)]
    public void HealthFor(EZombieSpeciality kind, int expected) =>
        Assert.Equal((ushort)expected,
            ZombieInstance.HealthFor(new ZombieTable { Health = 150 }, kind));

    // "Boss zombies are considered mega as well", so a boss is as wide and as long-reaching as one.
    [Fact]
    public void BossesAreWideAndLongReaching()
    {
        var boss = new ZombieInstance { Speciality = EZombieSpeciality.BossFire };
        Assert.Equal(0.75f, boss.Radius);
        Assert.Equal(4f, boss.AttackRangeSquared, 3);

        var ordinary = new ZombieInstance { Speciality = EZombieSpeciality.Normal };
        Assert.Equal(1f, ordinary.AttackRangeSquared, 3);
    }

    // PunchTargeting's radioactive branch, which used to be a note saying it could not be written.
    [Theory]
    [InlineData(EZombieSpeciality.Acid, UnturnedGodot.Damage.PunchTargeting.RadioactiveZombieSurface)]
    [InlineData(EZombieSpeciality.BossNuclear, UnturnedGodot.Damage.PunchTargeting.RadioactiveZombieSurface)]
    [InlineData(EZombieSpeciality.Normal, UnturnedGodot.Damage.PunchTargeting.ZombieSurface)]
    [InlineData(EZombieSpeciality.Burner, UnturnedGodot.Damage.PunchTargeting.ZombieSurface)]
    public void RadioactiveZombiesReportAlienDynamic(EZombieSpeciality kind, string expected) =>
        Assert.Equal(expected, UnturnedGodot.Damage.PunchTargeting.SurfaceFor(kind));
}

// The loader's own wiring. In a class of its own because the roll tests above define a `System`
// helper, which shadows the System namespace these tests need for System.IO.Path.
public class ZombieWorldLoadTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    // Everything a host supplies has to arrive on the system, or the roll silently uses the defaults
    // and nobody can tell from the outside that the wiring is missing.
    [RealDataFact(Map = "PEI")]
    public void ZombieWorld_PassesTheDataThrough()
    {
        ZombieDifficultyBank bank = ZombieDifficultyBank.ScanDirectory(
            System.IO.Path.Combine(GameData.Install!, "Bundles", "Assets",
                "Zombie_Difficulty"));
        var clothing = new ClothingArmorDatabase();
        ModeConfigData hard = ModeConfigData.For(EGameMode.Hard);

        ZombieSystem? system = ZombieWorld.Load(
            GameData.Map("PEI")!, FlatGround, new Random(1),
            difficulties: bank, mode: hard, isNighttime: true, isFullMoon: true, clothing: clothing,
            prioritization: EZombieDifficultyAssetPrioritization.TableOverridesNavmesh);

        Assert.NotNull(system);
        Assert.Same(bank, system!.Difficulties);
        Assert.Same(clothing, system.Clothing);
        Assert.Equal(hard, system.ModeConfig);
        // Zombie.askDamage's two mode switches ride the same config: HARD cannot stun at all.
        Assert.False(system.CanStun);
        Assert.True(system.OnlyCriticalStuns);
        Assert.True(system.IsNighttime);
        Assert.True(system.IsFullMoon);
        Assert.Equal(EZombieDifficultyAssetPrioritization.TableOverridesNavmesh,
            system.DifficultyPrioritization);
        // The full moon was set BEFORE Spawn, so the population it generated carries it.
        Assert.NotEmpty(system.Zombies);
        Assert.All(system.Zombies, z => Assert.True(z.IsHyper));
    }

    // And the defaults, which is what a caller that supplies nothing gets.
    [RealDataFact(Map = "PEI")]
    public void ZombieWorld_DefaultsToTheModeConfigAndDaytime()
    {
        ZombieSystem? system = ZombieWorld.Load(
            GameData.Map("PEI")!, FlatGround, new Random(1));

        Assert.NotNull(system);
        Assert.Null(system!.Difficulties);
        Assert.Null(system.Clothing);
        Assert.Equal(ModeConfigData.Normal, system.ModeConfig);
        Assert.True(system.CanStun);
        Assert.False(system.OnlyCriticalStuns);
        Assert.Equal(EZombieDifficultyAssetPrioritization.NavmeshOverridesTable,
            system.DifficultyPrioritization);
        Assert.False(system.IsNighttime);
        Assert.False(system.IsFullMoon);
        Assert.All(system.Zombies, z => Assert.False(z.IsHyper));
    }

    // The mode config decides the population's SIZE, so a real map loaded on EASY carries fewer zombies
    // than the same map on HARD. Pinned against the map rather than a synthetic bound because this is
    // the number an operator changes a difficulty to change.
    [RealDataFact(Map = "PEI")]
    public void ZombieWorld_SizesThePopulationFromTheModeConfig()
    {
        int easy = Count(EGameMode.Easy);
        int normal = Count(EGameMode.Normal);
        int hard = Count(EGameMode.Hard);

        Assert.True(easy < normal, $"EASY spawned {easy}, NORMAL {normal}");
        Assert.True(normal < hard, $"NORMAL spawned {normal}, HARD {hard}");

        static int Count(EGameMode mode) =>
            ZombieWorld.Load(GameData.Map("PEI")!, FlatGround, new Random(1),
                mode: ModeConfigData.For(mode))!.Zombies.Count;
    }
}
