using System.Collections.Generic;
using System.Diagnostics;
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
    Node3D Objects,
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
        (Node3D terrain, int tileCount) = BuildTerrain(level);
        terrainSw.Stop();

        var objectsSw = Stopwatch.StartNew();
        (Node3D objects, int placed, int withMesh, int unique) = BuildObjects(level,
            System.IO.Path.Combine(unturnedPath, "Bundles", "Objects"),
            System.IO.Path.Combine(unturnedPath, "Bundles", "Trees"),
            System.IO.Path.Combine(unturnedPath, "Bundles", "core_linux.masterbundle"));
        objectsSw.Stop();

        return new WorldBuildResult(terrain, objects, tileCount, placed, withMesh, unique,
            terrainSw.Elapsed.TotalMilliseconds, objectsSw.Elapsed.TotalMilliseconds);
    }

    private static (Node3D root, int tileCount) BuildTerrain(LevelInfo level)
    {
        var tiles = level.EnumerateTiles();
        GD.Print($"[unturned-godot] Terrain tiles: {tiles.Count}");

        var terrainRoot = new Node3D { Name = "Terrain" };
        foreach (var (x, y) in tiles)
        {
            var tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
            var splat = SplatmapTile.TryRead(level.SplatmapPath(x, y), x, y);
            terrainRoot.AddChild(TerrainBuilder.BuildTile(tile, splat));
        }
        return (terrainRoot, tiles.Count);
    }

    private static (Node3D root, int placed, int withMesh, int unique) BuildObjects(
        LevelInfo level, string objectBundlesDir, string treeBundlesDir, string bundlePath)
    {
        // Trees are Unturned "resources" placed via a separate file; render them alongside objects so
        // the map isn't bare. They share the object transform, asset db and mesh pipeline.
        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        List<PlacedTree> trees = LevelTrees.Load(System.IO.Path.Combine(level.Path, "Terrain", "Trees.dat"));
        foreach (PlacedTree t in trees)
            objects.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, 0, t.Guid));

        ObjectAssetDatabase db = ObjectAssetDatabase.ScanDirectory(objectBundlesDir);
        foreach (ObjectAsset a in ObjectAssetDatabase.ScanDirectory(treeBundlesDir).All)
            db.Add(a);
        GD.Print($"[unturned-godot] Placed objects: {objects.Count} (incl. {trees.Count} trees), " +
            $"asset db entries: {db.Count}");

        if (objects.Count == 0)
            return (new Node3D { Name = "Objects" }, 0, 0, 0);

        string cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        string textureCacheDir = ProjectSettings.GlobalizePath("user://texture_cache");
        string assetsDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(bundlePath)!, "Assets");
        var neededGuids = new HashSet<System.Guid>();
        foreach (PlacedObject o in objects)
            neededGuids.Add(o.Guid);

        // Parse the 1.4 GB bundle once, then reuse the compact per-GUID mesh + texture cache.
        if (ModelLibrary.CachedMeshCount(cacheDir) == 0 && System.IO.File.Exists(bundlePath))
        {
            GD.Print("[unturned-godot] Extracting models + textures from masterbundle (one-time)...");
            int extracted = ModelExtractor.Extract(bundlePath, objectBundlesDir, treeBundlesDir, assetsDir,
                neededGuids, cacheDir, textureCacheDir);
            GD.Print($"[unturned-godot] Extracted {extracted} meshes to cache");
        }

        var meshLibrary = ModelLibrary.Load(cacheDir, textureCacheDir);
        Node3D objectsRoot = ObjectsBuilder.Build(objects, db, meshLibrary, out int withMesh);
        GD.Print($"[unturned-godot] Rendered {withMesh}/{objects.Count} objects with real meshes " +
            $"({meshLibrary.Count} unique)");
        return (objectsRoot, objects.Count, withMesh, meshLibrary.Count);
    }
}
