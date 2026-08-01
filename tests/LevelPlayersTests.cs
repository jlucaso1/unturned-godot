using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelPlayersTests
{
    // Spawns/Players.dat exactly as LevelPlayers writes it: version, count, then 14 bytes per spawn.
    private static byte[] File(byte version, params (Vector3 Position, byte Angle, bool Alt)[] spawns)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(version);
        w.Write((byte)spawns.Length);
        foreach ((Vector3 p, byte angle, bool alt) in spawns)
        {
            w.Write(p.X);
            w.Write(p.Y);
            w.Write(p.Z);
            w.Write(angle);
            w.Write(alt);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Load_ReadsPositionsMirroredAndAnglesInDegrees()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Spawns", "Players.dat"), File(4,
            (new Vector3(-750.7f, 34.3f, -65.3f), 21, false),
            (new Vector3(10f, 5f, 20f), 90, true)));

        List<PlayerSpawnpoint> spawns = LevelPlayers.Load(dir.Path);

        Assert.Equal(2, spawns.Count);
        Assert.Equal(new Vector3(-750.7f, 34.3f, 65.3f), spawns[0].Position); // Unity Z mirrored
        Assert.Equal(-42f, spawns[0].YawDegrees);                             // half-degree byte, mirrored
        Assert.False(spawns[0].IsAlternate);
        Assert.Equal(new Vector3(10f, 5f, -20f), spawns[1].Position);
        Assert.Equal(-180f, spawns[1].YawDegrees);
        Assert.True(spawns[1].IsAlternate);
    }

    [Fact]
    public void Load_MissingFileOrVersionZero_ReturnsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(LevelPlayers.Load(dir.Path));

        dir.Write(Path.Combine("Spawns", "Players.dat"), new byte[] { 0, 3 });
        Assert.Empty(LevelPlayers.Load(dir.Path));
    }

    [Fact]
    public void Load_TooShortOrTruncatedFile_KeepsWhatParsed()
    {
        using var dir = new TempDir();
        dir.Write(Path.Combine("Spawns", "Players.dat"), new byte[] { 4 }); // header only
        Assert.Empty(LevelPlayers.Load(dir.Path));

        // Two spawns announced, one and a half written.
        byte[] full = File(4, (new Vector3(1, 2, 3), 0, false), (new Vector3(4, 5, 6), 0, false));
        dir.Write(Path.Combine("Spawns", "Players.dat"), full[..^5]);

        PlayerSpawnpoint only = Assert.Single(LevelPlayers.Load(dir.Path));
        Assert.Equal(new Vector3(1, 2, -3), only.Position);
    }

    [Fact]
    public void Load_UnreadableFile_ReturnsEmpty()
    {
        using var dir = new TempDir();
        string path = dir.Write(Path.Combine("Spawns", "Players.dat"),
            File(4, (new Vector3(1, 2, 3), 0, false)));

        // Held exclusively: spawns are a nice-to-have, so the map still loads without them.
        using var handle = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.None);
        Assert.Empty(LevelPlayers.Load(dir.Path));
    }

    [Fact]
    public void Choose_PrefersANormalSpawn_ThenFallsBackToAnAlternate()
    {
        var alternate = new PlayerSpawnpoint(new Vector3(1, 0, 0), 0f, isAlternate: true);
        var normal = new PlayerSpawnpoint(new Vector3(2, 0, 0), 90f, isAlternate: false);

        Assert.Equal(normal.Position, LevelPlayers.Choose(new[] { alternate, normal })!.Value.Position);
        Assert.Equal(alternate.Position, LevelPlayers.Choose(new[] { alternate })!.Value.Position);
        Assert.Null(LevelPlayers.Choose(System.Array.Empty<PlayerSpawnpoint>()));
    }

    // The shipped maps: every one is version 4 with a file length of exactly 2 + count * 14, and every
    // spawn sits inside the map's own bounds.
    [Theory]
    [InlineData("PEI", 22)]
    [InlineData("Washington", 24)]
    [InlineData("Yukon", 18)]
    [InlineData("Germany", 14)]
    public void Load_RealMap_MatchesTheShippedSpawnCount(string mapName, int expected)
    {
        if (GameData.Map(mapName) is not { } mapDir)
            return;

        List<PlayerSpawnpoint> spawns = LevelPlayers.Load(mapDir);

        Assert.Equal(expected, spawns.Count);
        Assert.NotNull(LevelPlayers.Choose(spawns));
        foreach (PlayerSpawnpoint spawn in spawns)
        {
            Assert.InRange(spawn.Position.Y, -100f, Landscape.TILE_HEIGHT);
            Assert.InRange(spawn.YawDegrees, -358f, 0f);
        }
    }
}
