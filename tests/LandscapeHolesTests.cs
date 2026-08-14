using System.IO;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LandscapeHolesTests
{
    private const int Res = Landscape.HOLES_RESOLUTION;
    private const int BytesPerRow = Res / 8;

    // A holes file cutting exactly the given cells, written the way LandscapeTile.WriteHoles does: the
    // array starts out all-visible, so an intact tile is all ones and a cut cell is a CLEARED bit.
    private static byte[] File_(params (int X, int Y)[] cut)
    {
        var bytes = new byte[LandscapeHoles.FILE_BYTES];
        bytes[0] = 1;
        for (int i = 1; i < bytes.Length; i++)
            bytes[i] = 0xFF;
        foreach ((int x, int y) in cut)
            bytes[1 + (x * BytesPerRow) + (y >> 3)] &= (byte)~(1 << (y & 7));
        return bytes;
    }

    // The layout, read back the way the SDK writes it: a version byte, then rows of eight cells to a
    // byte, LSB first. Getting the bit order backwards would put every hole seven cells from where the
    // map authored it — a mistake that leaves the file parsing "successfully" and the map wrong.
    [Fact]
    public void ReadsTheVersionByteThenEightCellsPerByteLsbFirst()
    {
        // Bit 0 and bit 5 of the first body byte: cells (0, 0) and (0, 5).
        byte[] bytes = File_((0, 0), (0, 5));
        LandscapeHoles holes = LandscapeHoles.Parse(bytes, 3, -4);

        Assert.Equal(3, holes.CoordX);
        Assert.Equal(-4, holes.CoordY);
        Assert.Equal(1, holes.Version);
        Assert.True(holes.IsHole(0, 0));
        Assert.False(holes.IsHole(0, 1));
        Assert.False(holes.IsHole(0, 4));
        Assert.True(holes.IsHole(0, 5));
        Assert.False(holes.IsHole(0, 6));
        Assert.False(holes.IsHole(1, 0)); // the next ROW, not the next byte's worth of the first
        Assert.Equal(2, holes.HoleCount);
        Assert.True(holes.HasAnyHoles);
    }

    // Rows are 32 bytes apart, so a cell late in a row and the same bit in the next row must not be
    // confused. Cell (1, 0) is byte 32; cell (0, 255) is byte 31, bit 7.
    [Fact]
    public void RowsAreIndexedIndependently()
    {
        LandscapeHoles holes = LandscapeHoles.Parse(File_((1, 0), (0, 255), (255, 255)), 0, 0);

        Assert.True(holes.IsHole(1, 0));
        Assert.True(holes.IsHole(0, 255));
        Assert.True(holes.IsHole(255, 255));
        Assert.False(holes.IsHole(0, 0));
        Assert.False(holes.IsHole(1, 255));
        Assert.Equal(3, holes.HoleCount);
    }

    // Callers walk a 257-sample heightmap over a 256-cell grid, so the index one past the end turns up
    // constantly. It is ground, not an exception — and neither is a negative one, which the dilation in
    // TerrainHoleCollision reaches for at the tile's edge.
    [Fact]
    public void OutOfRangeCellsAreGround()
    {
        LandscapeHoles holes = LandscapeHoles.Parse(File_((0, 0)), 0, 0);

        Assert.False(holes.IsHole(-1, 0));
        Assert.False(holes.IsHole(0, -1));
        Assert.False(holes.IsHole(Res, 0));
        Assert.False(holes.IsHole(0, Res));
    }

    // A map with no holes on a tile still ships the file on some maps. Nothing downstream should take the
    // hole path for it.
    [Fact]
    public void AnAllOnesFileCutsNothing()
    {
        LandscapeHoles holes = LandscapeHoles.Parse(File_(), 0, 0);

        Assert.False(holes.HasAnyHoles);
        Assert.Equal(0, holes.HoleCount);
    }

    [Fact]
    public void EveryCellCutIsCountedAndReported()
    {
        var bytes = new byte[LandscapeHoles.FILE_BYTES]; // body all zero: nothing exists at all
        bytes[0] = 1;
        LandscapeHoles holes = LandscapeHoles.Parse(bytes, 0, 0);

        Assert.True(holes.HasAnyHoles);
        Assert.Equal(Res * Res, holes.HoleCount);
        Assert.True(holes.IsHole(128, 128));
    }

    // The common case is no file at all, and the SDK deliberately does not warn about it: maps predating
    // holes, and maps that simply have none, never write one.
    [Fact]
    public void AMissingFileIsNoHolesRatherThanAnError()
    {
        using var dir = new TempDir();
        Assert.Null(LandscapeHoles.TryRead(Path.Combine(dir.Path, "nope.bin"), 0, 0));
    }

    // A file that IS there but is short is a damaged install, not an old map. Reporting it matters
    // because the silent reading — treating it as no holes — fills in an entrance the map has.
    [Fact]
    public void AShortFileIsReported()
    {
        using var dir = new TempDir();
        string path = dir.Write("Tile_0_0.bin", new byte[64]);

        Assert.Throws<IOException>(() => LandscapeHoles.TryRead(path, 0, 0));
        Assert.Throws<IOException>(() => LandscapeHoles.Parse(new byte[LandscapeHoles.FILE_BYTES - 1], 0, 0));
    }

    [Fact]
    public void ReadsTheFileFromDisk()
    {
        using var dir = new TempDir();
        string path = dir.Write("Tile_-1_-1.bin", File_((62, 62)));

        LandscapeHoles? holes = LandscapeHoles.TryRead(path, -1, -1);

        Assert.NotNull(holes);
        Assert.True(holes!.IsHole(62, 62));
        Assert.Equal(1, holes.HoleCount);
    }

    // A tile is read from a heightmap path and has to find its own holes beside it: the two live in
    // sibling folders under the map's Landscape/.
    [Fact]
    public void FindsTheHolesBesideAHeightmap()
    {
        using var dir = new TempDir();
        dir.Write("Landscape/Holes/Tile_2_-3.bin", File_((7, 9)));
        string heightmap = Path.Combine(dir.Path, "Landscape", "Heightmaps", "Tile_2_-3_Source.heightmap");

        LandscapeHoles? holes = LandscapeHoles.TryReadBeside(heightmap, 2, -3);

        Assert.NotNull(holes);
        Assert.True(holes!.IsHole(7, 9));
        // And the same path LevelInfo names, so the two cannot drift apart.
        Assert.Equal(new LevelInfo(dir.Path).HolePath(2, -3),
            Path.Combine(dir.Path, "Landscape", "Holes", "Tile_2_-3.bin"));
    }

    [Fact]
    public void ATileWithNoHolesFolderReadsNone()
    {
        using var dir = new TempDir();
        string heightmap = dir.Write("Landscape/Heightmaps/Tile_0_0_Source.heightmap", new byte[1]);

        Assert.Null(LandscapeHoles.TryReadBeside(heightmap, 0, 0));
        // A bare filename has no Landscape/ above it to look under, and answers none rather than throwing.
        Assert.Null(LandscapeHoles.TryReadBeside("Tile_0_0_Source.heightmap", 0, 0));
    }

    // Coordinates are formatted invariantly: a negative tile is "Tile_-1_-1.bin" on every locale, which
    // is the name PEI actually ships.
    [Fact]
    public void FileNamesAreTheGamesOwn()
    {
        Assert.Equal("Tile_-1_-1.bin", LandscapeHoles.FileName(-1, -1));
        Assert.Equal("Tile_0_0.bin", LandscapeHoles.FileName(0, 0));
    }

    // A tile carries its holes, so everything built from it — the drawn mesh, the collision heightfield —
    // reads the same set of cells rather than each fetching its own.
    [Fact]
    public void AHeightmapTileCarriesItsHoles()
    {
        using var dir = new TempDir();
        dir.Write("Landscape/Holes/Tile_0_0.bin", File_((5, 6)));
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        string heightmap = dir.Write("Landscape/Heightmaps/Tile_0_0_Source.heightmap",
            new byte[res * res * 2]);

        HeightmapTile tile = HeightmapTile.Read(heightmap, 0, 0);

        Assert.NotNull(tile.Holes);
        Assert.True(tile.Holes!.IsHole(5, 6));
        // And a tile handed a grid directly carries whatever it was given, including nothing.
        Assert.Null(HeightmapTile.FromHeights(0, 0, new float[res, res]).Holes);
    }

    // PEI's own two hole files, byte for byte. The count is the map's, not a fixture's: six cells under
    // the LandscapeHoleVolume at (-773.7, 54.9, -770.0) and seven under the other one.
    [RealDataTheory(Map = "PEI")]
    [InlineData(-1, -1, 6)]
    [InlineData(0, 0, 7)]
    public void ReadsPeisOwnHoles(int tileX, int tileY, int expected)
    {
        var level = new LevelInfo(GameData.Map("PEI")!);
        LandscapeHoles? holes = LandscapeHoles.TryRead(level.HolePath(tileX, tileY), tileX, tileY);

        Assert.NotNull(holes);
        Assert.Equal(1, holes!.Version);
        Assert.Equal(expected, holes.HoleCount);
    }

    // The one on tile (-1, -1) is where the map's LandscapeHoleVolume is, which is the check that the
    // transposition is right way round: the volume is authored in world space and the bits are not.
    [RealDataFact(Map = "PEI")]
    public void PeisHolesLandUnderItsHoleVolume()
    {
        var level = new LevelInfo(GameData.Map("PEI")!);
        LandscapeHoles holes = LandscapeHoles.TryRead(level.HolePath(-1, -1), -1, -1)!;

        // The volume sits at (-773.7, 54.9, -770.0) with a 3.9 x 5.6 x 4.2 box, so the cells it cut are
        // the ones covering roughly x in [-775.6, -771.8], z in [-772.1, -767.9].
        Vector2[] centres = TerrainHoleCollision.HoleCentres(holes, -1, -1);
        Assert.NotEmpty(centres);
        foreach (Vector2 centre in centres)
        {
            Assert.InRange(centre.X, -780f, -764f);
            Assert.InRange(-centre.Y, -780f, -764f); // Godot Z is Unity's negated
        }
    }
}
