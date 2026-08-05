using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// The eight layer textures each terrain tile is painted from.
//
// A tile names its materials in Level.hierarchy, the materials live in the game's Landscapes assets —
// and also in any workshop mod that ships its own terrain art, which is why the scan walks every content
// source rather than the game's folder. A tile whose materials cannot be resolved gets NO layers at all
// and the caller falls back to a flat averaged colour, which is why "null" is a supported answer here
// rather than a failure: a map installed without the mod that defines its terrain still loads, in the
// wrong colours instead of not at all.
public class TerrainLayersTests : TestClass
{
    public TerrainLayersTests(Node testScene) : base(testScene) { }

    // A level with no hierarchy to read has no tiles, and nothing throws. The caller is inside the
    // terrain stage of a load, with a loading screen up.
    [Test]
    public void ALevelWithNoHierarchyHasNoTiles()
    {
        TerrainLayers layers = TerrainLayers.Load("/nonexistent-unturned",
            new LevelInfo("/nonexistent-unturned/Maps/None"));

        Assert.Equal(0, layers.TileCount);
        Assert.Equal(0, layers.TextureCount);
        Assert.Null(layers.For(0, 0));
    }

    // A tile nobody painted answers null rather than an empty array, and the difference is what the
    // caller switches on: null means "fall back to the averaged colour", an empty array would mean
    // "paint this tile with nothing".
    [Test]
    public void AnUnknownTileAnswersNullRatherThanNoLayers()
    {
        TerrainLayers layers = TerrainLayers.Load("/nonexistent-unturned",
            new LevelInfo("/nonexistent-unturned/Maps/None"));

        Assert.Null(layers.For(-999, 999));
    }

    // PEI's own terrain, resolved end to end: its hierarchy, the game's landscape materials, and the
    // textures those name out of the bundles they ship in.
    [Test]
    public void TheRealMapResolvesItsTerrainLayers()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        TerrainLayers layers = TerrainLayers.Load(install, level);

        Assert.True(layers.TileCount > 0, "PEI resolved no terrain tiles at all");
        Assert.True(layers.TextureCount > 0, "PEI resolved tiles but not one layer texture");

        // Every tile it claims to know hands back real textures, rather than an entry that resolves to
        // nothing — a tile in the dictionary is a tile the terrain build will paint from.
        int painted = 0;
        for (int x = -8; x <= 8 && painted == 0; x++)
            for (int y = -8; y <= 8; y++)
                if (layers.For(x, y) is { } tile)
                {
                    Assert.NotEmpty(tile);
                    foreach (ImageTexture texture in tile)
                        Assert.True(texture.GetWidth() > 0 && texture.GetHeight() > 0,
                            $"tile ({x},{y}) carries a {texture.GetWidth()}x{texture.GetHeight()} layer");
                    painted++;
                    break;
                }

        Assert.True(painted > 0, $"PEI reported {layers.TileCount} tiles but none were found by coordinate");
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static bool RealMap(out string install, out LevelInfo level)
    {
        install = "";
        level = null!;
        string? found = Assets.UnturnedInstall.Find();
        string? maps = found == null ? null : System.IO.Path.Combine(found, "Maps", "PEI");
        if (found == null || maps == null || !System.IO.Directory.Exists(maps))
        {
            if (System.Environment.GetEnvironmentVariable("UG_REQUIRE_REAL_DATA") == "1")
            {
                throw new System.IO.IOException(
                    "UG_REQUIRE_REAL_DATA=1 but the PEI map is not present; this run exists to prove these "
                    + "tests execute");
            }

            Log.Print("[runtime-tests] skipping: no PEI map "
                + "(set UNTURNED_PATH or run ./scripts/fetch-game-data.sh)");
            return false;
        }

        install = found;
        level = new LevelInfo(maps);
        return true;
    }
}
