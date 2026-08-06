using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Assets;
using UnturnedGodot.Data;
using UnturnedGodot.Unity;

namespace UnturnedGodot;

// Streams the object world on a cold load: extract meshes (fast — SerializedFile only) on a worker, build
// the untextured scene so the map is playable in ~3 s, then extract textures (the ~1.18 GB .resS) on a
// worker and hot-swap them into the live materials as they land. A warm cache short-circuits to a normal
// synchronous build. All Godot node/resource work happens on the main thread (via Callable.CallDeferred);
// workers only do pure parsing/IO and hand texture keys back through a concurrent queue.
public partial class ObjectStreamer : Node
{
    [Signal] public delegate void MeshesReadyEventHandler(double elapsedMs);
    [Signal] public delegate void ProgressEventHandler(int applied, int total);
    [Signal] public delegate void FinishedEventHandler();

    // The world is finished AND no decode pass is still running. Finished alone does not say the second
    // half: a warm mesh cache with cold terrain layers builds and finishes the scene while its bundle
    // pass is still streaming. Anything that would open a bundle of its own — the deferred audio
    // extraction — has to wait for this one instead, or the two decode the same file at the same time,
    // recreating the multi-gigabyte peak the passes are serialized to avoid and racing over the same
    // cache files.
    [Signal] public delegate void ExtractionFinishedEventHandler();

    // GPU upload budget per frame while streaming. The ceiling is what measured well on a fast machine;
    // the live budget is clamped down from it by the frame time actually being achieved, so a machine that
    // drops to 20 fps backs off and hands the frame to the game rather than to texture streaming.
    private const double MaxApplySecondsPerFrame = 0.008;
    private const double MinApplySecondsPerFrame = 0.002;
    private double _frameSeconds;

    private IReadOnlyList<ContentSource> _sources = System.Array.Empty<ContentSource>();
    private string _cacheDir = "";
    private string _textureCacheDir = "";

    // Set before StartPrepare to send the extraction somewhere other than user://. For tests only; a
    // production load leaves both null and gets the session's real caches.
    internal string? CacheDirOverride { get; set; }
    internal string? TextureCacheDirOverride { get; set; }
    private LevelInfo _level = null!;
    // Kept from StartPrepare: the NPC characters are imported out of the install's resources.assets at
    // build time, long after the path was handed in.
    private string _unturnedPath = "";
    private List<PlacedObject> _npcs = new();

    private TextureRegistry _registry = null!;
    // One table for the whole load, so the base and lower-LOD libraries share their materials, and so the
    // cold path can re-group them once the textures it built them without have landed.
    private MaterialTable _materials = new();
    private List<PlacedObject> _objects = new();
    // The vehicles the map starts with, rolled from its own spawn tables. Placements like any other, so
    // they join the same needed set, extraction plan and mesh library — only their scene root is separate.
    private List<PlacedObject> _vehicles = new();
    private ObjectAssetDatabase _db = null!;

    // The object/tree assets this load scanned, once it has. Null until the scan finishes, which is why
    // the crosshair's hit test takes a callback rather than a value: it is built with the player, several
    // seconds before this exists, and asking it again per swing costs a field read.
    public ObjectAssetDatabase? Assets => _db;
    private Dictionary<Guid, FoliageAsset.Owned> _foliageAssets = new();
    private LevelFoliageChunks? _foliage;
    private FoliageResidencyIndex? _foliageIndex;
    // Held only between building the scene and warming the spawn ring; the tree owns the node itself.
    private FoliageStreamingRenderer? _foliageRenderer;

    // Every GUID this map needs a mesh for: placed objects, trees and the resolved foliage types. Drives
    // both the cold-load check and which slice of the shared cache is realised.
    private HashSet<Guid> _neededGuids = new();
    public IReadOnlySet<Guid> NeededGuids => _neededGuids;

    // The level's breakable placements (trees, rocks, rubble props) with the health off their assets,
    // built once the map has been read and handed to the hosted server. Null until then.
    public UnturnedGodot.Damage.DamageableWorld? Damageable { get; private set; }
    private List<ContentExtraction.BundlePlan> _plans = new();

    private readonly ConcurrentQueue<string> _readyKeys = new();
    private int _totalTextureKeys;
    private int _appliedTextures;
    private volatile bool _texturesDone;
    private bool _sceneBuilt;
    private bool _drainedFinal;
    private bool _streamStarted;
    private bool _finished;
    private bool _loadStateReleased;
    private Stopwatch _coldWatch = new();
    private Task _prepTask = Task.CompletedTask;
    private Task _streamTask = Task.CompletedTask;
    private readonly CancellationTokenSource _loadCancellation =
        CancellationTokenSource.CreateLinkedTokenSource(AppShutdown.Token);

    // The cold load runs the bundle decode ahead of Begin(), so these two say what has landed: the decode
    // signalling its mesh phase, and the streamer being in the tree with the world around it.
    private volatile bool _cold;
    private bool _meshesExtracted;
    private bool _coldBuildStarted;
    private Task _coldBuildTask = Task.CompletedTask;
    private bool _readyToBuild;
    private bool _began;

    // A warm load resolves every texture identity while it realises, so its materials are shared as far as
    // they go the moment they are built. The cold path builds them before the textures exist, so it owes
    // one more pass — this stays false until that pass has run, and the load state it reads is held open.
    private bool _materialsSettled = true;
    private Task _rededupTask = Task.CompletedTask;

    // The terrain's splat layer textures live in the same bundles as the objects, so this pass produces
    // them too and the terrain build takes them from here instead of decoding everything a second time.
    private readonly TaskCompletionSource<IReadOnlyDictionary<Guid, CachedTexture>> _layerTextures =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, CachedTexture> _layersProduced = new();
    private Dictionary<string, TerrainLayerPlan.BundleWants> _layerWants = new();
    private int _layersOutstanding;

    // The movement/zombie audio each bundle owes, by bundle path. Planned here rather than at the player's
    // spawn so the decode pass can carry the clips out of the same forward read as the textures: they sit
    // in the .resource node at the end of the blob the pass already walks to. Extracting them afterwards
    // meant a second whole-bundle LZMA decode and left its transient heap resident for the session.
    private Dictionary<string, AudioExtractor.Request> _audioWants = new(StringComparer.Ordinal);

    // Completes with every layer texture this pass decoded (empty when the cache already had them all, or
    // when nothing was decoded). Always completes: the terrain build waits on it.
    public Task<IReadOnlyDictionary<Guid, CachedTexture>> LayerTextures => _layerTextures.Task;

    // Set before Begin() by a load that intends to reconcile the navmesh: the object collision bodies are
    // mirrored into it as they are created, so the reconciliation pass can probe them on a worker instead
    // of a physics tick. Left null (free-cam, previews) the reconciliation falls back to the server.
    public CollisionFieldBuilder? NavigationField { get; set; }

    // Completes when the world is finished, faults when building it failed. The caller awaits this so a
    // failure lands in the load's error handling: without it, a warm build that threw while realising
    // meshes left the loading screen up for good, since Finished is never emitted and BeginAsync had
    // already returned.
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    // Kicks off the placement/asset IO — LevelObjects/Trees/Foliage.Load and the two asset-DB scans, all
    // pure file reads + parsing with no Godot objects — on a worker so it overlaps the caller's main-thread
    // terrain build. Call this before building terrain; then Begin() once the streamer is in the tree.
    public void StartPrepare(string unturnedPath, LevelInfo level)
    {
        _level = level;
        _sources = ContentSource.Discover(unturnedPath);
        // Where the extraction's output lands. Overridable so a test can drive a real prepare into a
        // temporary directory: resolved unconditionally to user://, any test of this path would write
        // into the machine's own model and texture caches, which is not a thing a test may do.
        // Production never sets the override.
        _cacheDir = CacheDirOverride ?? ProjectSettings.GlobalizePath("user://model_cache");
        _textureCacheDir = TextureCacheDirOverride ?? ProjectSettings.GlobalizePath("user://texture_cache");
        _registry = new TextureRegistry(_textureCacheDir);
        _prepTask = AppShutdown.Track(Task.Run(() =>
        {
            bool decodeWillSettleIt = false;
            try
            {
                _loadCancellation.Token.ThrowIfCancellationRequested();
                decodeWillSettleIt = Prepare(unturnedPath, level);
            }
            finally
            {
                // The terrain build is already blocked on this promise. If preparation threw — an
                // unreadable Foliage.blob is enough — leaving it unfinished hung the loading screen for
                // good, before anything could report the failure. Only the decode pass is allowed to
                // leave it open, because it is the one that fills it; BeginAsync still rethrows the real
                // exception, which is what puts the error on screen.
                if (!decodeWillSettleIt)
                    _layerTextures.TrySetResult(_layersProduced);
            }
        }));
    }

    // A failed build can happen before this node joins the scene tree, so QueueFree cannot own its workers.
    // Wait for preparation and the decode it may launch before another map is allowed to start loading.
    public async Task CancelAsync()
    {
        _loadCancellation.Cancel();
        await ObserveStopped(_prepTask);
        await ObserveStopped(_streamTask);
        await ObserveStopped(_coldBuildTask);
        await ObserveStopped(_rededupTask);
        _layerTextures.TrySetResult(_layersProduced);
        _completion.TrySetCanceled(_loadCancellation.Token);
    }

    private static async Task ObserveStopped(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception) { /* the failed load already reports the original error */ }
    }

    // Reads everything the extraction needs to know and, on a cold load, starts it. Returns true when
    // that pass took over responsibility for completing LayerTextures.
    private bool Prepare(string unturnedPath, LevelInfo level)
    {
        _unturnedPath = unturnedPath;
        LoadPlacements();

        // The terrain layers are planned first so the bundles that owe them are part of the extraction
        // plan: a source can owe layers and no mesh at all, and one left out of the plans is a bundle
        // the terrain has to decode by itself.
        _layerWants = PlanTerrainLayers(unturnedPath, level);

        // Cold-load when anything THIS map places is missing from the cache. The cache is shared by
        // every map (GUIDs are global), so "the cache directory is not empty" says nothing about
        // whether the map being loaded was ever extracted — picking a second map would otherwise
        // render its objects as fallback boxes forever. Known misses (assets the bundle has no mesh
        // for) are excluded, or they would make every boot look cold.
        // Each bundle keeps its own index, so a workshop map's custom objects are tracked separately
        // from the game's: neither can mask the other as "already extracted".
        _plans = ContentExtraction.Plan(_sources, _cacheDir, _textureCacheDir, _neededGuids, _db, _foliageAssets,
            new HashSet<string>(_layerWants.Keys, StringComparer.Ordinal));
        _cold = ContentExtraction.TotalMissing(_plans) > 0;
        _audioWants = PlanAudio(unturnedPath);

        // Start decoding here rather than in Begin(): the bundles are the longest pole of a first
        // load, and nothing about them depends on the terrain, the roads or the player. Running them
        // alongside those stages is worth more than any single-threaded speedup inside the decode.
        foreach (TerrainLayerPlan.BundleWants wants in _layerWants.Values)
            foreach (Guid[] materials in wants.ByContainerPath.Values)
                _layersOutstanding += materials.Length;

        if (!_cold && _layerWants.Count == 0)
            return false;

        foreach (string report in ContentExtraction.PendingReports(_plans))
            Log.Print(report);
        StartStreaming();
        return true;
    }

    // Awaited rather than waited on: the preparation runs while the terrain builds, but if it is still
    // going when the loading screen reaches this stage, blocking here would freeze the main thread and
    // stop the screen animating. The continuation resumes on the main thread (Godot installs its
    // synchronisation context there), which is what the rest of this file already relies on.
    public async Task BeginAsync()
    {
        if (_began)
            return;

        _began = true;
        await _prepTask;
        SetProcess(_cold);
        _readyToBuild = true; // the decode may already have meshes waiting on this
        MaybeBuildScene();
    }

    // The scene is built once the streamer is in the tree with the world around it (Begin) and, on a cold
    // load, every bundle has finished its mesh phase. Either can land first: the decode starts during the
    // terrain build, so its mesh phase can signal before the loading screen even reaches the object stage.
    private void MaybeBuildScene()
    {
        if (_sceneBuilt || !_readyToBuild)
            return;

        if (!_cold)
        {
            // Marked here rather than inside: the warm build awaits, so a second call landing while it is
            // still running would otherwise build the whole world a second time. That is reachable —
            // a map whose meshes are cached but whose terrain layers are not still runs a decode pass,
            // and its mesh-phase signal calls this before Begin does.
            _sceneBuilt = true;

            _ = BuildAndFinish().ContinueWith(
                t => _completion.TrySetException(t.Exception!.InnerExceptions),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        // Unlike the warm path this cannot mark _sceneBuilt up front as its re-entry guard: that flag
        // also releases _Process to apply textures, which must not start before the scene exists. A
        // dedicated latch keeps a second call from building the world twice while the first is still
        // realising.
        if (_meshesExtracted && !_coldBuildStarted)
        {
            _coldBuildStarted = true;
            // Every material this build creates is built against a texture cache that is still being
            // written, so their identities — and the sharing that follows from them — are provisional
            // until streaming settles.
            _materialsSettled = false;
            _materials.TrackSurfaces();
            // Held, not fire-and-forget: this build now spans frames, so CancelAsync has to be able to
            // wait for it before the load state it reads is torn down.
            _coldBuildTask = OnMeshesExtractedAsync();
            _ = _coldBuildTask.ContinueWith(
                t => _completion.TrySetException(t.Exception!.InnerExceptions),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    // What the terrain still owes, grouped by the bundle that carries it. Cached layers are excluded, so
    // a warm terrain cache asks the pass for nothing.
    private Dictionary<string, TerrainLayerPlan.BundleWants> PlanTerrainLayers(string unturnedPath,
        LevelInfo level)
    {
        try
        {
            Dictionary<(int x, int y), Guid[]> tiles =
                LevelHierarchy.ReadTileMaterials(Path.Combine(level.Path, "Level.hierarchy"));
            if (tiles.Count == 0)
                return new Dictionary<string, TerrainLayerPlan.BundleWants>();

            var materials = new Dictionary<Guid, LandscapeMaterialAsset>();
            var claimantRoots = new Dictionary<Guid, string>();
            foreach (ContentSource source in _sources)
                LandscapeMaterialAsset.MergeFirstClaimants(materials, claimantRoots, source.Root,
                    LandscapeMaterialAsset.ScanDirectory(Path.Combine(source.AssetsDir, "Landscapes")));

            var needed = new HashSet<Guid>();
            foreach (Guid[] guids in tiles.Values)
                foreach (Guid guid in guids)
                    if (guid != Guid.Empty && materials.ContainsKey(guid))
                        needed.Add(guid);

            Dictionary<string, TerrainLayerPlan.BundleWants> allWants =
                TerrainLayerPlan.ByBundle(needed, materials, claimantRoots, _sources, MasterBundleConfig.Load);
            Dictionary<Guid, string> bundlePaths = TerrainLayerPlan.MaterialBundlePaths(allWants);
            return TerrainLayerPlan.ByBundle(TerrainLayerCache.Missing(needed, bundlePaths), materials,
                claimantRoots, _sources, MasterBundleConfig.Load);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The terrain build resolves them itself if this could not be planned.
            return new Dictionary<string, TerrainLayerPlan.BundleWants>();
        }
    }

    // Which definitions each bundle still owes the audio cache, keyed by bundle path so the decode pass
    // can pick up its own. A scan of the physics materials only — the same cheap .dat walk the player's
    // movement audio does at spawn — so planning it twice costs a directory read, and a request whose
    // definitions are all cached is dropped here rather than handed to the pass as an empty want.
    private Dictionary<string, AudioExtractor.Request> PlanAudio(string unturnedPath)
    {
        var wants = new Dictionary<string, AudioExtractor.Request>(StringComparer.Ordinal);

        // Nothing plays these when no player spawns. FREECAM and STEP_PROBE are exactly the modes used to
        // measure the load, so making them read the .resource tail and rebuild clips no session will ever
        // hear both slows the measurement and taxes the thing being measured. A later ordinary session
        // fills the cache.
        if (OS.GetEnvironment("STEP_PROBE").Length > 0
            || EnvFlag.IsOn(OS.GetEnvironment("FREECAM"), whenUnset: false))
        {
            return wants;
        }

        try
        {
            string audioCacheDir = ProjectSettings.GlobalizePath("user://audio_cache");
            foreach (AudioExtractor.Request request in MovementAudioRequests.For(_sources,
                MovementAudioRequests.ScanPhysicsMaterials(_sources), unturnedPath, audioCacheDir))
            {
                if (request.BundlePath.Length > 0 && !AudioExtractor.IsSatisfied(request))
                    wants[request.BundlePath] = request;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The player's own deferred extraction still runs and reports whatever went wrong there.
        }

        return wants;
    }

    private void LoadPlacements()
    {
        // Three independent reads, and the bundle decode cannot start until all of them are in: the
        // placements (a hundred thousand of them on a workshop map), the asset database (thousands of
        // .dat files) and the foliage. Running them together is what lets the decode start sooner.
        // Trees stay in their own list until the asset database is in: a pre-GUID one is placed by an id
        // from the RESOURCE namespace, and only the database can turn that into a GUID without confusing
        // it with the identically-numbered object (see LegacyPlacements).
        var trees = new List<PlacedTree>();
        Task placements = Task.Run(() =>
        {
            _objects = LevelObjects.Load(_level.ObjectsDat);
            trees = LevelTrees.Load(Path.Combine(_level.Path, "Terrain", "Trees.dat"));
        });

        Task assets = Task.Run(() => _db = ContentExtraction.ScanAssets(_sources));

        Task foliage = Task.Run(() =>
        {
            string blobPath = Path.Combine(_level.Path, "Foliage.blob");
            if (FoliageBuilder.SpatialResidencyEnabled && File.Exists(blobPath))
            {
                string indexDirectory = ProjectSettings.GlobalizePath("user://foliage_index");
                string indexPath = Path.Combine(indexDirectory,
                    FoliageResidencyIndex.CacheFileName(blobPath));
                var watch = Stopwatch.StartNew();
                _foliageIndex = FoliageResidencyIndex.LoadOrBuild(blobPath, indexPath,
                    FoliageBuilder.RuntimeChunkTiles, out bool cacheHit);
                if (_foliageIndex == null)
                {
                    _foliage = LevelFoliageChunks.Load(blobPath, FoliageBuilder.RuntimeChunkTiles);
                    AppShutdown.PrintUnlessQuitting("[foliage-stream] source disappeared during indexing; "
                        + "using the legacy foliage loader");
                }
                else
                {
                    AppShutdown.PrintUnlessQuitting($"[foliage-stream] {(cacheHit ? "loaded" : "built")} "
                        + $"index with {_foliageIndex.Chunks.Count} chunks in {watch.ElapsedMilliseconds} ms");
                }
            }
            else
            {
                _foliage = LevelFoliageChunks.Load(blobPath, FoliageBuilder.RuntimeChunkTiles);
            }
            // Across every source, not just the core Assets folder: a workshop map's own grass and pebble
            // assets live next to its bundle, and scanning core alone leaves them unresolved — no needed
            // GUID, no extraction, no foliage on the map.
            IReadOnlyList<Guid>? foliageGuids = _foliageIndex?.AssetGuids ?? _foliage?.AssetGuids;
            if (foliageGuids != null)
                _foliageAssets = FoliageAsset.ScanSources(_sources, new HashSet<Guid>(foliageGuids));
        });

        Task.WaitAll(placements, assets, foliage);

        // Needs both of the first two: a pre-GUID map names its objects and trees by legacy id, and only
        // the asset database can turn those into the GUIDs the rest of the load is keyed on.
        int legacy = LegacyPlacements.ResolveGuids(_objects, _db)
            + LegacyPlacements.AppendTrees(trees, _objects, _db);
        if (legacy > 0)
            Log.Print($"[stream] legacy placements resolved by id: {legacy}");

        // After the asset scan, which it resolves its vehicles through, and before the needed set below.
        _vehicles = VehicleSpawnPlan.Load(_level, _sources, _db);
        Log.Print($"[stream] vehicles: {_vehicles.Count} spawned");

        // NPCs leave the object list before the needed set is built: they resolve to the player rig, not
        // to an extracted mesh, so counting them would keep every load looking cold (see NpcPlacements).
        _npcs = NpcPlacements.Partition(_objects, _db);

        _neededGuids = _db.ResolvePlacementGuids(_objects);
        foreach (PlacedObject v in _vehicles)
            _neededGuids.Add(v.Guid);
        // Only the foliage types that actually resolved to an asset: an unresolved GUID has nothing to
        // extract, so counting it as needed would report the cache cold on every boot.
        foreach (Guid g in _foliageAssets.Keys)
            _neededGuids.Add(g);
    }

    // Warm path: meshes and textures are already cached. Staged across frames so the loading screen
    // stays fluid: the ~400 ArrayMeshes realise in batches, then the scene builds, then textures apply.
    private async Task BuildAndFinish()
    {
        var phase = Stopwatch.StartNew();
        var meshLibrary = await ModelLibrary.LoadStagedAsync(_cacheDir, _registry, this, _neededGuids,
            sharedMaterials: _materials);
        Log.Print($"[stream] meshes realised: {phase.ElapsedMilliseconds} ms ({meshLibrary.Count})");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Dictionary<Guid, ArrayMesh> lod1Library = await LoadLod1LibraryAsync();

        phase.Restart();
        BuildObjects(meshLibrary, lod1Library);
        Log.Print($"[stream] scene built: {phase.ElapsedMilliseconds} ms");

        // Textures first, and before any frame is yielded. A material that reaches the renderer bare and
        // is textured a frame later makes it build the pipelines twice, so the warm pass — which yields
        // several frames — must not come between the scene and its textures.
        phase.Restart();
        _registry.ApplyAllAvailable();
        Log.Print($"[stream] textures applied: {phase.ElapsedMilliseconds} ms");

        // Then warm, still before the yield below. The build above put the streaming foliage in the
        // tree, so the very next frame is the one whose _Process runs its first plan — and takes the
        // whole spawn ring synchronously. Warming has to claim that frame first or there is nothing
        // left to warm.
        await PrewarmFoliageAsync();
        // The warm pass consumes cancellation and returns normally, so returning to the menu mid-pass
        // would otherwise fall through to publishing a world already queued for deletion. The cold
        // build below makes the same check for the same reason.
        if (_loadCancellation.IsCancellationRequested)
            return;
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        _finished = true;
        TryFinalizeLoadState();
        EmitSignal(SignalName.MeshesReady, 0.0);
        EmitFinishedAndReleaseNeededGuids();
        _completion.TrySetResult();
    }

    // Finished subscribers still need the selected map's GUIDs: Main uses them to fingerprint the
    // collider subset before navigation reconciliation. Release the set only after all synchronous
    // subscribers have consumed it, without allocating a second snapshot on every load.
    private void EmitFinishedAndReleaseNeededGuids()
    {
        EmitSignal(SignalName.Finished);
        _neededGuids.Clear();
        _neededGuids = new HashSet<Guid>();
    }

    // The authored lower levels go through the same staged realise as the base library, on both the warm
    // and the cold path, or half of a map's object geometry blocks the main thread in one frame and
    // freezes the loading animation.
    private Task<Dictionary<Guid, ArrayMesh>> LoadLod1LibraryAsync() =>
        ObjectsBuilder.ObjectLodEnabled
            ? ModelLibrary.LoadStagedAsync(_cacheDir, _registry, this, _neededGuids,
                ModelExtractor.Lod1Suffix, _materials)
            : Task.FromResult(new Dictionary<Guid, ArrayMesh>());

    private void BuildObjects(Dictionary<Guid, ArrayMesh> meshLibrary,
        Dictionary<Guid, ArrayMesh> lod1Library)
    {
        // The freecam mode never spawns the player, so collision bodies would sit unused — skip the collider
        // library entirely there and build the objects render-only (saves the shape/BVH build + its memory).
        var colliderLibrary = EnvFlag.IsOn(OS.GetEnvironment("FREECAM"), whenUnset: false)
            ? new Dictionary<Guid, List<CachedCollider>>()
            : ColliderLibrary.Load(_cacheDir, _neededGuids);
        var stage = Stopwatch.StartNew();
        Node3D root = ObjectsBuilder.Build(_objects, _db, meshLibrary, colliderLibrary, out int withMesh,
            lod1Library, NavigationField);
        // Handed on to reconciliation by whoever set it; this node has no further use for it and must not
        // be what keeps a map-sized collision mirror alive for the session.
        NavigationField = null;
        if (lod1Library.Count > 0)
            Log.Print($"[stream] object LOD levels: {lod1Library.Count} of {meshLibrary.Count} meshes "
                + "have an authored lower level");
        double buildMs = stage.Elapsed.TotalMilliseconds;
        stage.Restart();
        if (NpcsBuilder.Build(_npcs, _unturnedPath, out int npcsDrawn) is { } npcsRoot)
            root.AddChild(npcsRoot);
        withMesh += npcsDrawn;
        AddChild(root);
        AddChild(WorldBuilder.BuildVehicles(_vehicles, _db, meshLibrary, colliderLibrary, lod1Library));
        double attachMs = stage.Elapsed.TotalMilliseconds;
        stage.Restart();
        Node3D foliageRoot = _foliageIndex != null
            ? FoliageBuilder.Build(_foliageIndex, meshLibrary)
            : FoliageBuilder.Build(_foliage, meshLibrary);
        AddChild(foliageRoot);
        _foliageRenderer = foliageRoot as FoliageStreamingRenderer;
        Log.Print($"[stream] objects build {buildMs:0} ms, attach {attachMs:0} ms, "
            + $"foliage {stage.Elapsed.TotalMilliseconds:0} ms");
        _totalTextureKeys = _registry.PendingKeyCount;
        Log.Print($"[stream] built {withMesh}/{_objects.Count + _npcs.Count} objects ({meshLibrary.Count} meshes), " +
            $"{_totalTextureKeys} texture keys pending");

        // The hosted server's ledger of what can be broken, derived from the SAME placements and asset
        // database the bodies above were built from — which is the whole reason it is taken here rather
        // than re-read later: an independently loaded copy could disagree about what stands where, and
        // then a punch would break the wrong tree. Distilled to positions and hit points, so it survives
        // the drop below at a fraction of the size.
        Damageable = UnturnedGodot.Damage.DamageableWorldBuilder.Build(_objects, _db);
        Log.Print($"[stream] breakable placements: {Damageable.Count}");

        // These parsed inputs are consumed only up to here — the MultiMesh buffers now hold their own
        // copies and the streaming worker already captured _db by value. Drop them so the ~32 MB foliage
        // transform graph and the placement/asset lists don't live on this node for the whole session.
        _foliage = null;
        _foliageIndex = null;
        _objects = null!;
        _vehicles = null!;
        _db = null!;
        _foliageAssets = new(); // consumed by the streaming worker / mesh extraction; drop it too
    }

    // Hands the streaming foliage the spawn ring while the loading screen still owns the frame. The
    // camera is already where the player will stand — Main spawns the character before this node begins —
    // so this is the first plan the renderer would have run anyway, paid for here instead of on the frame
    // the world appears. It yields internally; awaiting it keeps the load staged rather than overlapping
    // its uploads with the texture apply below.
    private async Task PrewarmFoliageAsync()
    {
        FoliageStreamingRenderer? foliage = _foliageRenderer;
        _foliageRenderer = null;
        if (foliage == null || _loadCancellation.IsCancellationRequested)
            return;
        // The load's own token, not just the renderer's lifetime one: that node cannot leave the tree
        // until CancelAsync has finished waiting for this task, so without it a cancelled load would sit
        // through the whole remaining warm pass before the menu came back.
        await foliage.PrewarmAsync(_loadCancellation.Token);
    }

    private void StartStreaming()
    {
        Log.Print("[stream] cold load: streaming meshes then textures from one decode pass...");
        _coldWatch = Stopwatch.StartNew();

        // One decode pass per bundle that owes this map something. A decoded serialized-file node can be
        // hundreds of MiB, so passes are deliberately serialized: compressed file size does not safely
        // predict their resident expansion, and overlapping core plus workshop nodes caused multi-GiB
        // cold-load peaks. The scene still waits for the mesh phase of every pass.
        var pending = new List<ContentExtraction.BundlePlan>();
        foreach (ContentExtraction.BundlePlan plan in _plans)
            if (plan.NeedsExtraction || LayerWantsFor(plan).ByContainerPath.Count > 0)
                pending.Add(plan);

        if (pending.Count == 0)
            _layerTextures.TrySetResult(_layersProduced);

        int meshPhasesLeft = pending.Count;
        // Registered so quitting mid-decode waits for the pass to reach its next checkpoint instead of
        // tearing the tree down underneath it.
        _streamStarted = true;
        CancellationToken cancellation = _loadCancellation.Token;
        _streamTask = AppShutdown.Track(Task.Run(() =>
        {
            try
            {
                foreach (ContentExtraction.BundlePlan plan in pending)
                {
                    cancellation.ThrowIfCancellationRequested();
                    TerrainLayerPlan.BundleWants layers = LayerWantsFor(plan);
                    long megabytes = new FileInfo(plan.Source.BundlePath).Length >> 20;
                    AudioExtractor.Request? audio = _audioWants.GetValueOrDefault(plan.Source.BundlePath);
                    AppShutdown.PrintUnlessQuitting($"[stream] decoding {plan.Source.Name} ({megabytes} MB) for "
                        + $"{plan.Missing.Count} meshes, {plan.MissingTextures.Count} textures, "
                        + $"{layers.ByContainerPath.Count} terrain layers and "
                        + $"{audio?.DefPaths.Count ?? 0} audio definitions…");
                    ModelExtractor.StreamExtract(plan.Source, plan.Needed, _cacheDir, _textureCacheDir, _db,
                        onMeshesReady: () =>
                        {
                            if (Interlocked.Decrement(ref meshPhasesLeft) == 0)
                                DeferUnlessStopped(OnMeshPhaseDone);
                        },
                        onTextureWritten: key => _readyKeys.Enqueue(key),
                        foliageAssets: plan.Foliage, audio: audio,
                        layerWantsByPath: layers.ByContainerPath,
                        onLayerTexture: (material, texture) =>
                        {
                            _layersProduced[material] = texture;
                            TerrainLayerCache.Write(material, texture, plan.Source.BundlePath);

                            // Release the terrain as soon as its last layer lands: the pass still has the
                            // object textures to stream, and the game's own layers are inline, so waiting
                            // for the whole pass held the terrain back by seconds for nothing.
                            if (Interlocked.Decrement(ref _layersOutstanding) <= 0)
                                _layerTextures.TrySetResult(_layersProduced);
                        }, cancellationToken: cancellation);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // Expected when a failed attempt returns to the map menu.
            }
            catch (Exception e)
            {
                DeferUnlessStopped(() => Log.PrintErr($"[stream] extraction failed: {e}"));
                // Unblock the pipeline: build whatever the cache holds so MeshesReady/Finished still fire
                // (otherwise the loading screen would wait forever on a signal that never comes).
                DeferUnlessStopped(OnMeshPhaseDone);
            }
            finally
            {
                // The terrain build blocks on this, so it has to be settled even when the pass died.
                _layerTextures.TrySetResult(_layersProduced);
                _texturesDone = true;
                DeferUnlessStopped(TryFinalizeLoadState);
            }
        }, cancellation));
    }

    // A tracked decoder normally finishes before AppShutdown quits. Its grace period is deliberately
    // finite, though, so a worker stuck in slow IO may outlive the tree. Never enqueue engine work after
    // cancellation, and check again when the callback runs in case shutdown began while it was queued.
    private void DeferUnlessStopped(Action callback)
    {
        if (_loadCancellation.IsCancellationRequested)
            return;
        Callable.From(() =>
        {
            if (!_loadCancellation.IsCancellationRequested)
                callback();
        }).CallDeferred();
    }

    // Final release is gated by all three consumers: Finished says the main-thread scene no longer needs
    // the preparation graph; _texturesDone says no decoder can still be reading it; _materialsSettled says
    // the cold path's material re-grouping has run, which reads the registry's identity map and turns the
    // duplicate materials into garbage — the reclaim below is worth much less before it. This also handles
    // the warm layer-only path, where Finished may precede the background bundle pass.
    private void TryFinalizeLoadState()
    {
        if (_loadStateReleased || !_finished || !_materialsSettled || (_streamStarted && !_texturesDone))
            return;
        _loadStateReleased = true;

        int retainedItems = _sources.Count + _plans.Count + _neededGuids.Count + _layerWants.Count +
            _layersProduced.Count + _foliageAssets.Count + _readyKeys.Count + _audioWants.Count;
        int registryEntries = _registry.ReleaseLoadingIndexes();
        _materials.Release();
        _sources = Array.Empty<ContentSource>();
        _plans.Clear();
        _plans = new List<ContentExtraction.BundlePlan>();
        _layerWants.Clear();
        _layerWants = new Dictionary<string, TerrainLayerPlan.BundleWants>();
        _audioWants.Clear();
        _audioWants = new Dictionary<string, AudioExtractor.Request>(StringComparer.Ordinal);
        _layersProduced.Clear();
        while (_readyKeys.TryDequeue(out _)) { }
        _foliageAssets.Clear();
        _foliageAssets = new Dictionary<Guid, FoliageAsset.Owned>();
        _foliage = null;
        _foliageIndex = null;
        _objects = null!;
        _vehicles = null!;
        _db = null!;
        _level = null!;
        _cacheDir = "";
        _textureCacheDir = "";
        _prepTask = Task.CompletedTask;
        Log.Print($"[stream] released loading state: {retainedItems} items + " +
            $"{registryEntries} texture-index entries");
        LoadMemory.Reclaim("post-load");
        // After the reclaim: whatever this releases is the one-time work's, and a listener that starts a
        // decode of its own should not have its first allocations compacted out from under it.
        EmitSignal(SignalName.ExtractionFinished);
    }

    private TerrainLayerPlan.BundleWants LayerWantsFor(ContentExtraction.BundlePlan plan) =>
        TerrainLayerPlan.For(_layerWants, plan.Source.BundlePath);

    // Main thread: every bundle has finished its mesh phase. The scene can only be built once the streamer
    // is also in the tree, which Begin() decides.
    private void OnMeshPhaseDone()
    {
        _meshesExtracted = true;
        MaybeBuildScene();
    }

    // Main thread: meshes are cached — build the (untextured) scene. Textures keep streaming in on the
    // worker; _Process applies them (once this registry exists) as their keys land in the queue.
    private async Task OnMeshesExtractedAsync()
    {
        Dictionary<Guid, ArrayMesh> meshLibrary = ModelLibrary.Load(_cacheDir, _registry, _neededGuids,
            sharedMaterials: _materials);

        Dictionary<Guid, ArrayMesh> lod1Library = await LoadLod1LibraryAsync();

        // The staged realise above yields, so a cancel can land mid-flight. Returning here leaves the
        // scene unbuilt rather than attaching it to a node already on its way out.
        if (_loadCancellation.IsCancellationRequested)
            return;

        BuildObjects(meshLibrary, lod1Library);

        // Whatever the decode already produced goes on before the world is shown. Applying a texture
        // changes the material's shader key (an albedo map appears, the filter may switch to nearest, a
        // cutout swaps shader), so a material that reaches the scene bare and is textured a frame later
        // makes the renderer build its pipelines twice. Everything still arriving keeps streaming through
        // _Process; this only closes the gap for what was ready all along, at a cost of tens of ms.
        // It runs before the warm pass, which yields frames: they would be exactly that bare gap.
        _appliedTextures += _registry.ApplyAllAvailable();

        // Warmed before time-to-playable is read, and counted in it: a world handed over with its spawn
        // ring undecoded is not yet playable without the burst this replaces. _sceneBuilt is set after,
        // not before: it is also what releases _Process to apply textures, and the two would then spend
        // their separate per-frame budgets in the same frames the warm pass is pacing itself against.
        await PrewarmFoliageAsync();
        // The warm pass consumes the cancellation and returns normally, so the check has to be made
        // here. Falling through would set _sceneBuilt — which also releases _Process — and go on to
        // publish MeshesReady and Finished for a world already being torn down, while BackToMenu is
        // still inside CancelAsync waiting for this task.
        if (_loadCancellation.IsCancellationRequested)
            return;
        _sceneBuilt = true;
        // Read after the build, not before it: this is reported as time-to-playable, and the staged
        // realise above happens while the player is still waiting.
        double meshMs = _coldWatch.Elapsed.TotalMilliseconds;

        EmitSignal(SignalName.MeshesReady, meshMs);
        EmitSignal(SignalName.Progress, _appliedTextures, _totalTextureKeys);
        Log.Print($"[stream] playable in {meshMs:0} ms ({_appliedTextures}/{_totalTextureKeys} "
            + "textures already applied); the rest stream in...");
    }

    public override void _Process(double delta)
    {
        if (!_sceneBuilt)
            return;

        // Pace by time, not by count: a fixed budget per frame keeps the frame smooth whatever the
        // textures cost, where "eight per frame" turned a thousand-texture map into two hundred frames of
        // waiting after everything had already been decoded.
        _frameSeconds = _frameSeconds <= 0 ? delta : (_frameSeconds * 0.9) + (delta * 0.1);
        double seconds = Math.Clamp(_frameSeconds / 3.0, MinApplySecondsPerFrame, MaxApplySecondsPerFrame);

        int applied = 0;
        long until = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * seconds);
        while (_readyKeys.TryDequeue(out string? key))
        {
            if (_registry.Apply(key))
            {
                _appliedTextures++;
                applied++;
            }

            if (Stopwatch.GetTimestamp() >= until)
                break;
        }

        if (applied > 0)
            EmitSignal(SignalName.Progress, _appliedTextures, _totalTextureKeys);

        if (_texturesDone && _readyKeys.IsEmpty && !_drainedFinal)
        {
            _drainedFinal = true;
            _registry.ApplyAllAvailable(); // catch-up for any keys never signaled
            Log.Print($"[stream] fully textured in {_coldWatch.Elapsed.TotalMilliseconds:0} ms " +
                $"({_appliedTextures}/{_totalTextureKeys} keys)");
            _finished = true;
            // Deliberately started before, and not awaited by, the finish: the world is already fully
            // textured, so nothing the player sees waits on it. It only holds the load state open, which
            // TryFinalizeLoadState checks for.
            _rededupTask = RededuplicateMaterialsAsync();
            TryFinalizeLoadState();
            EmitFinishedAndReleaseNeededGuids();
            _completion.TrySetResult();
            SetProcess(false);
        }
    }

    // Cold-load epilogue: give the materials the sharing a warm load got for free.
    //
    // Every material this load built was keyed on a texture identity that did not exist yet — the mesh
    // phase runs before the texture phase of the same decode pass — so byte-identical textures produced a
    // material each where a warm load produced one. Now that the textures have landed, resolve the
    // identities for real and point the aliases at one material.
    //
    // The hashing is the whole cost, and it is pure IO+CPU over the texture cache, so it runs on a worker;
    // only the surface reassignment comes back to the main thread.
    private async Task RededuplicateMaterialsAsync()
    {
        try
        {
            var watch = Stopwatch.StartNew();
            string cacheDir = _registry.TextureCacheDir;
            var provisional = new List<string>(_registry.ProvisionalIdentityKeys);
            int before = _materials.Count;
            if (provisional.Count == 0)
            {
                _materials.Release();
                return;
            }

            CancellationToken cancellation = _loadCancellation.Token;
            Dictionary<string, string> resolved = await Task.Run(
                () => TextureIdentity.ResolveAll(cacheDir, provisional, cancellation), cancellation);

            // Back on the main thread (Godot's synchronisation context): everything below touches
            // registry state and mesh resources.
            if (cancellation.IsCancellationRequested)
                return;

            int settled = _registry.ResolvedIdentities(resolved);
            (int repointed, int materials) = _materials.Rededuplicate(_registry);
            _materials.Release();
            Log.Print($"[stream] material re-dedup: {settled} identities settled, {repointed} surfaces "
                + $"re-pointed, {before} -> {materials} material resources, "
                + $"{_registry.MaterialAliasCount} exact texture-key aliases "
                + $"in {watch.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) when (_loadCancellation.IsCancellationRequested)
        {
            // Expected when the map is abandoned while streaming.
        }
        finally
        {
            _materialsSettled = true;
            TryFinalizeLoadState();
        }
    }
}
