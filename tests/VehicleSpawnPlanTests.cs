using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Config;
using UnturnedGodot.Dat;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class VehicleSpawnPlanTests
{
    private static readonly Guid HatchbackGuid = Guid.Parse("8c32debcf1974cd7a24a46779872c205");
    private static readonly Guid TruckGuid = Guid.Parse("6f56dc58382349b79793f9ba8839774e");

    // A vehicle and a redirector pointing at it, the shape nearly every legacy vehicle id has today.
    private static ObjectAssetDatabase Assets()
    {
        var db = new ObjectAssetDatabase();
        db.Add(Parse($"GUID {HatchbackGuid:N}\nType Vehicle\nID 40\n"));
        db.Add(Parse($"GUID {TruckGuid:N}\nType Vehicle\n")); // no legacy id: reachable only by GUID
        db.Add(Parse($"GUID ac89f332c364461e82b1e94a16e868c4\n"
            + "Type SDG.Unturned.VehicleRedirectorAsset, Assembly-CSharp\nID 30\n"
            + $"TargetVehicle {HatchbackGuid:N}\n"));
        return db;
    }

    private static ObjectAsset Parse(string text)
    {
        Assert.True(ObjectAsset.TryParse(DatParser.Parse(text), null, out ObjectAsset? asset));
        return asset;
    }

    private static VehicleTable Table(ushort tableId, params (float Chance, ushort[] Ids)[] tiers)
    {
        var table = new VehicleTable { Name = "Test", TableId = tableId };
        foreach ((float chance, ushort[] ids) in tiers)
        {
            var tier = new VehicleTier { Name = "Tier", Chance = chance };
            tier.Vehicles.AddRange(ids);
            table.Tiers.Add(tier);
        }
        table.BuildTable();
        return table;
    }

    private static List<VehicleSpawnpointData> Spread(int count, float spacing = 100f)
    {
        var spawns = new List<VehicleSpawnpointData>(count);
        for (int i = 0; i < count; i++)
            spawns.Add(new VehicleSpawnpointData(0, new Vector3(i * spacing, 0f, 0f), 90f));
        return spawns;
    }

    [Fact]
    public void Build_RollsTheLegacyTiersWhenTheTableNamesNoSpawnAsset()
    {
        List<PlacedObject> placed = VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 30 })) },
            Spread(4), Assets(), new SpawnTableDatabase(), ELevelSize.Tiny, new Random(1));

        // Tiny caps at 4 instances, but the minimum natural count is what wins, so all four are used.
        Assert.Equal(4, placed.Count);
        // Legacy id 30 is a redirector; the placement carries the vehicle it redirects to.
        Assert.All(placed, p => Assert.Equal(HatchbackGuid, p.Guid));
    }

    [Fact]
    public void Build_ResolvesThroughTheSpawnTableWhenTheMapNamesOne()
    {
        var spawnTables = new SpawnTableDatabase();
        Assert.True(SpawnTableAsset.TryParse(DatParser.Parse(
            $"GUID 20cdf1b0b0324866be88d5fb7a5c4507\nType Spawn\nID 295\n"
            + $"Tables 1\nTable_0_GUID {TruckGuid:N}\nTable_0_Weight 100\n"), out SpawnTableAsset? table));
        spawnTables.AddIfAbsent(table);

        List<PlacedObject> placed = VehicleSpawnPlan.Build(new[] { Table(295, (1f, new ushort[] { 30 })) },
            Spread(2), Assets(), spawnTables, ELevelSize.Tiny, new Random(1));

        // The spawn table wins over the tiers, so this is the truck and never the tiers' hatchback.
        Assert.All(placed, p => Assert.Equal(TruckGuid, p.Guid));
    }

    [Fact]
    public void Build_SkipsSpawnpointsCrowdedByOneAlreadyTaken()
    {
        // Four spawnpoints within a metre of each other: only the first can be used.
        List<PlacedObject> placed = VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 40 })) },
            Spread(4, spacing: 1f), Assets(), new SpawnTableDatabase(), ELevelSize.Tiny, new Random(1));

        Assert.Single(placed);
    }

    [Fact]
    public void Build_LiftsAndOrientsEachPlacementLikeTheGameDoes()
    {
        var spawns = new List<VehicleSpawnpointData>
        {
            new(0, new Vector3(3f, 10f, -7f), 90f),
        };

        PlacedObject placed = Assert.Single(VehicleSpawnPlan.Build(
            new[] { Table(0, (1f, new ushort[] { 40 })) }, spawns, Assets(), new SpawnTableDatabase(),
            ELevelSize.Tiny, new Random(1)));

        Assert.Equal(new Vector3(3f, 10.5f, -7f), placed.Position); // point.y += 0.5f
        Assert.Equal(new Vector3(0f, 90f, 0f), placed.EulerDegrees);
        Assert.Equal(Vector3.One, placed.Scale);
    }

    [Fact]
    public void Build_TakesNoMoreThanTheLevelSizeAllows()
    {
        Assert.Equal(16, VehicleSpawnPlan.NaturalVehicleCount(ELevelSize.Tiny)); // the natural minimum
        Assert.Equal(32, VehicleSpawnPlan.NaturalVehicleCount(ELevelSize.Large));
        Assert.Equal(64, VehicleSpawnPlan.NaturalVehicleCount(ELevelSize.Insane));

        List<PlacedObject> placed = VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 40 })) },
            Spread(200), Assets(), new SpawnTableDatabase(), ELevelSize.Insane, new Random(1));

        Assert.Equal(64, placed.Count);
    }

    // "VehicleManager.SumNaturalVehicleCount() < Provider.modeConfigData.Vehicles.Min_Natural_Vehicles":
    // the floor is the server's, not a constant. NaturalVehicleCount already took the config; nothing
    // could reach it, because neither Load nor Build carried one and Build called it without an argument.
    [Fact]
    public void Build_TakesTheFloorFromTheModeConfig()
    {
        ModeConfigData busy = ModeConfigData.Normal with
        {
            Vehicles = ModeConfigData.Normal.Vehicles with { MinNaturalVehicles = 40 },
        };
        Assert.Equal(40, VehicleSpawnPlan.NaturalVehicleCount(ELevelSize.Tiny, busy));
        // The size still wins when it is the larger of the two — Math.Max, not a replacement.
        Assert.Equal(64, VehicleSpawnPlan.NaturalVehicleCount(ELevelSize.Insane, busy));

        List<PlacedObject> placed = VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 40 })) },
            Spread(200), Assets(), new SpawnTableDatabase(), ELevelSize.Tiny, new Random(1), busy);

        Assert.Equal(40, placed.Count);
        // ...and without the config it is the ported NORMAL floor, exactly as before.
        Assert.Equal(16, VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 40 })) },
            Spread(200), Assets(), new SpawnTableDatabase(), ELevelSize.Tiny, new Random(1)).Count);
    }

    // And the config survives the whole Load path, which is where it used to be dropped.
    [Fact]
    public void Load_CarriesTheModeConfigThroughToTheFloor()
    {
        using var dir = new TempDir();
        string map = Directory.CreateDirectory(Path.Combine(dir.Path, "Maps", "Test", "Spawns")).FullName;
        File.WriteAllBytes(Path.Combine(map, "Vehicles.dat"), LegacyVehiclesDat());
        Directory.CreateDirectory(Path.Combine(dir.Path, "Bundles", "Objects"));

        var level = new LevelInfo(Path.Combine(dir.Path, "Maps", "Test"));
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(dir.Path);

        // A map with no Size file reads as Medium, whose ceiling is the same 16 the NORMAL floor is.
        Assert.Equal(16, VehicleSpawnPlan.Load(level, sources, Assets()).Count);
        Assert.Equal(24, VehicleSpawnPlan.Load(level, sources, Assets(), ModeConfigData.Normal with
        {
            Vehicles = ModeConfigData.Normal.Vehicles with { MinNaturalVehicles = 24 },
        }).Count);
    }

    // Thirty spawnpoints, all far enough apart to be used, rolled from the map's own legacy tier ladder.
    private static byte[] LegacyVehiclesDat()
    {
        var w = new RiverBytes().Byte(4).Byte(1);
        w.Byte(255).Byte(255).Byte(255).Str("Civilian").UInt16(0);
        w.Byte(1).Str("Tier").Single(1f).Byte(1).UInt16(40);
        w.UInt16(30);
        for (int i = 0; i < 30; i++)
            w.Byte(0).Vector3(new Vector3(i * 500f, 0f, 0f)).Byte(0);
        return w.ToArray();
    }

    [Fact]
    public void Build_YieldsNothingWithoutTablesOrSpawnpoints()
    {
        Assert.Empty(VehicleSpawnPlan.Build(Array.Empty<VehicleTable>(), Spread(4), Assets(),
            new SpawnTableDatabase(), ELevelSize.Medium, new Random(1)));
        Assert.Empty(VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 40 })) },
            Array.Empty<VehicleSpawnpointData>(), Assets(), new SpawnTableDatabase(), ELevelSize.Medium,
            new Random(1)));
    }

    [Fact]
    public void Build_IgnoresATableWhoseSpawnAssetIsNotInstalled()
    {
        // The map names spawn table 295 but nothing ships it, so the spawnpoint is left empty rather than
        // silently falling back to the tiers — which is what Unturned does with an unresolvable table.
        Assert.Empty(VehicleSpawnPlan.Build(new[] { Table(295, (1f, new ushort[] { 30 })) },
            Spread(2), Assets(), new SpawnTableDatabase(), ELevelSize.Medium, new Random(1)));
    }

    [Fact]
    public void Build_FallsBackToAnyTierWhenTheRollPassesTheLadder()
    {
        // The authored chances need not sum to one; a roll past the end still has to produce a vehicle.
        var table = new VehicleTable { Name = "Thin", TableId = 0 };
        var tier = new VehicleTier { Name = "Only", Chance = 0.01f };
        tier.Vehicles.Add(30);
        table.Tiers.Add(tier);
        table.BuildTable();

        List<PlacedObject> placed = VehicleSpawnPlan.Build(new[] { table }, Spread(8), Assets(),
            new SpawnTableDatabase(), ELevelSize.Tiny, new Random(1));

        Assert.NotEmpty(placed);
        Assert.All(placed, p => Assert.Equal(HatchbackGuid, p.Guid));
    }

    [Fact]
    public void Build_IgnoresATierThatListsNoVehicles()
    {
        Assert.Empty(VehicleSpawnPlan.Build(new[] { Table(0, (1f, Array.Empty<ushort>())) }, Spread(2),
            Assets(), new SpawnTableDatabase(), ELevelSize.Medium, new Random(1)));
        // A table with no tiers at all is the same non-answer.
        Assert.Empty(VehicleSpawnPlan.Build(new[] { Table(0) }, Spread(2), Assets(),
            new SpawnTableDatabase(), ELevelSize.Medium, new Random(1)));
    }

    [Fact]
    public void Load_ReadsTheSpawnTablesFromASourcesOwnTree()
    {
        using var dir = new TempDir();
        // A map naming spawn table 295, and a source that keeps its tables directly under Spawns/ rather
        // than in a Vehicles subfolder — the layout a workshop item is free to use.
        string map = Directory.CreateDirectory(Path.Combine(dir.Path, "Maps", "Test", "Spawns")).FullName;
        File.WriteAllBytes(Path.Combine(map, "Vehicles.dat"), VehiclesDat());

        string bundles = Path.Combine(dir.Path, "Bundles");
        Directory.CreateDirectory(Path.Combine(bundles, "Objects"));
        Directory.CreateDirectory(Path.Combine(bundles, "Spawns"));
        File.WriteAllText(Path.Combine(bundles, "Spawns", "Cars.dat"),
            "GUID 20cdf1b0b0324866be88d5fb7a5c4507\nType Spawn\nID 295\n"
            + $"Tables 1\nTable_0_GUID {HatchbackGuid:N}\nTable_0_Weight 100\n");

        List<PlacedObject> placed = VehicleSpawnPlan.Load(new LevelInfo(Path.Combine(dir.Path, "Maps", "Test")),
            ContentSource.Discover(dir.Path), Assets());

        Assert.All(placed, p => Assert.Equal(HatchbackGuid, p.Guid));
        Assert.NotEmpty(placed);
    }

    [Fact]
    public void Load_YieldsNothingForAMapWithNoVehicleFile()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(Path.Combine(dir.Path, "Maps", "Test"));
        Assert.Empty(VehicleSpawnPlan.Load(new LevelInfo(Path.Combine(dir.Path, "Maps", "Test")),
            ContentSource.Discover(dir.Path), Assets()));
    }

    // One table deferring to spawn table 295, with two spawnpoints far enough apart to both be used.
    private static byte[] VehiclesDat()
    {
        var w = new RiverBytes().Byte(4).Byte(1);
        w.Byte(255).Byte(255).Byte(255).Str("Civilian").UInt16(295);
        w.Byte(1).Str("Tier").Single(1f).Byte(1).UInt16(30);
        w.UInt16(2);
        w.Byte(0).Vector3(new Vector3(0f, 0f, 0f)).Byte(0);
        w.Byte(0).Vector3(new Vector3(500f, 0f, 0f)).Byte(45);
        return w.ToArray();
    }

    [Fact]
    public void Build_IgnoresASpawnpointNamingATableTheMapDoesNotHave()
    {
        var spawns = new List<VehicleSpawnpointData> { new(9, Vector3.Zero, 0f) };

        Assert.Empty(VehicleSpawnPlan.Build(new[] { Table(0, (1f, new ushort[] { 40 })) }, spawns,
            Assets(), new SpawnTableDatabase(), ELevelSize.Medium, new Random(1)));
    }

    [RealDataFact(Map = "PEI")]
    public void Load_SpawnsPeisVehiclesAndRepeatsItselfExactly()
    {
        var level = new LevelInfo(GameData.Map("PEI")!);
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(GameData.Install!);
        ObjectAssetDatabase assets = ContentExtraction.ScanAssets(sources);

        List<PlacedObject> placed = VehicleSpawnPlan.Load(level, sources, assets);
        Assert.NotEmpty(placed);
        // PEI is a Medium map, so the pass takes at most sixteen spawnpoints.
        Assert.True(placed.Count <= VehicleSpawnPlan.NaturalVehicleCount(LevelSize.Read(level.Path)));

        // Everything placed is a real vehicle with a prefab, not a redirector left unresolved.
        foreach (PlacedObject vehicle in placed)
            Assert.Equal(EObjectType.Vehicle, assets.ResolveByGuid(vehicle.Guid)?.Type);

        // The seed is fixed, so a screenshot or benchmark of a map shows the same vehicles every run.
        List<PlacedObject> again = VehicleSpawnPlan.Load(level, sources, assets);
        Assert.Equal(placed.ConvertAll(v => (v.Guid, v.Position)), again.ConvertAll(v => (v.Guid, v.Position)));
    }
}

public class LevelSizeTests
{
    [Theory]
    [InlineData(0, ELevelSize.Tiny)]
    [InlineData(2, ELevelSize.Medium)]
    [InlineData(4, ELevelSize.Insane)]
    [InlineData(9, ELevelSize.Medium)] // out of range: fall back to the editor's default
    public void Read_TakesTheSizeFromAfterTheVersionAndAuthorId(byte stored, ELevelSize expected)
    {
        using var dir = new TempDir();
        var bytes = new byte[10];
        bytes[0] = 1;          // SAVEDATA version
        bytes[9] = stored;     // after the 8-byte CSteamID
        File.WriteAllBytes(Path.Combine(dir.Path, "Level.dat"), bytes);

        Assert.Equal(expected, LevelSize.Read(dir.Path));
    }

    [Fact]
    public void Read_FallsBackWhenTheFileIsMissingOrTruncated()
    {
        using var dir = new TempDir();
        Assert.Equal(ELevelSize.Medium, LevelSize.Read(dir.Path));

        File.WriteAllBytes(Path.Combine(dir.Path, "Level.dat"), new byte[] { 1, 2, 3 });
        Assert.Equal(ELevelSize.Medium, LevelSize.Read(dir.Path));
    }

    [RealDataFact(Map = "PEI")]
    public void Read_ReadsPeiAsAMediumMap()
    {
        Assert.Equal(ELevelSize.Medium, LevelSize.Read(GameData.Map("PEI")!));
    }
}
