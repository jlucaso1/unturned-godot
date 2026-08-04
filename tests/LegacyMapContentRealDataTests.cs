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
// [RealDataFact] turns "no install at all" into a visible skip, but the maps below are beyond what the
// real-data job fetches (PEI), and that job sets UG_REQUIRE_REAL_DATA=1 to turn skips off — so each fact
// checks for its own map and returns, the same way the California2 test in BakedNavGraphTests does.
// `./scripts/fetch-game-data.sh --maps all` is what makes them run. The sweep at the bottom needs no such
// guard: it asserts over whatever maps are installed, PEI included.
[Trait("Category", "RealData")]
public class LegacyMapContentRealDataTests
{
    private static string? RoadsBundle(string map) =>
        GameData.Map(map) is { } path ? Path.Combine(path, "Environment", "Roads.unity3d") : null;

    private static string? TreesDat(string map) =>
        GameData.Map(map) is { } path ? Path.Combine(path, "Terrain", "Trees.dat") : null;

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

    [RealDataFact]
    public void AlphaValley_Unity4RoadBundle_Decodes()
    {
        if (RoadsBundle("Alpha Valley") is not { } bundlePath)
            return;

        // SerializedFile format 9, written by Unity 4.5: the recursive type tree, no type hashes, and
        // Texture2D carrying m_MipMap instead of m_MipCount.
        List<UnityTexture> textures = Textures(bundlePath);

        Assert.Equal(8, textures.Count);
        UnityTexture trail = textures.Find(t => t.Name == "Trail")!;
        Assert.NotNull(trail);
        Assert.Equal(64, trail.Width);
        Assert.Equal(7, trail.MipCount); // derived from m_MipMap: 64x64 down to 1x1
        Assert.Contains(textures, t => t.Name == "Highway_0");
    }

    [RealDataFact]
    public void Germany_UnityFsRoadBundle_Decodes()
    {
        if (RoadsBundle("Germany") is not { } bundlePath)
            return;

        // A UnityFS container (not UnityRaw) holding a format-17 SerializedFile, with the pixels in a
        // .resS entry beside it rather than inline.
        List<UnityTexture> textures = Textures(bundlePath);

        Assert.Equal(10, textures.Count);
        Assert.Contains(textures, t => t.Name == "Highway_0");
    }

    [RealDataFact]
    public void AlphaValley_ReadsItsRegionGriddedTrees()
    {
        if (TreesDat("Alpha Valley") is not { } treesPath)
            return;

        List<PlacedTree> trees = LevelTrees.Load(treesPath);

        Assert.Equal(1981, trees.Count);
        // Version 6 carries the GUID, so these need nothing from the asset database to find their mesh.
        Assert.All(trees, t => Assert.NotEqual(Guid.Empty, t.Guid));
    }

    [RealDataFact]
    public void Monolith_ResolvesItsIdOnlyTreesThroughTheAssetDatabase()
    {
        if (TreesDat("Monolith") is not { } treesPath)
            return;

        List<PlacedTree> trees = LevelTrees.Load(treesPath);
        Assert.Equal(1810, trees.Count);
        // Version 5 predates GUIDs entirely: the legacy id is the only identity on the file.
        Assert.All(trees, t => Assert.Equal(Guid.Empty, t.Guid));

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(ContentSource.Discover(GameData.Install!));
        var placements = new List<PlacedObject>();
        Assert.Equal(trees.Count, LegacyPlacements.AppendTrees(trees, placements, db));
        Assert.All(placements, p => Assert.NotEqual(Guid.Empty, p.Guid));

        // Every one of Monolith's eight tree ids also names an object, and the merged database indexes the
        // objects first — so resolving through the object namespace would silently plant a house here.
        foreach (PlacedObject placement in placements)
        {
            Assert.Equal(EObjectType.Resource, db.ResolveByGuid(placement.Guid)!.Type);
            Assert.NotEqual(db.ResolveById(placement.Id)?.Guid, placement.Guid);
        }
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

    [RealDataFact(RequiresMasterBundle = true)]
    public void EveryDecalAsset_HasItsTextureUnderItsOwnPrefabKey()
    {
        // A Decal has no prefab, so nothing in the mesh pipeline could ever produce one and every decal
        // the maps place rendered as a placeholder box. What it does have is a decal.png filed under its
        // own key in the bundle, which is what the extractor now builds its quad around.
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(GameData.Install!);
        ObjectAssetDatabase db = ContentExtraction.ScanAssets(sources);
        PrefabGraph prefabs = GameData.Prefabs;

        // Core-owned decals only: a subscribed workshop item may ship its own, and its texture lives in
        // its own bundle rather than the core prefab graph this checks against.
        var decals = 0;
        foreach (ObjectAsset asset in db.All)
        {
            if (asset.Type != EObjectType.Decal || !sources[0].Owns(asset.Directory))
                continue;

            decals++;
            string key = PrefabKey.For(asset, sources[0]);
            Assert.True(prefabs.ContainerByPath.ContainsKey(prefabs.AssetPrefix + key + "/decal.png"),
                $"{asset.Directory} has no decal.png at {key}");
            Assert.True(asset.DecalX > 0f && asset.DecalY > 0f, $"{asset.Directory} has no decal size");
            Assert.False(prefabs.PartsByKey.ContainsKey(key), $"{asset.Directory} unexpectedly has a prefab");
        }

        Assert.Equal(14, decals); // the game's own graffiti, faction tags and the Isaac memorial
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
