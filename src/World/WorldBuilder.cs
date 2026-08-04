using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

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
    Node3D Vehicles,
    int TileCount,
    int PlacedObjectCount,
    int ObjectsWithMesh,
    int UniqueMeshCount,
    double TerrainMs,
    double ObjectsMs);

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class WorldBuilder
{
    // Conservative terrain occluders substantially reduce hidden object submission on hilly maps. Their
    // triangles are proven to remain below the source heightfield, so enable them by default; zero keeps
    // the uncullled path available for visual/performance A/B checks.
    private static bool OccludersEnabled => EnvFlag.IsOn(OS.GetEnvironment("TERRAIN_OCCLUDERS"), whenUnset: true);

    public static WorldBuildResult Build(string unturnedPath, string mapName)
    {
        var level = new LevelInfo(MapCatalog.ResolvePath(unturnedPath, mapName));
        Log.Print($"[unturned-godot] Loading map {level.Name} at {level.Path}");

        var terrainSw = Stopwatch.StartNew();
        (Node3D terrain, int tileCount, HeightmapSampler heights) = BuildTerrain(unturnedPath, level);
        terrainSw.Stop();

        var objectsSw = Stopwatch.StartNew();
        (Node3D objects, Node3D foliage, Node3D vehicles, int placed, int withMesh, int unique) =
            BuildObjects(level, unturnedPath);
        objectsSw.Stop();

        return new WorldBuildResult(terrain, heights, objects, foliage, vehicles, tileCount, placed,
            withMesh, unique, terrainSw.Elapsed.TotalMilliseconds, objectsSw.Elapsed.TotalMilliseconds);
    }

    // Interactive-load variant of BuildTerrain: the pure-CPU tile/LOD generation runs on the thread pool
    // (the main thread keeps rendering the loading screen), and the RenderingServer-touching FinishTile
    // calls yield to the render loop every few tiles so no single frame swallows all 16.
    public static async System.Threading.Tasks.Task<(Node3D root, int tileCount, HeightmapSampler heights)>
        BuildTerrainAsync(string unturnedPath, LevelInfo level, Node yieldOn,
            System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, UnturnedGodot.Unity.CachedTexture>>? sharedLayers = null)
    {
        var tiles = level.EnumerateTiles();
        Log.Print($"[unturned-godot] Terrain tiles: {tiles.Count}");
        var heightTiles = new HeightmapTile[tiles.Count];

        // Layer resolve + tile/LOD generation all happen off the main thread; only FinishTile below
        // touches the RenderingServer. Semantics match the sync variant exactly.
        TerrainLayers layers = await System.Threading.Tasks.Task.Run(() =>
            LoadLayers(unturnedPath, level, sharedLayers));

        var meshes = new TerrainBuilder.TileMesh[tiles.Count];
        TerrainOccluder.Prepared[]? occluders = OccludersEnabled
            ? new TerrainOccluder.Prepared[tiles.Count] : null;
        await System.Threading.Tasks.Task.Run(() => System.Threading.Tasks.Parallel.For(0, tiles.Count, i =>
        {
            (int x, int y) = tiles[i];
            HeightmapTile tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
            heightTiles[i] = tile;
            SplatmapTile? splat = SplatmapTile.TryRead(level.SplatmapPath(x, y), x, y);
            meshes[i] = TerrainBuilder.BuildTileMesh(tile, splat, layers.For(x, y) != null && splat != null);
            if (occluders != null)
                occluders[i] = TerrainOccluder.Prepare(meshes[i]);
        }));

        // Yield on a time budget, not every N tiles: waiting for a frame costs more than finishing the
        // tile that would have shared it, and a 52-tile map was spending most of this phase waiting.
        var terrainRoot = new Node3D { Name = "Terrain" };
        var controlTextures = new TerrainBuilder.ControlTextureCache();
        long budget = (long)(Stopwatch.Frequency * 0.008);
        long until = Stopwatch.GetTimestamp() + budget;
        for (int i = 0; i < meshes.Length; i++)
        {
            TerrainBuilder.TileMesh tm = meshes[i];
            terrainRoot.AddChild(TerrainBuilder.FinishTile(tm, layers.For(tm.X, tm.Y), controlTextures));
            if (occluders != null)
                terrainRoot.AddChild(TerrainOccluder.Finish(occluders[i]));
            if (Stopwatch.GetTimestamp() >= until)
            {
                await yieldOn.ToSignal(yieldOn.GetTree(), SceneTree.SignalName.ProcessFrame);
                until = Stopwatch.GetTimestamp() + budget;
            }
        }
        return (terrainRoot, tiles.Count, new HeightmapSampler(heightTiles));
    }

    public static (Node3D root, int tileCount, HeightmapSampler heights) BuildTerrain(
        string unturnedPath, LevelInfo level)
    {
        var tiles = level.EnumerateTiles();
        Log.Print($"[unturned-godot] Terrain tiles: {tiles.Count}");
        var heightTiles = new HeightmapTile[tiles.Count]; // kept for the height sampler (roads conform to it)

        // The map's own per-tile splat layer textures; tiles without a full set fall back to averaged
        // layer colors.
        TerrainLayers layers = LoadLayers(unturnedPath, level);

        // Build the tiles' geometry + meshoptimizer LODs on worker threads (the LOD generation dominates
        // terrain build time and is pure CPU/meshopt on data-only ImporterMeshes), then realise the meshes
        // and attach materials on this (main) thread — the only steps that touch the RenderingServer.
        var meshes = new TerrainBuilder.TileMesh[tiles.Count];
        TerrainOccluder.Prepared[]? occluders = OccludersEnabled
            ? new TerrainOccluder.Prepared[tiles.Count] : null;
        System.Threading.Tasks.Parallel.For(0, tiles.Count, i =>
        {
            (int x, int y) = tiles[i];
            HeightmapTile tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
            heightTiles[i] = tile;
            SplatmapTile? splat = SplatmapTile.TryRead(level.SplatmapPath(x, y), x, y);
            meshes[i] = TerrainBuilder.BuildTileMesh(tile, splat, layers.For(x, y) != null && splat != null);
            if (occluders != null)
                occluders[i] = TerrainOccluder.Prepare(meshes[i]);
        });

        var terrainRoot = new Node3D { Name = "Terrain" };
        var controlTextures = new TerrainBuilder.ControlTextureCache();
        for (int i = 0; i < meshes.Length; i++)
        {
            TerrainBuilder.TileMesh tm = meshes[i];
            terrainRoot.AddChild(TerrainBuilder.FinishTile(tm, layers.For(tm.X, tm.Y), controlTextures));
            if (occluders != null)
                terrainRoot.AddChild(TerrainOccluder.Finish(occluders[i]));
        }
        return (terrainRoot, tiles.Count, new HeightmapSampler(heightTiles));
    }

    // Resolves the map's per-tile splat layers and reports what came back, so a map that renders flat is
    // traceable to either "no materials in the hierarchy" or "textures could not be extracted".
    private static TerrainLayers LoadLayers(string unturnedPath, LevelInfo level,
        System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, UnturnedGodot.Unity.CachedTexture>>? sharedLayers = null)
    {
        TerrainLayers layers = TerrainLayers.Load(unturnedPath, level, sharedLayers);
        Log.Print($"[unturned-godot] Terrain layers: {layers.TextureCount} textures, "
            + (layers.TileCount > 0 ? $"{layers.TileCount} tiles textured" : "flat-color fallback"));
        return layers;
    }

    private static (Node3D root, Node3D foliage, Node3D vehicles, int placed, int withMesh, int unique)
        BuildObjects(LevelInfo level, string unturnedPath)
    {
        // The game's bundle plus any workshop mod that ships one: a workshop map places objects whose
        // prefabs live in the mod's bundle, not the game's.
        System.Collections.Generic.IReadOnlyList<ContentSource> sources = ContentSource.Discover(unturnedPath);
        // Trees are Unturned "resources" placed via a separate file; render them alongside objects so
        // the map isn't bare. They share the object transform, asset db and mesh pipeline.
        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        List<PlacedTree> trees = LevelTrees.Load(System.IO.Path.Combine(level.Path, "Terrain", "Trees.dat"));
        foreach (PlacedTree t in trees)
            objects.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, t.Id, t.Guid));

        // Scan the object/tree asset DB on a worker thread: on the warm path db is only consumed after the
        // main-thread ModelLibrary.Load below (for fallback-box coloring), so the scan overlaps that load and
        // is effectively free. The cold extraction branch resolves it explicitly before use.
        var dbTask = System.Threading.Tasks.Task.Run(() => ContentExtraction.ScanAssets(sources));
        Log.Print($"[unturned-godot] Placed objects: {objects.Count} (incl. {trees.Count} trees)");

        string cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        string textureCacheDir = ProjectSettings.GlobalizePath("user://texture_cache");

        // Resolve the foliage types the map's Foliage.blob uses, so their meshes are extracted too. Scanned
        // across every source: a workshop map's foliage assets sit next to its own bundle, not the game's.
        string foliagePath = System.IO.Path.Combine(level.Path, "Foliage.blob");
        LevelFoliageChunks? foliageData = null;
        FoliageResidencyIndex? foliageIndex = null;
        if (FoliageBuilder.SpatialResidencyEnabled && System.IO.File.Exists(foliagePath))
        {
            string indexDirectory = ProjectSettings.GlobalizePath("user://foliage_index");
            string indexPath = System.IO.Path.Combine(indexDirectory,
                FoliageResidencyIndex.CacheFileName(foliagePath));
            foliageIndex = FoliageResidencyIndex.LoadOrBuild(foliagePath, indexPath,
                FoliageBuilder.RuntimeChunkTiles, out _);
            if (foliageIndex == null)
                foliageData = LevelFoliageChunks.Load(foliagePath, FoliageBuilder.RuntimeChunkTiles);
        }
        else
        {
            foliageData = LevelFoliageChunks.Load(foliagePath, FoliageBuilder.RuntimeChunkTiles);
        }
        IReadOnlyList<Guid>? foliageGuids = foliageIndex?.AssetGuids ?? foliageData?.AssetGuids;
        var foliageAssets = foliageGuids != null
            ? FoliageAsset.ScanSources(sources, new HashSet<Guid>(foliageGuids))
            : new Dictionary<Guid, FoliageAsset.Owned>();

        // The vehicles the map starts with, rolled from its own tables. A spawned vehicle is a GUID and a
        // transform like any other placement, so its mesh joins the same needed set, the same extraction
        // plan and the same batching — only the scene root is its own.
        List<PlacedObject> vehicles = VehicleSpawnPlan.Load(level, sources, dbTask.Result);
        Log.Print($"[unturned-godot] Vehicles: {vehicles.Count} spawned");

        // A pre-GUID map names its objects and trees by legacy id; the needed set below is keyed on GUIDs,
        // so those placements borrow theirs from the asset database first.
        int legacy = LegacyPlacements.ResolveGuids(objects, dbTask.Result);
        if (legacy > 0)
            Log.Print($"[unturned-godot] Legacy placements resolved by id: {legacy}");

        var neededGuids = new HashSet<Guid>();
        foreach (PlacedObject o in objects)
            neededGuids.Add(o.Guid);
        foreach (PlacedObject v in vehicles)
            neededGuids.Add(v.Guid);
        foreach (Guid g in foliageAssets.Keys) // resolved foliage types only (see ObjectStreamer)
            neededGuids.Add(g);

        // Parse the 1.4 GB bundle once, then reuse the compact per-GUID mesh + texture cache. This is the
        // synchronous full build (used by the benchmark and the warm path); the interactive cold load
        // streams the two phases separately via ObjectStreamer. Extraction is driven by what THIS map is
        // missing from the (map-agnostic, GUID-keyed) cache, not by whether the cache has any files at all.
        System.Collections.Generic.List<ContentExtraction.BundlePlan> plans = ContentExtraction.Plan(
            sources, cacheDir, textureCacheDir, neededGuids, dbTask.Result, foliageAssets);
        foreach (string report in ContentExtraction.PendingReports(plans))
            Log.Print(report);
        foreach (ContentExtraction.BundlePlan plan in plans)
        {
            if (!plan.NeedsExtraction)
                continue;

            // One unreadable bundle (an old or damaged mod) must not cost the whole map: its objects
            // fall back to boxes and everything else still builds.
            try
            {
                int extracted = ModelExtractor.ExtractMeshes(plan.Source, plan.Needed, cacheDir,
                    dbTask.Result, plan.Foliage);
                ModelExtractor.ExtractTextures(plan.Source.BundlePath, plan.Source.CacheTag, cacheDir,
                    textureCacheDir);
                Log.Print($"[unturned-godot] Extracted {extracted} meshes from {plan.Source.Name}");
            }
            catch (Exception e)
            {
                Log.PrintErr($"[unturned-godot] {plan.Source.Name} could not be extracted "
                    + $"({e.GetType().Name}: {e.Message}); its objects will render as boxes.");
            }
        }

        var registry = new TextureRegistry(textureCacheDir);
        // One material table for both levels, as the streamed path uses: a prefab's lower level draws with
        // the same materials as its base one, so a second table just builds every one of them again.
        var materials = new MaterialTable();
        var meshLibrary = ModelLibrary.Load(cacheDir, registry, neededGuids, sharedMaterials: materials);
        var colliderLibrary = ColliderLibrary.Load(cacheDir, neededGuids);

        ObjectAssetDatabase db = dbTask.Result; // the scan ran concurrently with ModelLibrary.Load above
        int withMesh = 0;
        // The prefabs' authored lower levels, so this path renders the same as the streamed one: a
        // benchmark or screenshot taken here must submit the geometry a real session submits.
        Dictionary<Guid, ArrayMesh> lod1Library = ObjectsBuilder.ObjectLodEnabled
            ? ModelLibrary.Load(cacheDir, registry, neededGuids, ModelExtractor.Lod1Suffix, materials)
            : new Dictionary<Guid, ArrayMesh>();
        Node3D objectsRoot = objects.Count > 0
            ? ObjectsBuilder.Build(objects, db, meshLibrary, colliderLibrary, out withMesh, lod1Library)
            : new Node3D { Name = "Objects" };
        if (lod1Library.Count > 0)
            Log.Print($"[world] object LOD levels: {lod1Library.Count} of {meshLibrary.Count} meshes "
                + "have an authored lower level");

        Node3D vehiclesRoot = BuildVehicles(vehicles, db, meshLibrary, colliderLibrary, lod1Library);

        Node3D foliageRoot = foliageIndex != null
            ? FoliageBuilder.Build(foliageIndex, meshLibrary)
            : FoliageBuilder.Build(foliageData, meshLibrary);
        registry.ApplyAllAvailable();
        Log.Print($"[unturned-godot] Rendered {withMesh}/{objects.Count} objects with real meshes " +
            $"({meshLibrary.Count} unique)");
        return (objectsRoot, foliageRoot, vehiclesRoot, objects.Count, withMesh, meshLibrary.Count);
    }

    // Shared by both build paths so a vehicle is batched exactly like a placed object. The collider
    // library is handed straight through: ObjectsBuilder only builds bodies for LARGE/MEDIUM objects and
    // resources, so a vehicle's own colliders are cached but not turned into static world collision —
    // vehicles are rigidbodies in Unturned, and nothing drives them here yet.
    public static Node3D BuildVehicles(IReadOnlyList<PlacedObject> vehicles, ObjectAssetDatabase db,
        IReadOnlyDictionary<Guid, ArrayMesh> meshLibrary,
        IReadOnlyDictionary<Guid, List<CachedCollider>> colliderLibrary,
        IReadOnlyDictionary<Guid, ArrayMesh>? lod1Library)
    {
        if (vehicles.Count == 0)
            return new Node3D { Name = "Vehicles" };

        Node3D root = ObjectsBuilder.Build(vehicles, db, meshLibrary, colliderLibrary, out int withMesh,
            lod1Library, label: "Vehicles");
        Log.Print($"[unturned-godot] Rendered {withMesh}/{vehicles.Count} vehicles with real meshes");
        return root;
    }
}
