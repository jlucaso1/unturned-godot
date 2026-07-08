using System.Linq;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelInfoTests
{
    [Fact]
    public void EnumerateTiles_ParsesCoordsAndIgnoresNonMatching()
    {
        using var dir = new TempDir();
        dir.Write("Landscape/Heightmaps/Tile_0_0_Source.heightmap", new byte[1]);
        dir.Write("Landscape/Heightmaps/Tile_-1_2_Source.heightmap", new byte[1]);
        dir.Write("Landscape/Heightmaps/notes.txt", new byte[1]);          // wrong extension
        dir.Write("Landscape/Heightmaps/Tile_bad.heightmap", new byte[1]); // regex miss

        var level = new LevelInfo(dir.Path);
        var tiles = level.EnumerateTiles();

        Assert.Equal(2, tiles.Count);
        Assert.Contains((0, 0), tiles);
        Assert.Contains((-1, 2), tiles);
    }

    [Fact]
    public void EnumerateTiles_MissingDir_ReturnsEmpty()
    {
        using var dir = new TempDir();
        Assert.Empty(new LevelInfo(dir.Path).EnumerateTiles());
    }

    [Fact]
    public void Paths_AndName()
    {
        var level = new LevelInfo("/games/Unturned/Maps/PEI");
        Assert.Equal("PEI", level.Name);
        Assert.EndsWith("Landscape/Heightmaps/Tile_1_-2_Source.heightmap".Replace('/', System.IO.Path.DirectorySeparatorChar),
            level.HeightmapPath(1, -2));
        Assert.EndsWith("Landscape/Splatmaps/Tile_1_-2_Source.splatmap".Replace('/', System.IO.Path.DirectorySeparatorChar),
            level.SplatmapPath(1, -2));
        Assert.EndsWith("Level/Objects.dat".Replace('/', System.IO.Path.DirectorySeparatorChar),
            level.ObjectsDat);
    }
}
