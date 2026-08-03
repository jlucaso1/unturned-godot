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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public partial class ObjectStreamer : Node
{
    [Signal] public delegate void MeshesReadyEventHandler(double elapsedMs);
    [Signal] public delegate void ProgressEventHandler(int applied, int total);
    [Signal] public delegate void FinishedEventHandler();

    // GPU upload budget per frame while streaming. The ceiling is what measured well on a fast machine;
    // the live budget is clamped down from it by the frame time actually being achieved, so a machine that
    // drops to 20 fps backs off and hands the frame to the game rather than to texture streaming.
    private const double MaxApplySecondsPerFrame = 0.008;
    private const double MinApplySecondsPerFrame = 0.002;
    private double _frameSeconds;

    private IReadOnlyList<ContentSource> _sources = System.Array.Empty<ContentSource>();
    private string _cacheDir = "";
    private string _textureCacheDir = "";
    private LevelInfo _level = null!;

    private TextureRegistry _registry = null!;
    private List<PlacedObject> _objects = new();
    private ObjectAssetDatabase _db = null!;
    private Dictionary<Guid, FoliageAsset.Owned> _foliageAssets = new();
    private LevelFoliageChunks? _foliage;
    private FoliageResidencyIndex? _foliageIndex;
    // Held only between building the scene and warming the spawn ring; the tree owns the node itself.
    private FoliageStreamingRenderer? _foliageRenderer;

    // Every GUID this map needs a mesh for: placed objects, trees and the resolved foliage types. Drives
    // both the cold-load check and which slice of the shared cache is realised.
    private HashSet<Guid> _neededGuids = new();
    public IReadOnlySet<Guid> NeededGuids => _neededGuids;
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

    // The terrain's splat layer textures live in the same bundles as the objects, so this pass produces
    // them too and the terrain build takes them from here instead of decoding everything a second time.
    private readonly TaskCompletionSource<IReadOnlyDictionary<Guid, CachedTexture>> _layerTextures =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<Guid, CachedTexture> _layersProduced = new();
    private Dictionary<string, TerrainLayerPlan.BundleWants> _layerWants = new();
    private int _layersOutstanding;

    // Completes with every layer texture this pass decoded (empty when the cache already had them all, or
    // when nothing was decoded). Always completes: the terrain build waits on it.
    public Task<IReadOnlyDictionary<Guid, CachedTexture>> LayerTextures => _layerTextures.Task;

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
        _cacheDir = ProjectSettings.GlobalizePath("user://model_cache");
        _textureCacheDir = ProjectSettings.GlobalizePath("user://texture_cache");
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

    private void LoadPlacements()
    {
        // Three independent reads, and the bundle decode cannot start until all of them are in: the
        // placements (a hundred thousand of them on a workshop map), the asset database (thousands of
        // .dat files) and the foliage. Running them together is what lets the decode start sooner.
        Task placements = Task.Run(() =>
        {
            _objects = LevelObjects.Load(_level.ObjectsDat);
            List<PlacedTree> trees = LevelTrees.Load(Path.Combine(_level.Path, "Terrain", "Trees.dat"));
            foreach (PlacedTree t in trees)
                _objects.Add(new PlacedObject(t.Position, t.EulerDegrees, t.Scale, 0, t.Guid));
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

        _neededGuids = new HashSet<Guid>();
        foreach (PlacedObject o in _objects)
            _neededGuids.Add(o.Guid);
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
        var meshLibrary = await ModelLibrary.LoadStagedAsync(_cacheDir, _registry, this, _neededGuids);
        Log.Print($"[stream] meshes realised: {phase.ElapsedMilliseconds} ms ({meshLibrary.Count})");
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        Dictionary<Guid, ArrayMesh> lod1Library = await LoadLod1LibraryAsync();

        phase.Restart();
        BuildObjects(meshLibrary, lod1Library);
        Log.Print($"[stream] scene built: {phase.ElapsedMilliseconds} ms");
        // Before the yield, not after it. The build above put the streaming foliage in the tree, so the
        // very next frame is the one whose _Process runs its first plan — and takes the whole spawn ring
        // synchronously. Warming has to claim that frame first or there is nothing left to warm.
        await PrewarmFoliageAsync();
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        phase.Restart();
        _registry.ApplyAllAvailable();
        Log.Print($"[stream] textures applied: {phase.ElapsedMilliseconds} ms");
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

    // The one-time load allocates large transient buffers — mesh-cache reads, the foliage transform pool,
    // native Godot.Collections.Array copies, and on a cold load the ~1.4 GB masterbundle decode. The
    // workstation GC holds those freed segments rather than returning them to the OS, so steady-state RSS
    // stays inflated long after load. Force one compacting collection (including the large-object heap) once
    // load has settled to hand the memory back. One-time cost, off the steady frame loop.
    private static void ReclaimLoadMemory()
    {
        var sw = Stopwatch.StartNew();
        long before = ProcessRssMib();
        int passes = int.TryParse(System.Environment.GetEnvironmentVariable("UG_RECLAIM_PASSES"), out int configured)
            ? Math.Clamp(configured, 1, 2)
            : 1;
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        if (passes == 2)
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        if (before > 0) // Linux only (from /proc); skip the line where RSS is unavailable
            Log.Print($"[stream] post-load reclaim: RSS {before} -> {ProcessRssMib()} MB " +
                $"(managed {GC.GetTotalMemory(false) >> 20} MB, {passes} pass(es)) " +
                $"in {sw.ElapsedMilliseconds} ms");
    }

    // VmRSS from /proc/self/status in MiB (Linux); 0 elsewhere. Diagnostic only.
    private static long ProcessRssMib()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    return long.Parse(line.Split(':')[1].Trim().Split(' ')[0]) / 1024;
        }
        catch (Exception) { /* non-Linux or unreadable — diagnostic only */ }
        return 0;
    }

    // The authored lower levels go through the same staged realise as the base library, on both the warm
    // and the cold path, or half of a map's object geometry blocks the main thread in one frame and
    // freezes the loading animation.
    private Task<Dictionary<Guid, ArrayMesh>> LoadLod1LibraryAsync() =>
        ObjectsBuilder.ObjectLodEnabled
            ? ModelLibrary.LoadStagedAsync(_cacheDir, _registry, this, _neededGuids,
                ModelExtractor.Lod1Suffix)
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
            lod1Library);
        if (lod1Library.Count > 0)
            Log.Print($"[stream] object LOD levels: {lod1Library.Count} of {meshLibrary.Count} meshes "
                + "have an authored lower level");
        double buildMs = stage.Elapsed.TotalMilliseconds;
        stage.Restart();
        AddChild(root);
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
        Log.Print($"[stream] built {withMesh}/{_objects.Count} objects ({meshLibrary.Count} meshes), " +
            $"{_totalTextureKeys} texture keys pending");

        // These parsed inputs are consumed only up to here — the MultiMesh buffers now hold their own
        // copies and the streaming worker already captured _db by value. Drop them so the ~32 MB foliage
        // transform graph and the placement/asset lists don't live on this node for the whole session.
        _foliage = null;
        _foliageIndex = null;
        _objects = null!;
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
        await foliage.PrewarmAsync();
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
                    AppShutdown.PrintUnlessQuitting($"[stream] decoding {plan.Source.Name} ({megabytes} MB) for "
                        + $"{plan.Missing.Count} meshes, {plan.MissingTextures.Count} textures and "
                        + $"{layers.ByContainerPath.Count} terrain layers…");
                    ModelExtractor.StreamExtract(plan.Source.BundlePath, plan.Source.CacheTag,
                        plan.Source.ObjectsDir, plan.Source.TreesDir, plan.Source.AssetsDir,
                        plan.Needed, _cacheDir, _textureCacheDir, _db,
                        onMeshesReady: () =>
                        {
                            if (Interlocked.Decrement(ref meshPhasesLeft) == 0)
                                DeferUnlessStopped(OnMeshPhaseDone);
                        },
                        onTextureWritten: key => _readyKeys.Enqueue(key),
                        foliageAssets: plan.Foliage, isCoreBundle: plan.Source.IsCore,
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

    // Final release is gated by both consumers: Finished says the main-thread scene no longer needs the
    // preparation graph; _texturesDone says no decoder can still be reading it. This also handles the warm
    // layer-only path, where Finished may precede the background bundle pass.
    private void TryFinalizeLoadState()
    {
        if (_loadStateReleased || !_finished || (_streamStarted && !_texturesDone))
            return;
        _loadStateReleased = true;

        int retainedItems = _sources.Count + _plans.Count + _neededGuids.Count + _layerWants.Count +
            _layersProduced.Count + _foliageAssets.Count + _readyKeys.Count;
        int registryEntries = _registry.ReleaseLoadingIndexes();
        _sources = Array.Empty<ContentSource>();
        _plans.Clear();
        _plans = new List<ContentExtraction.BundlePlan>();
        _layerWants.Clear();
        _layerWants = new Dictionary<string, TerrainLayerPlan.BundleWants>();
        _layersProduced.Clear();
        while (_readyKeys.TryDequeue(out _)) { }
        _foliageAssets.Clear();
        _foliageAssets = new Dictionary<Guid, FoliageAsset.Owned>();
        _foliage = null;
        _foliageIndex = null;
        _objects = null!;
        _db = null!;
        _level = null!;
        _cacheDir = "";
        _textureCacheDir = "";
        _prepTask = Task.CompletedTask;
        Log.Print($"[stream] released loading state: {retainedItems} items + " +
            $"{registryEntries} texture-index entries");
        ReclaimLoadMemory();
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
        Dictionary<Guid, ArrayMesh> meshLibrary = ModelLibrary.Load(_cacheDir, _registry, _neededGuids);

        Dictionary<Guid, ArrayMesh> lod1Library = await LoadLod1LibraryAsync();

        // The staged realise above yields, so a cancel can land mid-flight. Returning here leaves the
        // scene unbuilt rather than attaching it to a node already on its way out.
        if (_loadCancellation.IsCancellationRequested)
            return;

        BuildObjects(meshLibrary, lod1Library);
        _sceneBuilt = true;
        // Warmed before time-to-playable is read, and counted in it: a world handed over with its spawn
        // ring undecoded is not yet playable without the burst this replaces.
        await PrewarmFoliageAsync();
        // Read after the build, not before it: this is reported as time-to-playable, and the staged
        // realise above happens while the player is still waiting.
        double meshMs = _coldWatch.Elapsed.TotalMilliseconds;

        // Whatever the decode already produced goes on before the world is shown. Applying a texture
        // changes the material's shader key (an albedo map appears, the filter may switch to nearest, a
        // cutout swaps shader), so a material that reaches the scene bare and is textured a frame later
        // makes the renderer build its pipelines twice. Everything still arriving keeps streaming through
        // _Process; this only closes the gap for what was ready all along, at a cost of tens of ms.
        _appliedTextures += _registry.ApplyAllAvailable();

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
            TryFinalizeLoadState();
            EmitFinishedAndReleaseNeededGuids();
            _completion.TrySetResult();
            SetProcess(false);
        }
    }
}
