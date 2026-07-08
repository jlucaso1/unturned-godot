using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;

namespace UnturnedGodot;

// Builds the map's world (terrain + objects) from an Unturned install. Extracted from Main so both the
// normal run and the benchmark harness exercise exactly the same build path — no duplicated logic to
// drift. Roots are returned detached; the caller decides whether to add them to the tree.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed record WorldBuildResult(
    Node3D Terrain,
    HeightmapSampler Heights,
    Node3D Objects,
    Node3D Foliage,
    int TileCount,
    int PlacedObjectCount,
    int ObjectsWithMesh,
    int UniqueMeshCount,
    double TerrainMs,
    double ObjectsMs);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class WorldBuilder
{
    public static WorldBuildResult Build(string unturnedPath, string mapName)
    {
        string mapPath = System.IO.Path.Combine(unturnedPath, "Maps", mapName);
        var level = new LevelInfo(mapPath);
        GD.Print($"[unturned-godot] Loading map {level.Name} at {level.Path}");

        var terrainSw = Stopwatch.StartNew();
        (Node3D terrain, int tileCount, HeightmapSampler heights) = BuildTerrain(level);
        terrainSw.Stop();

        var objectsSw = Stopwatch.StartNew();
        (Node3D objects, Node3D foliage, int placed, int withMesh, int unique) = BuildObjects(level,
            System.IO.Path.Combine(unturnedPath, "Bundles", "Objects"),
            System.IO.Path.Combine(unturnedPath, "Bundles", "Trees"),
            System.IO.Path.Combine(unturnedPath, "Bundles", "core_linux.masterbundle"));
        objectsSw.Stop();

        return new WorldBuildResult(terrain, heights, objects, foliage, tileCount, placed, withMesh, unique,
            terrainSw.Elapsed.TotalMilliseconds, objectsSw.Elapsed.TotalMilliseconds);
    }

    public static (Node3D root, int tileCount, HeightmapSampler heights) BuildTerrain(LevelInfo level)
    {
        var tiles = level.EnumerateTiles();
        GD.Print($"[unturned-godot] Terrain tiles: {tiles.Count}");
        var heightTiles = new HeightmapTile[tiles.Count]; // kept for the height sampler (roads conform to it)

        // Real per-layer terrain textures (Materials.unity3d), blended by the splatmap; null if the bundle
        // or a layer texture is missing, in which case tiles fall back to averaged layer colors.
        var textures = TerrainTextures.Load(System.IO.Path.Combine(level.Path, "Terrain"));
        ImageTexture[]? layers = TerrainBuilder.MapLayerTextures(textures);
        GD.Print($"[unturned-godot] Terrain textures: {textures.Count} loaded, " +
            (layers != null ? "8 layers textured" : "flat-color fallback"));

        // Build the tiles' geometry + meshoptimizer LODs on worker threads (the LOD generation dominates
        // terrain build time and is pure CPU/meshopt on data-only ImporterMeshes), then realise the meshes
        // and attach materials on this (main) thread — the only steps that touch the RenderingServer.
        bool textured = layers != null;
        var meshes = new TerrainBuilder.TileMesh[tiles.Count];
        System.Threading.Tasks.Parallel.For(0, tiles.Count, i =>
        {
            (int x, int y) = tiles[i];
            HeightmapTile tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
            heightTiles[i] = tile;
            SplatmapTile? splat = SplatmapTile.TryRead(level.SplatmapPath(x, y), x, y);
            meshes[i] = TerrainBuilder.BuildTileMesh(tile, splat, textured && splat != null);
        });

        var terrainRoot = new Node3D { Name = "Terrain" };
        foreach (TerrainBuilder.TileMesh tm in meshes)
            terrainRoot.AddChild(TerrainBuilder.FinishTile(tm, layers));
        return (terrainRoot, tiles.Count, new HeightmapSampler(heightTiles));
    }

    private static (Node3D root, Node3D foliage, int placed, int withMesh, int unique) BuildObjects(
        LevelInfo level, string objectBundlesDir, string treeBundlesDir, string bundlePath)
    {
        // Trees are Unturned "resources" placed via a separate file; render them alongside objects so
        // the map isn't bare. They share the object transform, asset db and mesh pipeline.
        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        List<PlacedTree> trees = LevelTrees.Load(System.IO.Path.Combine(level.Path, "Terrain", "Trees.dat"));
        foreach (PlacedTree t in trees)
            objects.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, 0, t.Guid));

        // Scan the object/tree asset DB on a worker thread: on the warm path db is only consumed after the
        // main-thread ModelLibrary.Load below (for fallback-box coloring), so the scan overlaps that load and
        // is effectively free. The cold extraction branch resolves it explicitly before use.
        var dbTask = System.Threading.Tasks.Task.Run(() =>
        {
            ObjectAssetDatabase scanned = ObjectAssetDatabase.ScanDirectory(objectBundlesDir);
            foreach (ObjectAsset a in ObjectAssetDatabase.ScanDirectory(treeBundlesDir).All)
                scanned.Add(a);
            return scanned;
        });
        GD.Print($"[unturned-godot] Placed objects: {objects.Count} (incl. {trees.Count} trees)");

        string cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        string textureCacheDir = ProjectSettings.GlobalizePath("user://texture_cache");
        string assetsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(bundlePath)!, "Assets");

        // Resolve the foliage types the map's Foliage.blob uses, so their meshes are extracted too.
        LevelFoliage? foliageData = LevelFoliage.Load(System.IO.Path.Combine(level.Path, "Foliage.blob"));
        var foliageAssets = foliageData != null
            ? FoliageAsset.ScanForGuids(assetsDir, new HashSet<Guid>(foliageData.AssetGuids))
            : new Dictionary<Guid, FoliageAsset>();

        var neededGuids = new HashSet<Guid>();
        foreach (PlacedObject o in objects)
            neededGuids.Add(o.Guid);

        // Parse the 1.4 GB bundle once, then reuse the compact per-GUID mesh + texture cache. This is the
        // synchronous full build (used by the benchmark and the warm path); the interactive cold load
        // streams the two phases separately via ObjectStreamer.
        bool foliageMissing = foliageAssets.Keys.Any(
            g => !System.IO.File.Exists(System.IO.Path.Combine(cacheDir, g.ToString("N") + ".mesh")));
        if ((ModelLibrary.CachedMeshCount(cacheDir) == 0 || foliageMissing) && System.IO.File.Exists(bundlePath))
        {
            GD.Print("[unturned-godot] Extracting models + textures from masterbundle (one-time)...");
            int extracted = ModelExtractor.ExtractMeshes(bundlePath, objectBundlesDir, treeBundlesDir,
                assetsDir, neededGuids, cacheDir, dbTask.Result, foliageAssets.Values.ToList());
            ModelExtractor.ExtractTextures(bundlePath, cacheDir, textureCacheDir);
            GD.Print($"[unturned-godot] Extracted {extracted} meshes to cache");
        }

        var registry = new TextureRegistry(textureCacheDir);
        var meshLibrary = ModelLibrary.Load(cacheDir, registry);

        ObjectAssetDatabase db = dbTask.Result; // the scan ran concurrently with ModelLibrary.Load above
        int withMesh = 0;
        Node3D objectsRoot = objects.Count > 0
            ? ObjectsBuilder.Build(objects, db, meshLibrary, out withMesh)
            : new Node3D { Name = "Objects" };

        Node3D foliageRoot = FoliageBuilder.Build(foliageData, meshLibrary);
        registry.ApplyAllAvailable();
        GD.Print($"[unturned-godot] Rendered {withMesh}/{objects.Count} objects with real meshes " +
            $"({meshLibrary.Count} unique)");
        return (objectsRoot, foliageRoot, objects.Count, withMesh, meshLibrary.Count);
    }
}
