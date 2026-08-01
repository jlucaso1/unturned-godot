using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One place a player can start, as the map author placed it.
public readonly struct PlayerSpawnpoint
{
    public PlayerSpawnpoint(Vector3 position, float yawDegrees, bool isAlternate)
    {
        Position = position;
        YawDegrees = yawDegrees;
        IsAlternate = isAlternate;
    }

    // Godot space (Unity's Z mirrored), metres.
    public Vector3 Position { get; }

    // Facing, in degrees around Y.
    public float YawDegrees { get; }

    // Arena maps keep a second set of spawns for their alternate round; normal play uses the others.
    public bool IsAlternate { get; }
}

// Ports Unturned's player spawnpoint file (Spawns/Players.dat, LevelPlayers): a version byte, a count
// byte, then one 14-byte record per spawn — position as three floats, the facing as a half-degree byte
// (0-179 covers the full turn), and the alternate-spawn flag.
//
// Verified against the shipped maps: every one of PEI, Washington, Yukon, Germany, Russia, Alpha Valley,
// Tutorial and Monolith is version 4 and its file length is exactly 2 + count * 14.
public static class LevelPlayers
{
    private const int RecordSize = 14;

    // Spawns for a map directory, in file order; empty when the map ships none (or the file is damaged).
    public static List<PlayerSpawnpoint> Load(string mapDirectory)
    {
        var spawns = new List<PlayerSpawnpoint>();
        string path = Path.Combine(mapDirectory, "Spawns", "Players.dat");
        if (!File.Exists(path))
            return spawns;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return spawns;
        }

        if (bytes.Length < 2 || bytes[0] == 0)
            return spawns;

        int count = bytes[1];
        for (int i = 0; i < count; i++)
        {
            int offset = 2 + (i * RecordSize);
            if (offset + RecordSize > bytes.Length)
                break; // truncated file: keep what parsed

            float x = System.BitConverter.ToSingle(bytes, offset);
            float y = System.BitConverter.ToSingle(bytes, offset + 4);
            float z = System.BitConverter.ToSingle(bytes, offset + 8);

            // The Z mirror that takes Unity to Godot also flips the direction the spawn faces.
            spawns.Add(new PlayerSpawnpoint(
                Landscape.UnityToGodot(new Vector3(x, y, z)),
                -(bytes[offset + 12] * 2f),
                bytes[offset + 13] != 0));
        }

        return spawns;
    }

    // The spawn to start a single-player session at: the first normal one, else the first alternate.
    // Deterministic on purpose, so screenshots and benchmarks of a map are repeatable.
    public static PlayerSpawnpoint? Choose(IReadOnlyList<PlayerSpawnpoint> spawns)
    {
        foreach (PlayerSpawnpoint spawn in spawns)
            if (!spawn.IsAlternate)
                return spawn;

        return spawns.Count > 0 ? spawns[0] : null;
    }
}
