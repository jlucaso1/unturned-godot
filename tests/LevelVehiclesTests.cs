using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelVehiclesTests
{
    private readonly record struct Tier(string Name, float Chance, ushort[] Vehicles);
    private readonly record struct Table(string Name, ushort TableId, Tier[] Tiers);
    private readonly record struct Point(byte Type, Vector3 At, byte HalfDegrees);

    // Serializes a Spawns/Vehicles.dat for a given SAVEDATA_VERSION, matching LevelVehicles.load's
    // version-gated layout exactly.
    private static byte[] Build(int version, Table[] tables, Point[] points)
    {
        var w = new RiverBytes().Byte((byte)version);
        if (version > 1 && version < 3)
            w.UInt64(76561198000000000);

        w.Byte((byte)tables.Length);
        foreach (Table table in tables)
        {
            w.Byte(255).Byte(128).Byte(0);
            w.Str(table.Name);
            if (version > 3)
                w.UInt16(table.TableId);

            w.Byte((byte)table.Tiers.Length);
            foreach (Tier tier in table.Tiers)
            {
                w.Str(tier.Name).Single(tier.Chance).Byte((byte)tier.Vehicles.Length);
                foreach (ushort vehicle in tier.Vehicles)
                    w.UInt16(vehicle);
            }
        }

        w.UInt16((ushort)points.Length);
        foreach (Point point in points)
            w.Byte(point.Type).Vector3(point.At).Byte(point.HalfDegrees);
        return w.ToArray();
    }

    private static (List<VehicleTable>, List<VehicleSpawnpointData>) Load(byte[] bytes)
    {
        using var dir = new TempDir();
        string path = Path.Combine(dir.Path, "Vehicles.dat");
        File.WriteAllBytes(path, bytes);
        return LevelVehicles.Load(path);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Load_ReadsTablesTiersAndSpawnpointsForEveryVersion(int version)
    {
        var tables = new[]
        {
            new Table("Civilian", 295, new[]
            {
                new Tier("Offroader", 0.25f, new ushort[] { 1, 2 }),
                new Tier("Truck", 0.75f, new ushort[] { 17 }),
            }),
        };
        var points = new[]
        {
            new Point(0, new Vector3(10f, 20f, 30f), 45), // 90 degrees
            new Point(0, new Vector3(-5f, 0f, 5f), 0),
        };

        (List<VehicleTable> read, List<VehicleSpawnpointData> spawns) =
            Load(Build(version, tables, points));

        VehicleTable table = Assert.Single(read);
        Assert.Equal("Civilian", table.Name);
        // The table id only exists from version 4; older files leave it at zero and use the tiers.
        Assert.Equal(version > 3 ? (ushort)295 : (ushort)0, table.TableId);
        Assert.Equal(2, table.Tiers.Count);

        Assert.Equal(2, spawns.Count);
        Assert.Equal(new Vector3(10f, 20f, 30f), spawns[0].Point); // Unity space, as read
        Assert.Equal(90f, spawns[0].AngleDegrees);
        Assert.Equal(0f, spawns[1].AngleDegrees);
    }

    [Fact]
    public void Load_BuildsTheCumulativeTierLadderAscending()
    {
        var tables = new[]
        {
            new Table("Civilian", 0, new[]
            {
                new Tier("Common", 0.6f, new ushort[] { 1 }),
                new Tier("Rare", 0.1f, new ushort[] { 2 }),
                new Tier("Uncommon", 0.3f, new ushort[] { 3 }),
            }),
        };

        (List<VehicleTable> read, _) = Load(Build(4, tables, System.Array.Empty<Point>()));

        VehicleTable table = Assert.Single(read);
        // Sorted ascending by chance, then accumulated: 0.1, 0.1+0.3, 0.1+0.3+0.6.
        Assert.Equal(new[] { "Rare", "Uncommon", "Common" },
            table.Tiers.ConvertAll(tier => tier.Name).ToArray());
        Assert.Equal(0.1f, table.Tiers[0].Chance, 5);
        Assert.Equal(0.4f, table.Tiers[1].Chance, 5);
        Assert.Equal(1.0f, table.Tiers[2].Chance, 5);
    }

    [Fact]
    public void Load_MissingFileAndVersionZeroYieldNothing()
    {
        using var dir = new TempDir();
        (List<VehicleTable> tables, List<VehicleSpawnpointData> spawns) =
            LevelVehicles.Load(Path.Combine(dir.Path, "absent.dat"));
        Assert.Empty(tables);
        Assert.Empty(spawns);

        (tables, spawns) = Load(new byte[] { 0 });
        Assert.Empty(tables);
        Assert.Empty(spawns);
    }

    [RealDataFact(Map = "PEI")]
    public void Load_ReadsPeisRealVehicleFile()
    {
        (List<VehicleTable> tables, List<VehicleSpawnpointData> spawns) =
            LevelVehicles.Load(Path.Combine(GameData.Map("PEI")!, "Spawns", "Vehicles.dat"));

        Assert.NotEmpty(tables);
        Assert.NotEmpty(spawns);
        // Every spawnpoint names a table that exists, which is what the roll indexes into.
        foreach (VehicleSpawnpointData spawn in spawns)
            Assert.True(spawn.Type < tables.Count, $"spawnpoint names table {spawn.Type}");
        // PEI is a modern map: its tables defer to spawn table assets.
        Assert.Contains(tables, table => table.TableId != 0);
    }
}
