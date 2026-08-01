using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot;

// Turns the level's PRE-BAKED navmesh (Environment/Navigation_<N>.dat) into the ZombiePathQuery used by
// the Seeker port. Normal maps use bounded NavigationServer regions and its funnel-quality paths. Very
// large maps use the baked shared-edge graph directly, because Godot's global polygon merge can otherwise
// occupy an engine worker for minutes and prevent shutdown. Both paths work headless.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ZombieNavigation
{
    // Above this size Godot 4.7's global NavMapBuilder merge is unbounded in practice (California2's
    // 266k faces kept one worker busy for minutes). Large maps use the already-baked shared-edge graph
    // directly; smaller maps retain NavigationServer's funnel-quality paths.
    private const int MaxGodotTriangles = 100_000;

    private readonly Rid _map;
    private readonly List<Rid> _regions = new();
    private readonly bool _useBakedGraph;
    private BakedNavGraph? _bakedGraph;
    private BakedNavGraph? _progressGraph; // small-map fallback while NavigationServer synchronizes
    private bool _synced; // the map's FIRST (async) synchronization pass has completed (map_changed)
    private bool _ready;  // a real route resolved: the map actually answers queries
    private bool _published;
    private bool _disposed;
    private Vector3 _probeFrom;
    private Vector3 _probeTo;

    public ZombiePathQuery Query { get; }

    // Polling this also advances the small-map readiness probe. Consumers use it to distinguish a graph
    // that is still being built from a live graph that definitively found no route.
    public bool IsReady => EnsureReady() || (!_useBakedGraph && _progressGraph != null);

    public static ZombieNavigation? Build(IReadOnlyList<NavFlag>? flags)
    {
        if (flags == null || flags.Count == 0)
            return null;
        return new ZombieNavigation(flags, publishImmediately: true);
    }

    // Interactive loading parses the baked data early, but deliberately does not publish it to Godot yet.
    // The old preload created the full map, then ReconcileNavigation threw it away and created it again;
    // California2 consequently had two 250k-polygon synchronizations racing in the engine worker pool.
    private static IReadOnlyList<NavFlag>? PreloadedFlags;
    private static string? PreloadedLevelDir;

    public static void Preload(string levelDir)
    {
        if (PreloadedFlags != null)
            return;
        PreloadedFlags = LevelNavmesh.Load(System.IO.Path.Combine(levelDir, "Environment"));
        PreloadedLevelDir = levelDir;
    }

    public static ZombieNavigation? TakePreloaded()
    {
        IReadOnlyList<NavFlag>? flags = PreloadedFlags;
        string? levelDir = PreloadedLevelDir;
        PreloadedFlags = null;
        PreloadedLevelDir = null;
        return flags is { Count: > 0 }
            ? new ZombieNavigation(flags, publishImmediately: false, levelDir) : null;
    }

    // Drops parsed data nobody claimed. A load that fails between Preload and TakePreloaded must not let
    // the next map's session consume the failed map's navmesh.
    public static void DiscardPreloaded()
    {
        PreloadedFlags = null;
        PreloadedLevelDir = null;
    }

    private readonly string? _levelDir;

    private ZombieNavigation(IReadOnlyList<NavFlag> flags, bool publishImmediately, string? levelDir = null)
    {
        _levelDir = levelDir;
        int totalTriangles = 0;
        foreach (NavFlag flag in flags)
            totalTriangles += flag.Triangles.Length / 3;
        _useBakedGraph = totalTriangles > MaxGodotTriangles;
        _map = _useBakedGraph ? default : NavigationServer3D.MapCreate();
        // Readiness probe: two corners of the first flag's first triangle. map_changed only says the
        // FIRST synchronization pass finished — measured live, real routes still resolve empty for a
        // few more seconds while the edge merge completes, so readiness is only declared once this
        // route actually resolves (probing earlier than map_changed would spam console errors).
        if (flags[0].Triangles.Length >= 3)
        {
            _probeFrom = flags[0].Vertices[flags[0].Triangles[0]];
            _probeTo = flags[0].Vertices[flags[0].Triangles[1]];
        }
        // The pre-baked mesh was recast at cellSize 0.1 (doorways and dense clutter produce edges
        // that close together); the map's default 0.25 rasterization cell collapses distinct edges
        // into the same cell and DROPS their connections — houses lost their doorway link and
        // zombies pushed at windows. Match the map grid to the source data's resolution.
        if (!_useBakedGraph)
        {
            NavigationServer3D.MapSetCellSize(_map, 0.1f);
            NavigationServer3D.MapSetCellHeight(_map, 0.1f);
            NavigationServer3D.MapSetUseAsyncIterations(_map, true);
            NavigationServer3D.MapSetUseEdgeConnections(_map, true);
        }

        _flags = flags;
        if (publishImmediately)
            Publish();

        // The final Godot map synchronizes asynchronously (about 5 s for PEI's 42k triangles). Queries
        // are gated until its regions have iteration IDs; interactive loading serves the progressive
        // CPU graph during that interval, so the brain never falls back to a straight line through walls.
        if (!_useBakedGraph)
            NavigationServer3D.Singleton.MapChanged += OnMapChanged;

        Rid map = _map;
        bool debug = OS.GetEnvironment("NAV_DEBUG") == "1";
        Query = (Vector3 from, Vector3 to, List<Vector3> path) =>
        {
            if (_useBakedGraph)
                return _bakedGraph?.TryPath(from, to, path) == true;

            if (!EnsureReady())
                return _progressGraph?.TryPath(from, to, path) == true;
            _progressGraph = null; // NavigationServer now owns funnel-quality routing
            Vector3[] points = NavigationServer3D.MapGetPath(map, from, to, optimize: true);
            if (points.Length < 2)
                return false; // live graph, no route: the brain keeps its old route or stands
            for (int i = 0; i < points.Length; i++)
                path.Add(points[i]);
            if (debug)
                RecordDetour(from, to, points);
            return true;
        };
    }

    private bool EnsureReady()
    {
        if (_disposed)
            return false;
        if (_useBakedGraph)
            return _ready && _bakedGraph != null;
        if (_ready)
            return true;

        // MapChanged can be emitted for queued changes before every async region builder has landed.
        // Iteration IDs are the server's completion counters; require all of them before issuing the
        // probe query that declares the graph live.
        if (!_published || !_synced || !IterationsReady()
            || NavigationServer3D.MapGetPath(_map, _probeFrom, _probeTo, optimize: true).Length < 2)
            return false;
        _ready = true;
        Log.Print("[nav] navmesh answering queries; zombie pathfinding live");
        return true;
    }

    // NAV_DEBUG=1 diagnostics: how far each route wanders compared with flying straight there. A route
    // that hugs the direct line scores ~1; a zombie sent around the houses scores 2 or 3. Reported as a
    // distribution because the mean alone hides the tail, and it is the tail that reads as "why is it
    // going that way?" in game.
    private static int RouteCount;
    private static double DetourSum;
    private static int Over2;
    private static int Over3;
    private static double Worst;
    private static int SinceReport;

    private static void RecordDetour(Vector3 from, Vector3 to, Vector3[] points)
    {
        float direct = from.DistanceTo(to);
        if (direct < 1f)
            return; // too short for the ratio to mean anything

        float walked = 0f;
        for (int i = 1; i < points.Length; i++)
            walked += points[i - 1].DistanceTo(points[i]);

        double ratio = walked / direct;
        RouteCount++;
        DetourSum += ratio;
        if (ratio > 2.0)
            Over2++;
        if (ratio > 3.0)
            Over3++;
        if (ratio > Worst)
            Worst = ratio;

        if (++SinceReport < 200)
            return;
        SinceReport = 0;
        Log.Print($"[nav] detour over {RouteCount} routes: mean={DetourSum / RouteCount:0.###} " +
            $"worst={Worst:0.##} >2x={Over2} ({100.0 * Over2 / RouteCount:0.#}%) " +
            $">3x={Over3} ({100.0 * Over3 / RouteCount:0.#}%)");
    }

    private IReadOnlyList<NavFlag> _flags = System.Array.Empty<NavFlag>();

    // Publishes the final, reconciled graph once. Each spatial region is capped at a few thousand
    // triangles so Godot's async region builder has bounded jobs instead of one map-sized task that can
    // monopolize a worker through shutdown.
    private void Publish()
    {
        if (_disposed)
            return;

        if (_useBakedGraph)
        {
            var watch = Stopwatch.StartNew();
            _bakedGraph = BakedNavGraph.Build(_flags, _unreachable);
            _published = true;
            _ready = true;
            int graphTriangles = 0, graphDropped = 0;
            foreach (NavFlag flag in _flags)
            {
                graphTriangles += flag.Triangles.Length / 3;
                if (_unreachable.TryGetValue(flag, out HashSet<int>? skip))
                    graphDropped += skip.Count;
            }
            Log.Print($"[nav] large baked graph ready: {graphTriangles - graphDropped} triangles, {graphDropped} dropped "
                + $"in {watch.ElapsedMilliseconds} ms (NavigationServer bypassed)");
            return;
        }

        if (_published)
            NavigationServer3D.MapSetActive(_map, false);
        foreach (Rid old in _regions)
            NavigationServer3D.FreeRid(old);
        _regions.Clear();

        int triangles = 0, dropped = 0;
        foreach (NavFlag flag in _flags)
        {
            _unreachable.TryGetValue(flag, out HashSet<int>? skip);
            dropped += skip?.Count ?? 0;
            foreach (NavmeshRegionData part in NavmeshPartition.Build(flag, skip))
            {
                var mesh = new NavigationMesh { Vertices = part.Vertices };
                for (int i = 0; i + 2 < part.Triangles.Length; i += 3)
                    mesh.AddPolygon(new[] { part.Triangles[i], part.Triangles[i + 1], part.Triangles[i + 2] });

                Rid region = NavigationServer3D.RegionCreate();
                NavigationServer3D.RegionSetUseAsyncIterations(region, true);
                NavigationServer3D.RegionSetUseEdgeConnections(region, true);
                NavigationServer3D.RegionSetNavigationMesh(region, mesh);
                NavigationServer3D.RegionSetMap(region, _map);
                _regions.Add(region);
                triangles += part.SourceTriangleCount;
            }
        }

        _ready = false;
        _synced = false;
        _published = true;
        NavigationServer3D.MapSetActive(_map, true);

        Log.Print(dropped == 0
            ? $"[nav] navmesh up: {_regions.Count} bounded regions, {triangles} triangles (pre-baked)"
            : $"[nav] navmesh reconciled with collision: {dropped} unwalkable triangles dropped, "
              + $"{triangles} kept in {_regions.Count} bounded regions");
    }

    // California2's shared-edge graph takes roughly 100 ms to assemble. Reconciliation itself is
    // cooperative, so doing that final pure-CPU build on the main thread would reintroduce exactly
    // the hitch the frame-budgeted probing avoids (including on a persistent-cache hit).
    private async Task PublishAsync(string? fingerprint = null, string? graphCachePath = null)
    {
        if (!_useBakedGraph)
        {
            Publish();
            return;
        }

        var watch = Stopwatch.StartNew();
        string? cacheWarning = null;
        (BakedNavGraph graph, bool cacheHit) = await Task.Run(() =>
        {
            if (fingerprint != null && graphCachePath != null && System.IO.File.Exists(graphCachePath))
            {
                using System.IO.FileStream input = System.IO.File.OpenRead(graphCachePath);
                if (BakedNavGraph.TryRead(input, fingerprint, _flags, out BakedNavGraph? cached))
                    return (cached!, true);
            }
            BakedNavGraph built = BakedNavGraph.Build(_flags, _unreachable);
            if (fingerprint != null && graphCachePath != null)
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(graphCachePath)!);
                    using System.IO.FileStream output = System.IO.File.Create(graphCachePath);
                    built.Write(output, fingerprint);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                    cacheWarning = e.Message;
                }
            }
            return (built, false);
        });
        if (_disposed || AppShutdown.IsShuttingDown)
            return;
        if (cacheWarning != null)
            Log.PushWarning($"[nav] CSR cache write failed ({cacheWarning})");

        _bakedGraph = graph;
        _published = true;
        _ready = true;
        int graphTriangles = 0, graphDropped = 0;
        foreach (NavFlag flag in _flags)
        {
            graphTriangles += flag.Triangles.Length / 3;
            if (_unreachable.TryGetValue(flag, out HashSet<int>? skip))
                graphDropped += skip.Count;
        }
        Log.Print($"[nav] large baked graph ready: {graphTriangles - graphDropped} triangles, {graphDropped} dropped "
            + $"in {watch.ElapsedMilliseconds} ms off the main thread "
            + $"({(cacheHit ? "CSR cache hit" : "CSR built")}, NavigationServer bypassed)");
    }

    private readonly Dictionary<NavFlag, HashSet<int>> _unreachable = new();

    // Removes the navmesh triangles a body cannot actually reach, using the real collision world.
    //
    // Unturned baked this mesh with a climb tolerance larger than the CharacterController's
    // m_StepOffset (0.5), so it lays navmesh straight over obstacles the body cannot climb — a 1 m
    // window sill gets bridged. The planner then hands a zombie a route through the window; the zombie
    // walks into the sill and stands there for good. Passable to the planner, impassable to the physics.
    //
    // So make the graph agree with the world instead of patching the symptom in the brain. Each face is
    // sampled at several points (the baked triangles are large enough that one can span an opening AND
    // the sill beside it, so the centre alone lies) and the HIGHEST surface under it wins. A face whose
    // ground sits more than a step above the lowest neighbour it shares an edge with cannot be reached
    // from there, so it is not a route. Kerbs and stair treads, well under the step, are untouched.
    //
    // MUST run after the object colliders are in the physics space. Called earlier it measures bare
    // terrain, which silently deletes the wrong tenth of the navmesh — see the ObjectStreamer.Finished
    // wiring in Main.
    public async Task PruneAgainstCollisionAsync(Node owner, PhysicsDirectSpaceState3D space, float stepOffset,
        IReadOnlySet<Guid> colliderGuids)
    {
        // Diagnostic/benchmark aid: isolates NavigationServer publication cost from collision probing.
        if (OS.GetEnvironment("NAV_SKIP_RECONCILE") == "1")
        {
            Publish();
            return;
        }

        string? cachePath = null;
        string? fingerprint = null;
        bool partialCheckpoints = OS.GetEnvironment("UG_PARTIAL_NAV_CACHE") != "0";
        int[] triangleCounts = new int[_flags.Count];
        for (int i = 0; i < _flags.Count; i++)
            triangleCounts[i] = _flags[i].Triangles.Length / 3;
        if (_levelDir != null)
        {
            try
            {
                string modelCache = ProjectSettings.GlobalizePath("user://model_cache");
                string reconcileCache = ProjectSettings.GlobalizePath("user://nav_reconcile");
                (fingerprint, cachePath) = await Task.Run(() =>
                {
                    string fp = NavReconcileCache.Fingerprint(_levelDir, modelCache, colliderGuids);
                    fp = NavReconcileCache.WithStepOffset(fp, stepOffset);
                    return (fp, System.IO.Path.Combine(reconcileCache,
                        NavReconcileCache.MapKey(_levelDir) + ".cache"));
                });
                if (System.IO.File.Exists(cachePath))
                {
                    using System.IO.FileStream input = System.IO.File.OpenRead(cachePath);
                    if (NavReconcileCache.TryReadPartial(input, fingerprint, triangleCounts, out var cached))
                    {
                        int restored = 0;
                        for (int i = 0; i < _flags.Count; i++)
                            if (cached[i] is { } completed)
                            {
                                _unreachable[_flags[i]] = completed;
                                restored++;
                            }
                        if (restored == _flags.Count)
                        {
                            if (!_useBakedGraph)
                                await PublishProgressGraphAsync();
                            await PublishAsync(fingerprint, cachePath + ".csr");
                            Log.Print($"[nav] collision reconciliation cache hit ({fingerprint[..12]})");
                            ReleaseReconciliationState();
                            return;
                        }
                        if (partialCheckpoints && restored > 0)
                            Log.Print($"[nav] resumed collision reconciliation: {restored}/{_flags.Count} flags cached");
                        else
                            _unreachable.Clear();
                    }
                }
            }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
            {
                Log.PushWarning($"[nav] reconciliation cache unavailable; recomputing ({e.Message})");
                cachePath = null;
                fingerprint = null;
            }
        }


        // Always expose navigation before the cooperative collision scan starts. Large maps update this
        // graph in place; small maps use it as a deterministic fallback until the final Godot map syncs.
        await PublishProgressGraphAsync();

        var ray = new PhysicsRayQueryParameters3D { CollisionMask = 1 };
        double frameBudgetMs = OS.GetEnvironment("NAV_RECONCILE_BUDGET_MS") is { Length: > 0 } configured
            ? Math.Max(0.25, configured.ToFloat())
            // The pre-baked graph is usable immediately on every map; reconciliation only refines it
            // against our collision world. A 2 ms "small map" fast path made PEI spend ~2.2 ms of every
            // physics tick here and produced visible tails, while buying no loading correctness. Keep the
            // optional refinement cooperative everywhere and let callers explicitly raise the budget when
            // measuring how quickly it completes.
            : 0.25;
        var total = Stopwatch.StartNew();
        foreach (NavFlag flag in _flags)
        {
            if (_unreachable.ContainsKey(flag))
                continue;

            // PublishProgressGraphAsync, reachability computation and checkpoint persistence can resume
            // on the idle synchronization context. DirectSpaceState queries are only valid from a physics
            // notification when Godot runs physics on a separate thread, so explicitly re-enter one for
            // every batch instead of relying on where the previous await happened to resume.
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (_disposed || AppShutdown.IsShuttingDown)
                return;
            int count = flag.Triangles.Length / 3;
            var surface = new float[count];
            var known = new bool[count];
            var budget = Stopwatch.StartNew();
            long benchmarkStarted = Benchmark.RuntimeCounters.Start();
            for (int triangle = 0; triangle < count; triangle++)
            {
                if (_disposed || AppShutdown.IsShuttingDown)
                    return;

                Vector3 a = flag.Vertices[flag.Triangles[triangle * 3]];
                Vector3 b = flag.Vertices[flag.Triangles[(triangle * 3) + 1]];
                Vector3 c = flag.Vertices[flag.Triangles[(triangle * 3) + 2]];
                float highest = float.MinValue;
                foreach (Vector3 point in NavmeshReachability.SamplePoints(a, b, c))
                {
                    // Look down from above the highest thing the agent could step onto, and stop below
                    // the navmesh so a floor beneath it is never mistaken for this one's ground.
                    ray.From = point + (Vector3.Up * (stepOffset + 1.5f));
                    ray.To = point + Vector3.Down;
                    Godot.Collections.Dictionary hit = space.IntersectRay(ray);
                    if (hit.Count > 0)
                        highest = MathF.Max(highest, ((Vector3)hit["position"]).Y);

                    // A triangle has seven probes. Checking only after all seven let one triangle
                    // overrun the nominal 0.25 ms large-map budget by 2-7x. Yield between probes so
                    // reconciliation remains invisible to the physics-frame tail while preserving
                    // every sample and the exact final reachability decision.
                    if (budget.Elapsed.TotalMilliseconds >= frameBudgetMs)
                    {
                        Benchmark.RuntimeCounters.Record(
                            Benchmark.RuntimeCounters.Counter.NavigationReconcile, benchmarkStarted);
                        await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
                        budget.Restart();
                        benchmarkStarted = Benchmark.RuntimeCounters.Start();
                    }
                }
                if (highest != float.MinValue)
                {
                    surface[triangle] = highest;
                    known[triangle] = true;
                }

            }
            Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.NavigationReconcile,
                benchmarkStarted);

            HashSet<int> unreachable = await Task.Run(
                () => NavmeshReachability.Unreachable(flag, stepOffset, surface, known));
            if (_disposed || AppShutdown.IsShuttingDown)
                return;
            _unreachable[flag] = unreachable;
            (_useBakedGraph ? _bakedGraph : _progressGraph)?.Disable(flag, unreachable);
            if (partialCheckpoints && cachePath != null && fingerprint != null)
                await PersistCheckpointAsync(cachePath, fingerprint, triangleCounts);
        }

        await PublishAsync(fingerprint, cachePath == null ? null : cachePath + ".csr");
        if (cachePath != null && fingerprint != null)
            await PersistCheckpointAsync(cachePath, fingerprint, triangleCounts);
        Log.Print($"[nav] collision reconciliation submitted in {total.ElapsedMilliseconds} ms");
        ReleaseReconciliationState();
    }

    private async Task PublishProgressGraphAsync()
    {
        if ((_useBakedGraph ? _bakedGraph : _progressGraph) != null)
            return;
        BakedNavGraph graph = await Task.Run(() => BakedNavGraph.Build(_flags, _unreachable));
        if (_disposed || AppShutdown.IsShuttingDown)
            return;
        if (_useBakedGraph)
        {
            _bakedGraph = graph;
            _published = true;
            _ready = true;
        }
        else
        {
            _progressGraph = graph;
        }
        Log.Print($"[nav] progressive collision graph ready ({_unreachable.Count}/{_flags.Count} flags reconciled)");
    }

    private bool _checkpointWarningIssued;

    private async Task PersistCheckpointAsync(string cachePath, string fingerprint,
        IReadOnlyList<int> triangleCounts)
    {
        var ordered = new List<HashSet<int>?>(_flags.Count);
        foreach (NavFlag flag in _flags)
            ordered.Add(_unreachable.GetValueOrDefault(flag));
        try
        {
            await Task.Run(() =>
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath)!);
                string temporary = cachePath + ".tmp";
                using (System.IO.FileStream output = System.IO.File.Create(temporary))
                    NavReconcileCache.WritePartial(output, fingerprint, ordered, triangleCounts);
                System.IO.File.Move(temporary, cachePath, overwrite: true);
            });
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            if (!_checkpointWarningIssued)
            {
                _checkpointWarningIssued = true;
                Log.PushWarning($"[nav] reconciliation checkpoint unavailable ({e.Message})");
            }
        }
    }

    // Once Publish has copied the rejected-triangle decisions into either Godot's regions or the
    // immutable CSR graph (and the persistent cache has been written), these sets have no remaining
    // runtime consumer. California2 can hold hundreds of thousands of HashSet entries here otherwise.
    private void ReleaseReconciliationState()
    {
        if (OS.GetEnvironment("UG_KEEP_NAV_RECONCILE_STATE") == "1")
            return;

        int rejected = 0;
        foreach (HashSet<int> indices in _unreachable.Values)
            rejected += indices.Count;
        int flags = _unreachable.Count;
        _unreachable.Clear();
        Log.Print($"[nav] released reconciliation state: {rejected:N0} indices across {flags:N0} flags");
    }

    private bool IterationsReady()
    {
        if (_useBakedGraph)
            return _bakedGraph != null;
        if (NavigationServer3D.MapGetIterationId(_map) == 0)
            return false;
        foreach (Rid region in _regions)
            if (NavigationServer3D.RegionGetIterationId(region) == 0)
                return false;
        return true;
    }

    private void OnMapChanged(Rid map)
    {
        if (map != _map || _synced)
            return;
        _synced = true;
        Log.Print("[nav] navmesh first synchronization done");
    }

    public void Free()
    {
        if (_disposed)
            return;
        _disposed = true;
        _unreachable.Clear();
        if (_useBakedGraph)
        {
            _bakedGraph = null;
            return;
        }
        _progressGraph = null;
        NavigationServer3D.Singleton.MapChanged -= OnMapChanged;
        if (_published)
            NavigationServer3D.MapSetActive(_map, false);
        foreach (Rid region in _regions)
            NavigationServer3D.FreeRid(region);
        _regions.Clear();
        NavigationServer3D.FreeRid(_map);
    }
}
