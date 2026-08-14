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
    double ObjectsMs,
    // The object/tree asset database this build scanned. Handed back rather than rebuilt by whoever needs
    // it next: the scan walks several thousand .dat files, and the crosshair's own hit test asks it the
    // same question the placement did — whether a fist can hurt this asset.
    ObjectAssetDatabase Assets);

public static class WorldBuilder
{
    // Conservative terrain occluders substantially reduce hidden object submission on hilly maps. Their
    // triangles are proven to remain below the source heightfield, so enable them by default; zero keeps
    // the uncullled path available for visual/performance A/B checks.
    private static bool OccludersEnabled => EnvFlag.IsOn(OS.GetEnvironment("TERRAIN_OCCLUDERS"), whenUnset: true);

    // Dedicated authority needs static object collision but none of the render/texture products. This
    // follows the same source ownership, asset metadata, extraction and ObjectsBuilder policy as the
    // interactive world, then asks ObjectsBuilder to realise only bodies and ladder volumes.
    public static Node3D BuildObjectCollision(string unturnedPath, LevelInfo level,
        out IReadOnlySet<Guid> selectedGuids, CollisionFieldBuilder? navigationField = null) =>
        BuildObjectCollision(unturnedPath, level, out selectedGuids, out _, navigationField);

    // With the server's ledger of what those bodies can break into: the same placements and the same
    // asset database, so a punch's hit and its collider are provably the same object.
    public static Node3D BuildObjectCollision(string unturnedPath, LevelInfo level,
        out IReadOnlySet<Guid> selectedGuids, out UnturnedGodot.Damage.DamageableWorld damageable,
        CollisionFieldBuilder? navigationField = null)
    {
        IReadOnlyList<ContentSource> sources = ContentSource.Discover(unturnedPath);
        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        List<PlacedTree> trees = LevelTrees.Load(System.IO.Path.Combine(level.Path,
            "Terrain", "Trees.dat"));

        ObjectAssetDatabase db = ContentExtraction.ScanAssets(sources);
        int legacy = LegacyPlacements.ResolveGuids(objects, db)
            + LegacyPlacements.AppendTrees(trees, objects, db);
        if (legacy > 0)
            Log.Print($"[server] Legacy placements resolved by id: {legacy}");
        HashSet<Guid> needed = db.ResolvePlacementGuids(objects);
        selectedGuids = needed;

        string cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        string textureCacheDir = ProjectSettings.GlobalizePath("user://texture_cache");
        var noFoliage = new Dictionary<Guid, FoliageAsset.Owned>();
        List<ContentExtraction.BundlePlan> plans = ContentExtraction.Plan(sources, cacheDir,
            textureCacheDir, needed, db, noFoliage);
        foreach (ContentExtraction.BundlePlan plan in plans)
        {
            if (plan.Missing.Count == 0)
                continue;
            try
            {
                ModelExtractor.ExtractMeshes(plan.Source, plan.Needed, cacheDir, db, plan.Foliage);
            }
            catch (Exception e)
            {
                Log.PrintErr($"[server] {plan.Source.Name} collision extraction failed "
                    + $"({e.GetType().Name}: {e.Message}); affected assets have no body this run");
            }
        }

        Dictionary<Guid, List<CachedCollider>> colliders = ColliderLibrary.Load(cacheDir, needed);
        damageable = UnturnedGodot.Damage.DamageableWorldBuilder.Build(objects, db);
        Node3D root = ObjectsBuilder.Build(objects, db,
            new Dictionary<Guid, ArrayMesh>(), colliders, out _,
            navigationField: navigationField, label: "DedicatedObjects", renderGeometry: false);
        Log.Print($"[server] authoritative object collision: {colliders.Count}/{needed.Count} asset types");
        Log.Print($"[server] breakable placements: {damageable.Count}");
        return root;
    }

    // HeightfieldMoveSolver keeps players/zombies on grade, but it cannot answer a 3D ray or stop a
    // capsule at a cliff. Publish the same cheap heightmap shapes as the interactive player path so a
    // dedicated attack cannot pass through terrain and its movement world is not object-only.
    public static Node3D BuildTerrainCollision(IReadOnlyList<HeightmapTile> tiles,
        CollisionFieldBuilder? navigationField = null)
    {
        var root = new Node3D { Name = "DedicatedTerrainCollision" };
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        foreach (HeightmapTile tile in tiles)
        {
            float[] data = tile.RawSamples != null
                ? TerrainHeightfield.MapData(tile.RawSamples)
                : TerrainHeightfield.MapData(tile.Heights);
            Transform3D placement = TerrainHeightfield.CollisionTransform(tile.CoordX, tile.CoordY);
            var body = new StaticBody3D { Name = $"Terrain_{tile.CoordX}_{tile.CoordY}" };
            body.AddChild(new CollisionShape3D
            {
                Shape = new HeightMapShape3D
                {
                    MapWidth = res,
                    MapDepth = res,
                    MapData = data,
                },
                Transform = placement,
            });
            root.AddChild(body);
            navigationField?.AddHeightfield(placement, res, res, data);
        }
        Log.Print($"[server] authoritative terrain collision: {tiles.Count} heightfield bodies");
        return root;
    }

    public static WorldBuildResult Build(string unturnedPath, string mapName)
    {
        var level = new LevelInfo(MapCatalog.ResolvePath(unturnedPath, mapName));
        Log.Print($"[unturned-godot] Loading map {level.Name} at {level.Path}");

        var terrainSw = Stopwatch.StartNew();
        (Node3D terrain, int tileCount, HeightmapSampler heights) = BuildTerrain(unturnedPath, level);
        terrainSw.Stop();

        var objectsSw = Stopwatch.StartNew();
        (Node3D objects, Node3D foliage, Node3D vehicles, int placed, int withMesh, int unique,
            ObjectAssetDatabase assets) = BuildObjects(level, unturnedPath);
        objectsSw.Stop();

        return new WorldBuildResult(terrain, heights, objects, foliage, vehicles, tileCount, placed,
            withMesh, unique, terrainSw.Elapsed.TotalMilliseconds, objectsSw.Elapsed.TotalMilliseconds,
            assets);
    }

    // Interactive-load variant of BuildTerrain: the pure-CPU layer resolve and tile/LOD generation run on
    // the thread pool (the main thread keeps rendering the loading screen), and the RenderingServer-
    // touching FinishTile calls yield to the render loop every few tiles so no single frame swallows all 16.
    public static System.Threading.Tasks.Task<(Node3D root, int tileCount, HeightmapSampler heights)>
        BuildTerrainAsync(string unturnedPath, LevelInfo level, Node yieldOn,
            System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, UnturnedGodot.Unity.CachedTexture>>? sharedLayers = null)
        => BuildTerrain(unturnedPath, level, sharedLayers,
            async () => await yieldOn.ToSignal(yieldOn.GetTree(), SceneTree.SignalName.ProcessFrame));

    public static (Node3D root, int tileCount, HeightmapSampler heights) BuildTerrain(
        string unturnedPath, LevelInfo level)
    {
        // With no yield callback nothing below ever awaits, so the task is already complete when it comes
        // back and this only unwraps it — GetResult rather than .Result so an exception is not re-wrapped.
        return BuildTerrain(unturnedPath, level, sharedLayers: null, yieldEvery: null)
            .GetAwaiter().GetResult();
    }

    // The one terrain build. The interactive and synchronous entry points above used to be near-identical
    // twins whose only difference was a Task.Run wrapper and an 8 ms yield budget, held equal by a comment
    // claiming they matched — a promise that had already rotted: the async twin's layer resolve had drifted
    // onto a worker thread while creating GPU textures. `yieldEvery` is what tells them apart, and a null
    // one means "never yield", which is the synchronous build.
    private static async System.Threading.Tasks.Task<(Node3D root, int tileCount, HeightmapSampler heights)>
        BuildTerrain(string unturnedPath, LevelInfo level,
            System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, UnturnedGodot.Unity.CachedTexture>>? sharedLayers,
            Func<System.Threading.Tasks.Task>? yieldEvery)
    {
        var tiles = level.EnumerateTiles();
        Log.Print($"[unturned-godot] Terrain tiles: {tiles.Count}");
        var heightTiles = new HeightmapTile[tiles.Count]; // kept for the height sampler (roads conform to it)

        // The map's own per-tile splat layer textures; tiles without a full set fall back to averaged
        // layer colors. Phase 1 is pure parsing/IO, so the interactive load hands it to the thread pool;
        // awaiting it lands back on Godot's synchronisation context, i.e. the main thread.
        TerrainLayers layers = yieldEvery == null
            ? LoadLayers(unturnedPath, level, sharedLayers)
            : await System.Threading.Tasks.Task.Run(() => LoadLayers(unturnedPath, level, sharedLayers));

        // Phase 2, main thread: the layer pixels become ImageTextures here and nowhere else.
        int textured = layers.Realise();
        Log.Print($"[unturned-godot] Terrain layers: {layers.TextureCount} textures, "
            + (textured > 0 ? $"{textured} tiles textured" : "flat-color fallback"));

        // Build the tiles' geometry + meshoptimizer LODs on worker threads (the LOD generation dominates
        // terrain build time and is pure CPU/meshopt on data-only ImporterMeshes), then realise the meshes
        // and attach materials on this (main) thread — the only steps that touch the RenderingServer.
        var meshes = new TerrainBuilder.TileMesh[tiles.Count];
        TerrainOccluder.Prepared[]? occluders = OccludersEnabled
            ? new TerrainOccluder.Prepared[tiles.Count] : null;
        void GenerateTiles() => System.Threading.Tasks.Parallel.For(0, tiles.Count, i =>
        {
            (int x, int y) = tiles[i];
            HeightmapTile tile = HeightmapTile.Read(level.HeightmapPath(x, y), x, y);
            heightTiles[i] = tile;
            SplatmapTile? splat = SplatmapTile.TryRead(level.SplatmapPath(x, y), x, y);
            meshes[i] = TerrainBuilder.BuildTileMesh(tile, splat, layers.For(x, y) != null && splat != null);
            if (occluders != null)
                occluders[i] = TerrainOccluder.Prepare(meshes[i]);
        });
        if (yieldEvery == null)
            GenerateTiles();
        else
            await System.Threading.Tasks.Task.Run(GenerateTiles);

        // Yield on a time budget, not every N tiles: waiting for a frame costs more than finishing the
        // tile that would have shared it, and a 52-tile map was spending most of this phase waiting.
        var terrainRoot = new Node3D { Name = "Terrain" };
        terrainRoot.AddToGroup(SceneGroups.Terrain);
        var controlTextures = new TerrainBuilder.ControlTextureCache();
        long budget = (long)(Stopwatch.Frequency * 0.008);
        long until = Stopwatch.GetTimestamp() + budget;
        for (int i = 0; i < meshes.Length; i++)
        {
            TerrainBuilder.TileMesh tm = meshes[i];
            terrainRoot.AddChild(TerrainBuilder.FinishTile(tm, layers.For(tm.X, tm.Y), controlTextures));
            if (occluders != null)
                terrainRoot.AddChild(TerrainOccluder.Finish(occluders[i]));
            if (yieldEvery != null && Stopwatch.GetTimestamp() >= until)
            {
                await yieldEvery();
                until = Stopwatch.GetTimestamp() + budget;
            }
        }
        return (terrainRoot, tiles.Count, new HeightmapSampler(heightTiles));
    }

    // Phase 1 of the layer resolve: which texture each tile paints with, as pixels. Worker-safe by design
    // — TerrainLayers.Realise is the half that creates the GPU resources. What it found is reported by the
    // caller (after Realise, which is what decides how many tiles ended up textured), so a map that renders
    // flat is traceable to either "no materials in the hierarchy" or "textures could not be extracted".
    private static TerrainLayers LoadLayers(string unturnedPath, LevelInfo level,
        System.Threading.Tasks.Task<IReadOnlyDictionary<Guid, UnturnedGodot.Unity.CachedTexture>>? sharedLayers = null)
        => TerrainLayers.Load(unturnedPath, level, sharedLayers);

    private static (Node3D root, Node3D foliage, Node3D vehicles, int placed, int withMesh, int unique, ObjectAssetDatabase assets)
        BuildObjects(LevelInfo level, string unturnedPath)
    {
        // The game's bundle plus any workshop mod that ships one: a workshop map places objects whose
        // prefabs live in the mod's bundle, not the game's.
        System.Collections.Generic.IReadOnlyList<ContentSource> sources = ContentSource.Discover(unturnedPath);
        // Trees are Unturned "resources" placed via a separate file; render them alongside objects so
        // the map isn't bare. They share the object transform, asset db and mesh pipeline.
        List<PlacedObject> objects = LevelObjects.Load(level.ObjectsDat);
        // The trees join them below, once the asset database can tell a pre-GUID one's RESOURCE id from
        // the object that shares its number (see LegacyPlacements).
        List<PlacedTree> trees = LevelTrees.Load(System.IO.Path.Combine(level.Path, "Terrain", "Trees.dat"));

        // Scan the object/tree asset DB on a worker thread: on the warm path db is only consumed after the
        // main-thread ModelLibrary.Load below (for fallback-box coloring), so the scan overlaps that load and
        // is effectively free. The cold extraction branch resolves it explicitly before use.
        var dbTask = System.Threading.Tasks.Task.Run(() => ContentExtraction.ScanAssets(sources));
        Log.Print($"[unturned-godot] Placed objects: {objects.Count + trees.Count} (incl. {trees.Count} trees)");

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
        ObjectAssetDatabase db = dbTask.Result;
        List<PlacedObject> vehicles = VehicleSpawnPlan.Load(level, sources, db);
        Log.Print($"[unturned-godot] Vehicles: {vehicles.Count} spawned");

        // A pre-GUID map names its objects and trees by legacy id; the needed set below is keyed on GUIDs,
        // so those placements borrow theirs from the asset database first — and the trees join the list
        // here, resolved through the resource namespace rather than the object one.
        int legacy = LegacyPlacements.ResolveGuids(objects, db)
            + LegacyPlacements.AppendTrees(trees, objects, db);
        if (legacy > 0)
            Log.Print($"[unturned-godot] Legacy placements resolved by id: {legacy}");

        // NPCs leave the list before the needed set is built, not after: they resolve to the player rig
        // rather than to an extracted mesh, so a GUID left in here is one the extraction plan chases on
        // every load and ObjectsBuilder then draws as a box (see NpcPlacements).
        List<PlacedObject> npcs = NpcPlacements.Partition(objects, db);

        HashSet<Guid> neededGuids = db.ResolvePlacementGuids(objects);
        foreach (PlacedObject v in vehicles)
            neededGuids.Add(v.Guid);
        foreach (Guid g in foliageAssets.Keys) // resolved foliage types only (see ObjectStreamer)
            neededGuids.Add(g);

        // Parse the 1.4 GB bundle once, then reuse the compact per-GUID mesh + texture cache. This is the
        // synchronous full build (used by the benchmark and the warm path); the interactive cold load
        // streams the two phases separately via ObjectStreamer. Extraction is driven by what THIS map is
        // missing from the (map-agnostic, GUID-keyed) cache, not by whether the cache has any files at all.
        System.Collections.Generic.List<ContentExtraction.BundlePlan> plans = ContentExtraction.Plan(
            sources, cacheDir, textureCacheDir, neededGuids, db, foliageAssets);
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
                    db, plan.Foliage);
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
        if (NpcsBuilder.Build(npcs, unturnedPath, out int npcsDrawn) is { } npcsRoot)
            objectsRoot.AddChild(npcsRoot);
        withMesh += npcsDrawn;

        Node3D vehiclesRoot = BuildVehicles(vehicles, db, meshLibrary, colliderLibrary, lod1Library);

        Node3D foliageRoot = foliageIndex != null
            ? FoliageBuilder.Build(foliageIndex, meshLibrary)
            : FoliageBuilder.Build(foliageData, meshLibrary);
        registry.ApplyAllAvailable();
        Log.Print($"[unturned-godot] Rendered {withMesh}/{objects.Count + npcs.Count} objects with real meshes " +
            $"({meshLibrary.Count} unique)");
        // NPCs are counted back in: they left `objects` to be built differently, not to stop being
        // placements, and a rendered total that includes them over one that does not reads as nonsense.
        return (objectsRoot, foliageRoot, vehiclesRoot, objects.Count + npcs.Count, withMesh,
            meshLibrary.Count, db);
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
