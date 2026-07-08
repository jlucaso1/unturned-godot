using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// End-to-end compatibility checks against a real Unturned install. These self-skip (return early)
// when the game is not present, so they never fail on machines without the content.
public class RealDataTests
{
    private static string? UnturnedPath()
    {
        string env = System.Environment.GetEnvironmentVariable("UNTURNED_PATH") ?? "";
        if (env.Length > 0 && Directory.Exists(env)) return env;
        string def = "/home/jlucaso/.local/share/Steam/steamapps/common/Unturned";
        return Directory.Exists(def) ? def : null;
    }

    private static LevelInfo? Pei()
    {
        string? root = UnturnedPath();
        if (root == null) return null;
        var level = new LevelInfo(Path.Combine(root, "Maps", "PEI"));
        return Directory.Exists(level.HeightmapsDir) ? level : null;
    }

    [Fact]
    public void RealHeightmaps_ParseToExpectedRange()
    {
        LevelInfo? level = Pei();
        if (level == null) return;

        var tiles = level.EnumerateTiles();
        Assert.NotEmpty(tiles);

        var (x, y) = tiles[0];
        HeightmapTile tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
        // Normalized heights are always within [0,1] when read with the correct endianness.
        Assert.InRange(tile.Heights[0, 0], 0f, 1f);
        Assert.InRange(tile.Heights[128, 128], 0f, 1f);
    }

    [Fact]
    public void RealLighting_ParsesPeiMiddayValues()
    {
        string? root = UnturnedPath();
        if (root == null) return;

        string path = Path.Combine(root, "Maps", "PEI", "Environment", "Lighting.dat");
        LevelLighting? lighting = LevelLighting.Load(path);
        if (lighting == null) return;

        Assert.Equal(12, lighting.Version);
        Assert.Equal(4, lighting.Times.Count);

        // PEI's shipped sea level places the ocean surface at 0.1 * Level.TERRAIN (256) = ~25.6m.
        Assert.Equal(0.1f, lighting.SeaLevel, 0.001f);

        // PEI's shipped midday: full-strength warm sunlight, a bright warm ambient and a blue sea.
        LightingKeyframe day = lighting.Midday;
        Assert.Equal(1f, day.Intensity);
        Assert.Equal(1f, day.Shadows);
        Assert.True(day.Sea.B > day.Sea.R); // water is bluer than it is red
        Assert.Equal(0.933f, day.Sun.R, 0.01f);
        Assert.Equal(0.863f, day.Sun.G, 0.01f);
        Assert.Equal(0.757f, day.Sun.B, 0.01f);
        Assert.Equal(0.8f, day.AmbientSky.R, 0.01f);
        Assert.InRange(day.AmbientEquator.R, 0.5f, 1f);
    }

    [Fact]
    public void RealObjects_ParseAndResolveAgainstBundles()
    {
        LevelInfo? level = Pei();
        if (level == null) return;

        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        Assert.NotEmpty(objects);

        string bundles = Path.Combine(UnturnedPath()!, "Bundles", "Objects");
        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(bundles);
        Assert.True(db.Count > 0);

        // Every placement in a shipped map must resolve to a known asset (GUID or legacy id).
        int resolved = 0;
        foreach (PlacedObject o in objects)
            if (db.Resolve(o.Guid, o.Id) != null) resolved++;

        Assert.Equal(objects.Count, resolved);
    }
}
