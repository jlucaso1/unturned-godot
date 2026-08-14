using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Config;

namespace UnturnedGodot.Data;

// Decides which vehicles a map starts with and where, porting the natural-spawn pass VehicleManager runs
// once the level is loaded: take as many of the map's spawnpoints as the level's size allows, in random
// order, skipping any that sit too close to a vehicle already placed, and roll each one's table for the
// vehicle it gets.
//
// The result is expressed as PlacedObjects so the vehicles ride the exact same path as everything else the
// map places: one transform per instance through ObjectPlacement, batched per GUID by ObjectsBuilder.
public static class VehicleSpawnPlan
{
    // VehicleManager.canUseSpawnpoint: a spawnpoint within this of an already-spawned vehicle is skipped,
    // so a car park does not stack its vehicles into one another.
    private const float MinSpacingMetres = 8f;

    // addVehicleAtSpawn lifts the spawn point before instantiating, because the vehicle is a rigidbody
    // that then settles onto the ground. Nothing here simulates, so this is simply where the game puts it.
    private const float SpawnHeightOffset = 0.5f;

    private static int MaxInstancesFor(ELevelSize size) => size switch
    {
        ELevelSize.Tiny => 4,
        ELevelSize.Small => 8,
        ELevelSize.Large => 32,
        ELevelSize.Insane => 64,
        _ => 16, // Medium
    };

    // GetNumberOfNaturalVehiclesToSpawn with no vehicles loaded from a save, which is always the case
    // here. The floor is Provider.modeConfigData.Vehicles.Min_Natural_Vehicles (VehicleManager.cs:2475),
    // which used to be a 16 written out here because nothing read the mode config. It comes from the
    // config record now — whose own default is the same 16 ZombiesConfigData's sibling constructor
    // assigns, so an unconfigured host is unchanged and a configured one is finally listened to.
    public static int NaturalVehicleCount(ELevelSize size, ModeConfigData? config = null) =>
        Math.Max(MaxInstancesFor(size),
            (int)(config ?? ModeConfigData.Normal).Vehicles.MinNaturalVehicles);

    // Which vehicles a map rolls is random in Unturned, and nothing here simulates them afterwards, so the
    // port fixes the seed: a screenshot or a benchmark of a map has to show the same vehicles every run.
    // Each map still gets its own answer, because it brings its own spawnpoints and tables.
    private const int DeterministicSeed = 0x5645_4831; // "VEH1"

    public static List<PlacedObject> Load(LevelInfo level, IReadOnlyList<ContentSource> sources,
        ObjectAssetDatabase assets) =>
        Load(level, sources, assets, new Random(DeterministicSeed));

    // Everything a map's vehicles need, read from the map and the installed content: the spawnpoints and
    // tables from Spawns/Vehicles.dat, and — only for tables that defer to one — the spawn table assets.
    public static List<PlacedObject> Load(LevelInfo level, IReadOnlyList<ContentSource> sources,
        ObjectAssetDatabase assets, Random random)
    {
        (List<VehicleTable> tables, List<VehicleSpawnpointData> spawnpoints) =
            LevelVehicles.Load(Path.Combine(level.Path, "Spawns", "Vehicles.dat"));
        if (spawnpoints.Count == 0)
            return new List<PlacedObject>();

        var spawnTables = new SpawnTableDatabase();
        if (NeedsSpawnTables(tables))
            foreach (ContentSource source in sources)
                foreach (SpawnTableAsset table in SpawnTableDatabase.ScanDirectory(TablesDir(source)).All)
                    spawnTables.AddIfAbsent(table);

        return Build(tables, spawnpoints, assets, spawnTables, LevelSize.Read(level.Path), random);
    }

    // Every table on every shipped map that sets a TableId resolves within the vehicle subtree, so that is
    // all the vehicle path reads. A source that keeps its tables elsewhere gets its whole Spawns tree
    // scanned instead, which is still correct: the legacy ids are one namespace across the categories.
    private static string TablesDir(ContentSource source)
    {
        string vehicles = Path.Combine(source.SpawnsDir, "Vehicles");
        return Directory.Exists(vehicles) ? vehicles : source.SpawnsDir;
    }

    private static bool NeedsSpawnTables(IReadOnlyList<VehicleTable> tables)
    {
        foreach (VehicleTable table in tables)
            if (table.TableId != 0)
                return true;
        return false;
    }

    public static List<PlacedObject> Build(IReadOnlyList<VehicleTable> tables,
        IReadOnlyList<VehicleSpawnpointData> spawnpoints, ObjectAssetDatabase assets,
        SpawnTableDatabase spawnTables, ELevelSize size, Random random)
    {
        var placed = new List<PlacedObject>();
        if (tables.Count == 0 || spawnpoints.Count == 0)
            return placed;

        // A copy, drawn from without replacement: the original takes each candidate out of the list as it
        // considers it, so a spawnpoint rejected for being crowded is not offered again.
        var candidates = new List<VehicleSpawnpointData>(spawnpoints);
        var taken = new List<Vector3>();
        int remaining = NaturalVehicleCount(size);

        while (remaining > 0 && candidates.Count > 0)
        {
            remaining--;
            int index = random.Next(candidates.Count);
            VehicleSpawnpointData spawn = candidates[index];
            candidates.RemoveAt(index);

            if (!IsClear(taken, spawn.Point))
                continue;

            if (ResolveVehicle(tables, spawn.Type, assets, spawnTables, random) is not { } vehicle)
                continue;

            taken.Add(spawn.Point);
            Vector3 point = spawn.Point with { Y = spawn.Point.Y + SpawnHeightOffset };
            // Unity's Quaternion.Euler(0, angle, 0): the spawnpoint's yaw is its whole orientation.
            placed.Add(new PlacedObject(point, new Vector3(0f, spawn.AngleDegrees, 0f), Vector3.One,
                id: 0, vehicle.Guid));
        }

        return placed;
    }

    private static bool IsClear(List<Vector3> taken, Vector3 point)
    {
        foreach (Vector3 other in taken)
            if (other.DistanceSquaredTo(point) < MinSpacingMetres * MinSpacingMetres)
                return false;
        return true;
    }

    // LevelVehicles.GetRandomAssetForSpawnpoint: a table with a spawn table asset defers to it, and one
    // without falls back to the tiers the map editor saved. Either way the answer may be a redirector,
    // which ResolveVehicle follows to the vehicle that owns the prefab.
    private static ObjectAsset? ResolveVehicle(IReadOnlyList<VehicleTable> tables, byte type,
        ObjectAssetDatabase assets, SpawnTableDatabase spawnTables, Random random)
    {
        if (type >= tables.Count)
            return null;

        VehicleTable table = tables[type];
        if (table.TableId != 0)
        {
            if (spawnTables.ById(table.TableId) is not { } spawnTable)
                return null;
            (Guid guid, ushort assetId) = spawnTables.Resolve(spawnTable, () => (float)random.NextDouble());
            return assets.ResolveVehicle(guid, assetId);
        }

        ushort legacyId = RollLegacyVehicleId(table, random);
        return legacyId == 0 ? null : assets.ResolveVehicle(Guid.Empty, legacyId);
    }

    // VehicleTable.GetRandomLegacyVehicleId: walk the cumulative tier ladder LevelVehicles.BuildTable left
    // behind, then take one of that tier's vehicles. A roll past the end of the ladder (the chances need
    // not sum to one) falls back to any tier, exactly as the original does.
    private static ushort RollLegacyVehicleId(VehicleTable table, Random random)
    {
        if (table.Tiers.Count == 0)
            return 0;

        float roll = (float)random.NextDouble();
        foreach (VehicleTier tier in table.Tiers)
            if (roll < tier.Chance)
                return Pick(tier, random);

        return Pick(table.Tiers[random.Next(table.Tiers.Count)], random);
    }

    private static ushort Pick(VehicleTier tier, Random random) =>
        tier.Vehicles.Count > 0 ? tier.Vehicles[random.Next(tier.Vehicles.Count)] : (ushort)0;
}
