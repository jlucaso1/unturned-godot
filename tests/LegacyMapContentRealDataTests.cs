using System;
using System.Collections.Generic;
using System.IO;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using UnturnedGodot.Unity;
using Xunit;

namespace UnturnedGodot.Tests;

// The pre-2018 maps, against the real content. Their per-map bundles and their Trees.dat were written by
// older Unity and Unturned versions than PEI's, and every one of these assertions failed before the readers
// learned those formats: the roads drew with the procedural fallback shader and the maps had no trees.
//
// Each fact self-skips when its map is not installed (`./scripts/fetch-game-data.sh --maps all` gets them
// all); the sweep at the bottom runs against whatever is.
[Trait("Category", "RealData")]
public class LegacyMapContentRealDataTests
{
    private static string RoadsBundle(string map) =>
        Path.Combine(GameData.Map(map)!, "Environment", "Roads.unity3d");

    private static string TreesDat(string map) =>
        Path.Combine(GameData.Map(map)!, "Terrain", "Trees.dat");

    // Every Texture2D the bundle holds, decoded the way RoadsBuilder and TerrainTextures decode them.
    private static List<UnityTexture> Textures(string bundlePath)
    {
        MapBundle bundle = MapBundle.ReadFile(bundlePath)!;
        Assert.NotNull(bundle);

        var textures = new List<UnityTexture>();
        foreach (SerializedObject o in bundle.Objects)
            if (bundle.TryReadTexture(o, out UnityTexture tex, out byte[] pixels))
            {
                Assert.NotEmpty(pixels);
                textures.Add(tex);
            }
        return textures;
    }

    [RealDataFact(Map = "Alpha Valley")]
    public void AlphaValley_Unity4RoadBundle_Decodes()
    {
        // SerializedFile format 9, written by Unity 4.5: the recursive type tree, no type hashes, and
        // Texture2D carrying m_MipMap instead of m_MipCount.
        List<UnityTexture> textures = Textures(RoadsBundle("Alpha Valley"));

        Assert.Equal(8, textures.Count);
        UnityTexture trail = textures.Find(t => t.Name == "Trail")!;
        Assert.NotNull(trail);
        Assert.Equal(64, trail.Width);
        Assert.Equal(7, trail.MipCount); // derived from m_MipMap: 64x64 down to 1x1
        Assert.Contains(textures, t => t.Name == "Highway_0");
    }

    [RealDataFact(Map = "Germany")]
    public void Germany_UnityFsRoadBundle_Decodes()
    {
        // A UnityFS container (not UnityRaw) holding a format-17 SerializedFile, with the pixels in a
        // .resS entry beside it rather than inline.
        List<UnityTexture> textures = Textures(RoadsBundle("Germany"));

        Assert.Equal(10, textures.Count);
        Assert.Contains(textures, t => t.Name == "Highway_0");
    }

    [RealDataFact(Map = "Alpha Valley")]
    public void AlphaValley_ReadsItsRegionGriddedTrees()
    {
        List<PlacedTree> trees = LevelTrees.Load(TreesDat("Alpha Valley"));

        Assert.Equal(1981, trees.Count);
        // Version 6 carries the GUID, so these need nothing from the asset database to find their mesh.
        Assert.All(trees, t => Assert.NotEqual(Guid.Empty, t.Guid));
    }

    [RealDataFact(Map = "Monolith", RequiresMasterBundle = true)]
    public void Monolith_ResolvesItsIdOnlyTreesThroughTheAssetDatabase()
    {
        List<PlacedTree> trees = LevelTrees.Load(TreesDat("Monolith"));
        Assert.Equal(1810, trees.Count);
        // Version 5 predates GUIDs entirely: the legacy id is the only identity on the file.
        Assert.All(trees, t => Assert.Equal(Guid.Empty, t.Guid));

        var placements = new List<PlacedObject>();
        foreach (PlacedTree t in trees)
            placements.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, t.Id, t.Guid));

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(ContentSource.Discover(GameData.Install!));
        Assert.Equal(trees.Count, LegacyPlacements.ResolveGuids(placements, db));
        Assert.All(placements, p => Assert.NotEqual(Guid.Empty, p.Guid));
    }

    [RealDataFact(RequiresMasterBundle = true)]
    public void HolidayVariant_KeepsItsPrefabBehindABundleOverride()
    {
        // The Christmas ornament PEI, Washington and Yukon each place is a Resource that ships no prefab of
        // its own: it points at the base tree's. ModelExtractor derives a prefab key per category, and while
        // the tree branch came first that override was never consulted — the ornament had no mesh anywhere.
        ObjectAssetDatabase db = ContentExtraction.ScanAssets(ContentSource.Discover(GameData.Install!));
        ObjectAsset ornament = db.ResolveByGuid(new Guid("f0707c1712804e6fbe1a7d925cb33ca4"))!;

        Assert.NotNull(ornament);
        Assert.Equal("/Trees/Ornament_0", ornament.BundleOverridePath);
        Assert.False(GameData.Prefabs.PartsByKey.ContainsKey("trees/ornament_0_xmas")); // its own folder
        Assert.True(GameData.Prefabs.PartsByKey.ContainsKey("trees/ornament_0"));       // the override target
    }

    [RealDataFact]
    public void EveryInstalledMap_RoadAndTerrainBundlesDecode()
    {
        var checkedBundles = 0;
        foreach (MapEntry map in MapCatalog.Scan(GameData.Install!))
        {
            foreach (string relative in new[]
                     {
                         Path.Combine("Environment", "Roads.unity3d"),
                         Path.Combine("Terrain", "Materials.unity3d"),
                         Path.Combine("Terrain", "Details.unity3d"),
                     })
            {
                string path = Path.Combine(map.Path, relative);
                if (!File.Exists(path))
                    continue;

                // Not Assert.NotEmpty on the textures: what is being pinned is that no map's bundle throws
                // an unsupported-format exception on the way in, which is what sent these to the fallback.
                Assert.All(Textures(path), t => Assert.NotEqual(0, t.Width));
                checkedBundles++;
            }
        }

        Assert.True(checkedBundles > 0, "an install with no map bundles at all is not a real-data run");
    }
}
