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
        layers.Realise();

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

    // The load is split in two, and this is the split.
    //
    // Load is pure parsing and file IO — bundle decode, cache reads — and the interactive terrain build
    // runs it on the thread pool while the main thread keeps drawing the loading screen. Realise is what
    // turns the pixels it found into ImageTextures, and Image.CreateFromData + ImageTexture.CreateFromImage
    // are RenderingServer operations: main thread only, the same rule ModelLibrary.Realise,
    // TerrainBuilder.FinishTile and TextureRegistry.Apply all keep.
    //
    // Load used to build them itself, so a normal interactive load created eight to thirty ImageTextures
    // on a worker, concurrently with the loading screen's own tween and with the FinishTile calls of
    // later tiles. That reads as an intermittent cold-load crash or a terrain layer that comes out wrong,
    // and it is invisible in the synchronous build because there the same call runs on the main thread.
    //
    // Resolving on a worker and realising here is exactly the shape the interactive build now has.
    [Test]
    public async System.Threading.Tasks.Task TexturesAreBuiltByRealiseOnTheMainThreadAndNotByLoad()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        TerrainLayers layers = await System.Threading.Tasks.Task.Run(
            () => TerrainLayers.Load(install, level));

        // The worker found the pixels...
        Assert.True(layers.TextureCount > 0, "PEI resolved no layer textures at all");
        // ...and created no GPU resource while doing it: not one tile can answer with textures yet.
        Assert.Equal(0, layers.TileCount);
        for (int x = -8; x <= 8; x++)
            for (int y = -8; y <= 8; y++)
                Assert.Null(layers.For(x, y));

        // Back on Godot's synchronisation context, which is the main thread.
        int textured = layers.Realise();

        Assert.True(textured > 0, "PEI realised no textured tiles");
        Assert.Equal(textured, layers.TileCount);
    }

    // Realise is idempotent: the terrain build calls it once, but a second call must not upload every
    // layer a second time, and it must keep answering with the textures the tiles were already given.
    [Test]
    public void RealisingTwiceKeepsTheSameTextures()
    {
        if (!RealMap(out string install, out LevelInfo level))
            return;

        TerrainLayers layers = TerrainLayers.Load(install, level);
        int first = layers.Realise();
        ImageTexture[]? before = FirstPaintedTile(layers);

        Assert.Equal(first, layers.Realise());
        Assert.Same(before, FirstPaintedTile(layers));
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static ImageTexture[]? FirstPaintedTile(TerrainLayers layers)
    {
        for (int x = -8; x <= 8; x++)
            for (int y = -8; y <= 8; y++)
                if (layers.For(x, y) is { } tile)
                    return tile;
        return null;
    }

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
