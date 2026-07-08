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
