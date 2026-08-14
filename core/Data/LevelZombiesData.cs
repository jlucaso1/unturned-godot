using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One zombie table (LevelZombies.load's version > 2 branch): a themed kind of zombie — Police, Military,
// Civilian, the mega 'Special' — with its stats and four clothing slots (shirt/pants/hat/gear), each a
// spawn chance plus the clothing item ids it can roll.
public sealed class ZombieTable
{
    public string Name = string.Empty;
    public bool IsMega;
    public ushort Health;
    public byte Damage;
    public byte LootIndex;

    // ZombieTable.lootID: the spawn table a killed zombie of this kind rolls its drop from. Read but not
    // yet spent — nothing drops loot here, and the drop path is what would spend it.
    public ushort LootId;

    public uint Xp;
    public float Regen;

    // ZombieTable.difficultyGUID: the ZombieDifficultyAsset this table spawns under. It is the other
    // half of ZombieManager.GetDifficultyInBoundForTable — the navigation bound names one and so does
    // the table, and the level asset's ZombieDifficultyAssetPrioritization decides which wins. Parsed
    // and thrown away until now, which is what forced the speciality chances to be constants.
    public Guid DifficultyGuid;

    public readonly List<(float Chance, List<ushort> Items)> Slots = new();
}

public readonly struct ZombieSpawnpointData
{
    public readonly byte Type; // index into the tables list
    public readonly Vector3 Point;

    public ZombieSpawnpointData(byte type, Vector3 point)
    {
        Type = type;
        Point = point;
    }
}

// Ports LevelZombies.load: the tables come from Spawns/Zombies.dat (Unturned's Block format — byte-length
// strings, RGB byte colors, little-endian numbers) and the spawn POINTS from Spawns/Animals.dat (a River;
// the file name is a historical quirk — animals live in Fauna.dat).
public static class LevelZombiesData
{
    public const int RegionsWorldSize = 64; // Regions.WORLD_SIZE

    public static List<ZombieTable> LoadTables(string zombiesDatPath)
    {
        var tables = new List<ZombieTable>();
        if (!File.Exists(zombiesDatPath))
            return tables;

        byte[] d = File.ReadAllBytes(zombiesDatPath);
        int pos = 0;
        byte version = d[pos++];
        if (version <= 3) // ancient formats: no shipped map uses them
            return tables;
        if (version < 5)
            pos += 8; // CSteamID

        if (version >= 10)
            pos += 4; // nextTableUniqueId

        byte tableCount = d[pos++];
        for (int t = 0; t < tableCount; t++)
        {
            if (version >= 10)
                pos += 4; // per-table unique id
            pos += 3; // editor color (RGB bytes)

            var table = new ZombieTable();
            table.Name = ReadBlockString(d, ref pos);
            table.IsMega = d[pos++] != 0;
            table.Health = BitConverter.ToUInt16(d, pos);
            pos += 2;
            table.Damage = d[pos++];
            table.LootIndex = d[pos++];
            if (version > 6)
            {
                table.LootId = BitConverter.ToUInt16(d, pos);
                pos += 2;
            }
            table.Xp = version > 7 ? BitConverter.ToUInt32(d, pos) : (table.IsMega ? 40u : 3u);
            if (version > 7)
                pos += 4;
            table.Regen = 10f;
            if (version > 5)
            {
                table.Regen = BitConverter.ToSingle(d, pos);
                pos += 4;
            }
            if (version > 8 && Guid.TryParse(ReadBlockString(d, ref pos), out Guid difficulty))
                table.DifficultyGuid = difficulty;

            byte slotCount = d[pos++];
            for (int s = 0; s < slotCount; s++)
            {
                float chance = BitConverter.ToSingle(d, pos);
                pos += 4;
                byte clothCount = d[pos++];
                var items = new List<ushort>(clothCount);
                for (int c = 0; c < clothCount; c++)
                {
                    items.Add(BitConverter.ToUInt16(d, pos));
                    pos += 2;
                }
                table.Slots.Add((chance, items));
            }
            tables.Add(table);
        }
        return tables;
    }

    // Spawns/Animals.dat: one River, 64x64 regions each holding (table index, position) spawnpoints.
    public static List<ZombieSpawnpointData> LoadSpawnpoints(string animalsDatPath)
    {
        var spawns = new List<ZombieSpawnpointData>();
        if (!File.Exists(animalsDatPath))
            return spawns;

        using var river = new River(animalsDatPath);
        byte version = river.ReadByte();
        if (version == 0)
            return spawns;

        for (int x = 0; x < RegionsWorldSize; x++)
        {
            for (int y = 0; y < RegionsWorldSize; y++)
            {
                ushort count = river.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    byte type = river.ReadByte();
                    Vector3 point = river.ReadSingleVector3();
                    spawns.Add(new ZombieSpawnpointData(type, point));
                }
            }
        }
        return spawns;
    }

    // Block.readString: single byte length + UTF-8 bytes.
    private static string ReadBlockString(byte[] d, ref int pos)
    {
        byte length = d[pos++];
        string value = System.Text.Encoding.UTF8.GetString(d, pos, length);
        pos += length;
        return value;
    }
}
