using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

public class ZombieDataTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("zombie-data-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string WriteFile(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteBlockString(BinaryWriter w, string s)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
        w.Write((byte)bytes.Length);
        w.Write(bytes);
    }

    private static byte[] TablesV10()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)10);      // version
        w.Write(3);             // nextTableUniqueId
        w.Write((byte)2);       // tableCount

        w.Write(1);             // unique id
        w.Write(new byte[] { 10, 20, 30 }); // editor color
        WriteBlockString(w, "Civilian");
        w.Write(false);         // mega
        w.Write((ushort)100);   // health
        w.Write((byte)10);      // damage
        w.Write((byte)0);       // loot index
        w.Write((ushort)0);     // loot id
        w.Write(3u);            // xp
        w.Write(10f);           // regen
        WriteBlockString(w, "00000000000000000000000000000000");
        w.Write((byte)2);       // slots
        w.Write(1f);            // shirt slot: always worn
        w.Write((byte)2);
        w.Write((ushort)151);
        w.Write((ushort)152);
        w.Write(0f);            // pants slot: never worn
        w.Write((byte)1);
        w.Write((ushort)153);

        w.Write(2);
        w.Write(new byte[] { 1, 2, 3 });
        WriteBlockString(w, "Special");
        w.Write(true);
        w.Write((ushort)2999);
        w.Write((byte)33);
        w.Write((byte)0);
        w.Write((ushort)0);
        w.Write(40u);
        w.Write(10f);
        WriteBlockString(w, "00000000000000000000000000000000");
        w.Write((byte)0);       // no slots
        return ms.ToArray();
    }

    [Fact]
    public void Tables_V10_ParsesStatsAndSlots()
    {
        List<ZombieTable> tables = LevelZombiesData.LoadTables(WriteFile("Zombies.dat", TablesV10()));

        Assert.Equal(2, tables.Count);
        Assert.Equal("Civilian", tables[0].Name);
        Assert.False(tables[0].IsMega);
        Assert.Equal(100, tables[0].Health);
        Assert.Equal(10, tables[0].Damage);
        Assert.Equal(3u, tables[0].Xp);
        Assert.Equal(10f, tables[0].Regen);
        Assert.Equal(2, tables[0].Slots.Count);
        Assert.Equal(1f, tables[0].Slots[0].Chance);
        Assert.Equal(new ushort[] { 151, 152 }, tables[0].Slots[0].Items);

        Assert.Equal("Special", tables[1].Name);
        Assert.True(tables[1].IsMega);
        Assert.Equal(2999, tables[1].Health);
        Assert.Equal(33, tables[1].Damage);
        Assert.Empty(tables[1].Slots);
    }

    [Fact]
    public void Tables_V4_UsesLegacyDefaults()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)4);           // version: pre-5 carries a CSteamID, no xp/regen/guid fields
        w.Write(0UL);               // CSteamID
        w.Write((byte)2);
        foreach (bool mega in new[] { false, true })
        {
            w.Write(new byte[] { 0, 0, 0 });
            WriteBlockString(w, mega ? "Mega" : "Old");
            w.Write(mega);
            w.Write((ushort)50);
            w.Write((byte)5);
            w.Write((byte)0);
            w.Write((byte)0);       // slots
        }

        List<ZombieTable> tables = LevelZombiesData.LoadTables(WriteFile("Zombies.dat", ms.ToArray()));
        Assert.Equal(2, tables.Count);
        Assert.Equal(3u, tables[0].Xp);   // legacy default
        Assert.Equal(40u, tables[1].Xp);  // legacy mega default
        Assert.Equal(10f, tables[0].Regen);
    }

    [Fact]
    public void Tables_AncientVersionOrMissingFile_ReturnEmpty()
    {
        Assert.Empty(LevelZombiesData.LoadTables(WriteFile("Zombies.dat", new byte[] { 3 })));
        Assert.Empty(LevelZombiesData.LoadTables(Path.Combine(_dir, "nope.dat")));
    }

    private static byte[] Spawnpoints(params (byte Type, Vector3 Point)[] spawns)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)1); // version
        // All spawns land in region (0,0); the other 4095 regions are empty.
        w.Write((ushort)spawns.Length);
        foreach ((byte type, Vector3 p) in spawns)
        {
            w.Write(type);
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
        }
        for (int i = 1; i < 64 * 64; i++)
            w.Write((ushort)0);
        return ms.ToArray();
    }

    [Fact]
    public void Spawnpoints_ReadAcrossRegions()
    {
        string path = WriteFile("Animals.dat", Spawnpoints(
            (0, new Vector3(10, 5, -20)),
            (1, new Vector3(-3, 0, 7))));

        List<ZombieSpawnpointData> spawns = LevelZombiesData.LoadSpawnpoints(path);
        Assert.Equal(2, spawns.Count);
        Assert.Equal(0, spawns[0].Type);
        Assert.Equal(new Vector3(10, 5, -20), spawns[0].Point);
        Assert.Equal(1, spawns[1].Type);
    }

    [Fact]
    public void Spawnpoints_VersionZeroOrMissingFile_ReturnEmpty()
    {
        Assert.Empty(LevelZombiesData.LoadSpawnpoints(WriteFile("Animals.dat", new byte[] { 0 })));
        Assert.Empty(LevelZombiesData.LoadSpawnpoints(Path.Combine(_dir, "nope.dat")));
    }

    private void WriteBounds(params (Vector3 Center, Vector3 Size)[] bounds)
    {
        using var w = new BinaryWriter(File.Create(Path.Combine(_dir, "Bounds.dat")));
        w.Write((byte)1);
        w.Write((byte)bounds.Length);
        foreach ((Vector3 c, Vector3 s) in bounds)
        {
            w.Write(c.X);
            w.Write(c.Y);
            w.Write(c.Z);
            w.Write(s.X);
            w.Write(s.Y);
            w.Write(s.Z);
        }
    }

    private void WriteFlags(byte version, params (byte MaxZombies, bool Spawn, bool Hyper)[] flags)
    {
        using var w = new BinaryWriter(File.Create(Path.Combine(_dir, "Flags_Data.dat")));
        w.Write(version);
        w.Write((byte)flags.Length);
        foreach ((byte max, bool spawn, bool hyper) in flags)
        {
            w.Write((byte)0); // empty difficulty GUID string
            if (version > 1)
                w.Write(max);
            if (version > 2)
                w.Write(spawn);
            if (version >= 4)
                w.Write(hyper);
            if (version >= 5)
                w.Write(0);   // maxBossZombies
        }
    }

    [Fact]
    public void Navigation_LoadsBoundsWithFlagData()
    {
        WriteBounds((new Vector3(0, 140, 0), new Vector3(100, 300, 100)),
            (new Vector3(500, 140, 0), new Vector3(80, 300, 80)));
        WriteFlags(5, (12, true, false), (0, false, true));

        List<NavBound> bounds = LevelNavigationData.Load(_dir);
        Assert.Equal(2, bounds.Count);
        Assert.Equal(12, bounds[0].MaxZombies);
        Assert.True(bounds[0].SpawnZombies);
        Assert.False(bounds[0].HyperAgro);
        Assert.False(bounds[1].SpawnZombies);
        Assert.True(bounds[1].HyperAgro);
    }

    [Fact]
    public void Navigation_OldFlagVersions_LeaveDefaults()
    {
        WriteBounds((new Vector3(0, 0, 0), new Vector3(10, 10, 10)));
        WriteFlags(1, (99, false, true)); // v1 stores only the GUID: everything else stays default

        List<NavBound> bounds = LevelNavigationData.Load(_dir);
        Assert.Equal(64, bounds[0].MaxZombies);
        Assert.True(bounds[0].SpawnZombies);
        Assert.False(bounds[0].HyperAgro);
    }

    [Fact]
    public void Navigation_MissingFiles_ReturnEmpty()
    {
        Assert.Empty(LevelNavigationData.Load(_dir));
    }

    [Fact]
    public void Navigation_VersionZeroFiles_ReturnEmpty()
    {
        File.WriteAllBytes(Path.Combine(_dir, "Bounds.dat"), new byte[] { 0 });
        File.WriteAllBytes(Path.Combine(_dir, "Flags_Data.dat"), new byte[] { 0 });
        Assert.Empty(LevelNavigationData.Load(_dir));
    }

    [Fact]
    public void TryGetBound_MatchesXZFootprint()
    {
        var bounds = new List<NavBound>
        {
            new() { Center = new Vector3(0, 140, 0), Size = new Vector3(100, 300, 100) },
            new() { Center = new Vector3(500, 140, 0), Size = new Vector3(80, 300, 80) },
        };
        Assert.Equal(0, LevelNavigationData.TryGetBound(bounds, new Vector3(49, 0, -49)));
        Assert.Equal(1, LevelNavigationData.TryGetBound(bounds, new Vector3(510, 999, 10))); // Y ignored
        Assert.Equal(LevelNavigationData.NoBound, LevelNavigationData.TryGetBound(bounds, new Vector3(200, 0, 0)));
        Assert.Equal(LevelNavigationData.NoBound, LevelNavigationData.TryGetBound(bounds, new Vector3(0, 0, 200)));
    }

    // Real PEI data (self-skips without the game): 19 zombie tables, 1456 spawnpoints, 19 nav bounds.
    [Fact]
    public void RealPei_ZombieDataParses()
    {
        string pei = "/home/jlucaso/.local/share/Steam/steamapps/common/Unturned/Maps/PEI";
        if (!Directory.Exists(pei))
            return;

        List<ZombieTable> tables = LevelZombiesData.LoadTables(Path.Combine(pei, "Spawns", "Zombies.dat"));
        Assert.Equal(19, tables.Count);
        ZombieTable mega = Assert.Single(tables, t => t.IsMega);
        Assert.Equal("Special", mega.Name);
        Assert.Equal(2999, mega.Health);
        Assert.Equal(33, mega.Damage);

        List<ZombieSpawnpointData> spawns =
            LevelZombiesData.LoadSpawnpoints(Path.Combine(pei, "Spawns", "Animals.dat"));
        Assert.Equal(1456, spawns.Count);
        Assert.All(spawns, s => Assert.InRange(s.Type, 0, tables.Count - 1));

        List<NavBound> bounds = LevelNavigationData.Load(Path.Combine(pei, "Environment"));
        Assert.Equal(19, bounds.Count);
        Assert.All(bounds, b => Assert.True(b.Size.X > 0 && b.Size.Z > 0));

        // Every PEI spawnpoint sits inside a nav bound, exactly as the editor placed them.
        int inBounds = 0;
        foreach (ZombieSpawnpointData s in spawns)
        {
            Vector3 godot = new(s.Point.X, s.Point.Y, -s.Point.Z);
            if (LevelNavigationData.TryGetBound(bounds, godot) != LevelNavigationData.NoBound)
                inBounds++;
        }
        Assert.Equal(spawns.Count, inBounds);
    }
}
