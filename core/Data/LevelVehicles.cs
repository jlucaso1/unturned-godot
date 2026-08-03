using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One tier of a map's vehicle table: the legacy vehicle ids it can roll, and the share of the table's
// rolls it takes. Chance is the authored per-tier share as it sits in the file; BuildTable turns the set
// of them into the ascending cumulative ladder the roll walks.
public sealed class VehicleTier
{
    public string Name = string.Empty;
    public float Chance;
    public readonly List<ushort> Vehicles = new();
}

// One themed group of vehicles a map places — "Civilian", "Police", "Military_Canada" — as the map editor
// saved it. A modern map sets TableId and lets a spawn table asset decide what actually rolls; the tiers
// are the pre-GUID fallback that maps still carry, and Unturned uses them only when TableId is zero.
public sealed class VehicleTable
{
    public string Name = string.Empty;
    public ushort TableId;
    public readonly List<VehicleTier> Tiers = new();

    // Ports VehicleTable.buildTable: sort the tiers by ascending chance, then replace each chance with the
    // running total, so a single [0,1) roll picks a tier by walking until it falls under one.
    public void BuildTable()
    {
        var sorted = new List<VehicleTier>(Tiers.Count);
        foreach (VehicleTier tier in Tiers)
        {
            int at = -1;
            for (int i = 0; i < sorted.Count; i++)
                if (tier.Chance < sorted[i].Chance)
                {
                    at = i;
                    break;
                }

            if (at < 0)
                sorted.Add(tier);
            else
                sorted.Insert(at, tier);
        }

        float total = 0f;
        foreach (VehicleTier tier in sorted)
        {
            total += tier.Chance;
            tier.Chance = total;
        }

        Tiers.Clear();
        Tiers.AddRange(sorted);
    }
}

// One place a vehicle can appear, as the map author placed it. Kept in Unity space with the authored yaw,
// like the placed objects: VehicleSpawnPlan hands both to ObjectPlacement, which is the one place the
// Unity->Godot flip lives.
public readonly struct VehicleSpawnpointData
{
    public readonly byte Type;           // index into the tables list
    public readonly Vector3 Point;       // Unity world space
    public readonly float AngleDegrees;  // Unity yaw

    public VehicleSpawnpointData(byte type, Vector3 point, float angleDegrees)
    {
        Type = type;
        Point = point;
        AngleDegrees = angleDegrees;
    }
}

// Ports LevelVehicles.load (Unturned/Level/LevelVehicles.cs) for SAVEDATA_VERSION 4: the map's vehicle
// tables followed by a flat list of spawnpoints, each naming the table it rolls from.
public static class LevelVehicles
{
    public static (List<VehicleTable> Tables, List<VehicleSpawnpointData> Spawns) Load(string vehiclesDatPath)
    {
        var tables = new List<VehicleTable>();
        var spawns = new List<VehicleSpawnpointData>();
        if (!File.Exists(vehiclesDatPath))
            return (tables, spawns);

        using var river = new River(vehiclesDatPath);

        byte version = river.ReadByte();
        if (version == 0)
            return (tables, spawns);

        if (version > 1 && version < 3)
            river.ReadUInt64(); // CSteamID

        byte tableCount = river.ReadByte();
        for (int t = 0; t < tableCount; t++)
        {
            river.ReadByte(); // editor color (RGB bytes)
            river.ReadByte();
            river.ReadByte();

            var table = new VehicleTable { Name = river.ReadString() };
            if (version > 3)
                table.TableId = river.ReadUInt16();

            byte tierCount = river.ReadByte();
            for (int i = 0; i < tierCount; i++)
            {
                var tier = new VehicleTier { Name = river.ReadString(), Chance = river.ReadSingle() };
                byte vehicleCount = river.ReadByte();
                for (int v = 0; v < vehicleCount; v++)
                    tier.Vehicles.Add(river.ReadUInt16());
                table.Tiers.Add(tier);
            }

            // The original builds the ladder here too, on everything but the editor.
            table.BuildTable();
            tables.Add(table);
        }

        ushort pointCount = river.ReadUInt16();
        for (int i = 0; i < pointCount; i++)
        {
            byte type = river.ReadByte();
            Vector3 point = river.ReadSingleVector3();
            // The yaw is stored as a half-degree byte, the same way player spawns store their facing.
            spawns.Add(new VehicleSpawnpointData(type, point, river.ReadByte() * 2f));
        }

        return (tables, spawns);
    }
}
