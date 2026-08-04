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
    private Task? _wideBodyBuild; // one-shot: the CPU graph a body wider than the default needs
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
        bool debug = EnvFlag.IsOn(OS.GetEnvironment("NAV_DEBUG"), whenUnset: false);
        Query = (Vector3 from, Vector3 to, List<Vector3> path, float radius) =>
        {
            if (_useBakedGraph)
                return _bakedGraph?.TryPath(from, to, path, radius) == true;

            // NavigationServer bakes ONE agent radius into its map, so it cannot serve a body wider
            // than the default however the query asks. That is not a corner case: this branch covers
            // every map at or under MaxGodotTriangles, which is most of them, so leaving it would mean
            // a mega reverts to a 0.40 m route the moment the map finishes synchronizing.
            //
            // So the CPU graph is kept rather than discarded once the server is ready, and the wider
            // bodies are routed on it. Collision reconciliation already writes to whichever graph is
            // live, so it does not go stale, and it is only retained on maps small enough that the
            // server took them in the first place.
            bool ready = EnsureReady();
            if (!ready || radius > BakedNavGraph.AgentRadius)
            {
                if (_progressGraph != null)
                    return _progressGraph.TryPath(from, to, path, radius);
                if (!ready)
                    return false;
                // Nothing built to serve the wider body — which is the dedicated-server flow, where the
                // map is published immediately and collision reconciliation never runs, so nothing ever
                // creates the CPU graph and the fallback above has nothing to fall back to. Start it,
                // once, and take the server's default-radius route in the meantime: a route aimed
                // slightly too close beats no route, and the caller keeps its previous one on false.
                EnsureWideBodyGraph();
            }
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

    // What reconciliation has taken out of the graph, for whoever needs to DESCRIBE the graph rather
    // than query it — the repro capture, which has to record the faces a rebuild would restore.
    //
    // Keyed by the flag's index, not the flag. NavFlag is a plain class with reference equality, and
    // the interactive load deserializes the navmesh twice — Preload() here and ZombieWorld.Load() for
    // the simulation — so the instances are not the same objects and a lookup by flag silently misses
    // every time. The index is what both orders agree on, since both read the same file.
    //
    // Held separately from _unreachable because that is working state: it is cleared when
    // reconciliation finishes, including on a cache hit, which is nearly always before anyone captures
    // anything. This is the part that has to outlive it.
    // Empty until the final graph is published, and that is not caution — it is the only honest answer
    // while reconciliation runs. The live graph is being built ONCE and then Disabled face by face; a
    // replay applies what it is given as build-time exclusions, and the two differ at T-junctions,
    // where only a fresh build can stitch a seam a removed face just exposed. A mid-reconciliation dump
    // that carried these would therefore replay over connections the session did not have. Carrying
    // none instead makes it behave exactly like a dump from before this field existed.
    public IReadOnlyDictionary<int, IReadOnlySet<int>> DisabledFaces => _reconciled
        ? _disabledByFlag
        : Empty;

    private static readonly Dictionary<int, IReadOnlySet<int>> Empty = new();

    private readonly Dictionary<int, IReadOnlySet<int>> _disabledByFlag = new();
    private bool _reconciled;

    // Snapshot one flag's disabled set at the moment it is decided, keyed by position in _flags.
    //
    // An EMPTY set removes rather than skips. A partial cache is loaded into here and then, if partial
    // resumption is off, thrown away and every flag recomputed; a flag that came back non-empty from
    // the cache and recomputes to empty would otherwise keep its old entry, and the dump would claim
    // faces the live graph actually has. Last writer wins is the only rule that holds here.
    private void RememberDisabled(NavFlag flag, HashSet<int> unreachable)
    {
        for (int i = 0; i < _flags.Count; i++)
            if (ReferenceEquals(_flags[i], flag))
            {
                if (unreachable.Count == 0)
                    _disabledByFlag.Remove(i);
                else
                    _disabledByFlag[i] = new HashSet<int>(unreachable);
                return;
            }
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
    public async Task PruneAgainstCollisionAsync(Node owner, PhysicsDirectSpaceState3D space,
        float stepOffset, IReadOnlySet<Guid> colliderGuids, CollisionFieldBuilder? collision = null)
    {
        try
        {
            await ReconcileAsync(owner, space, stepOffset, colliderGuids, collision);
        }
        finally
        {
            // What the builder holds is the map's entire collision geometry, recorded during the load
            // purely so this pass could probe it. Reconciliation happens once, so however this ended —
            // finished, cache hit, shutdown, or a failure — nothing is going to ask for it again.
            collision?.Release();
        }
    }

    private async Task ReconcileAsync(Node owner, PhysicsDirectSpaceState3D space, float stepOffset,
        IReadOnlySet<Guid> colliderGuids, CollisionFieldBuilder? collision)
    {
        // Diagnostic/benchmark aid: isolates NavigationServer publication cost from collision probing.
        if (EnvFlag.IsOn(OS.GetEnvironment("NAV_SKIP_RECONCILE"), whenUnset: false))
        {
            Publish();
            return;
        }

        string? cachePath = null;
        string? fingerprint = null;
        bool partialCheckpoints = EnvFlag.IsOn(OS.GetEnvironment("UG_PARTIAL_NAV_CACHE"), whenUnset: true);
        // An audit does not touch the cache at either end. Reading it would return before any comparison
        // happened, and the common run is a warm one — so an audit that honoured the cache would report
        // nothing on exactly the runs someone would use it on. Writing it is worse: an audit replaces
        // every face with the server's own answer, so its verdicts are the OLD algorithm's, and leaving
        // them under the ordinary fingerprint would have the next normal run restore them instead of
        // running the hybrid it is supposed to. A diagnostic measures; it does not leave results behind.
        bool audit = EnvFlag.IsOn(OS.GetEnvironment("UG_NAV_PROBE_AUDIT"), whenUnset: false);
        bool cpuField = collision != null && EnvFlag.IsOn(OS.GetEnvironment("UG_NAV_CPU_PROBE"),
            whenUnset: true);
        // The audit exists to compare the field against the server, so it needs the field whatever
        // UG_NAV_CPU_PROBE says. Left alone, the two flags together would send every flag down the
        // server-only path and report nothing — a diagnostic that silently measures nothing is worse than
        // one that refuses. Where no geometry was recorded there is no field to turn on, so the audit is
        // declined out loud instead.
        if (audit && !cpuField)
        {
            if (collision != null)
            {
                cpuField = true;
                Log.Print("[nav] UG_NAV_PROBE_AUDIT is on, so UG_NAV_CPU_PROBE=0 is ignored for this run");
            }
            else
            {
                audit = false;
                Log.Print("[nav] UG_NAV_PROBE_AUDIT needs a collision field, and this session recorded "
                    + "none — reconciling normally instead");
            }
        }
        _auditing = audit; // read once for the whole pass rather than per flag and per face
        int[] triangleCounts = new int[_flags.Count];
        for (int i = 0; i < _flags.Count; i++)
            triangleCounts[i] = _flags[i].Triangles.Length / 3;
        if (_levelDir != null && !audit)
        {
            try
            {
                string modelCache = ProjectSettings.GlobalizePath("user://model_cache");
                string reconcileCache = ProjectSettings.GlobalizePath("user://nav_reconcile");
                (fingerprint, cachePath) = await Task.Run(() =>
                {
                    string fp = NavReconcileCache.Fingerprint(_levelDir, modelCache, colliderGuids);
                    fp = NavReconcileCache.WithStepOffset(fp, stepOffset);
                    fp = NavReconcileCache.WithProbeSettings(fp, cpuField, ConfirmationMargin);
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
                                RememberDisabled(_flags[i], completed);
                                restored++;
                            }
                        if (restored == _flags.Count)
                        {
                            if (!_useBakedGraph)
                                await PublishProgressGraphAsync();
                            _reconciled = true;
                            await PublishAsync(fingerprint, cachePath + ".csr");
                            Log.Print($"[nav] collision reconciliation cache hit ({fingerprint[..12]})");
                            ReleaseReconciliationState();
                            return;
                        }
                        if (partialCheckpoints && restored > 0)
                            Log.Print($"[nav] resumed collision reconciliation: {restored}/{_flags.Count} flags cached");
                        else
                        {
                            // The snapshots go with it. Everything is about to be recomputed, and until
                            // each flag gets there the live graph has none of these faces disabled —
                            // a capture in that window would record holes that are not in it.
                            _unreachable.Clear();
                            _disabledByFlag.Clear();
                        }
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

        // The CPU copy of the solid world, if the load recorded one. Building it is pure CPU (mostly the
        // per-collider BVHs) so it goes to a worker; a map that did not record one — free-cam, a headless
        // server, an old save path — simply keeps the server-only probing below.
        CollisionField? field = null;
        if (cpuField && collision != null)
        {
            var fieldWatch = Stopwatch.StartNew();
            CollisionFieldBuilder source = collision;
            field = await Task.Run(source.Build);
            source.Release(); // the field owns what it kept; the rest can go now rather than at the end
            if (_disposed || AppShutdown.IsShuttingDown)
                return;
            Log.Print($"[nav] collision field built in {fieldWatch.ElapsedMilliseconds} ms: "
                + $"{field.TileCount} terrain tiles, {field.InstanceCount:N0} collider instances over "
                + $"{field.ShapeCount:N0} shapes ({field.TriangleCount:N0} triangles)");
        }

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
        _probeTally = default;
        var pending = new List<NavFlag>();
        foreach (NavFlag flag in _flags)
            if (!_unreachable.ContainsKey(flag))
                pending.Add(flag);

        // The whole map's sampling and planning in ONE hop to the thread pool.
        //
        // Not for the CPU — that is a fraction of a second for a map (see PerfHarness `navprobe`). For the
        // hop itself. An await inside a Godot signal handler resumes on the engine's synchronization
        // context, which drains once a frame, so every worker round trip costs a whole frame however
        // little work it did. Doing this per flag per phase cost 19 flags x 3 phases x a frame, and on a
        // slow machine that measured 78 seconds of pure waiting against one second of actual probing.
        List<FlagPlan>? plans = null;
        if (field != null)
        {
            var planWatch = Stopwatch.StartNew();
            CollisionField world = field;
            try
            {
                plans = await Task.Run(() => Plan(pending, world, stepOffset));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            _probeTally.SampleMs = planWatch.Elapsed.TotalMilliseconds;
            if (_disposed || AppShutdown.IsShuttingDown)
                return;
        }

        for (int i = 0; i < pending.Count; i++)
        {
            NavFlag flag = pending[i];
            HashSet<int>? unreachable = plans != null
                ? await ConfirmFlagAsync(owner, space, ray, flag, stepOffset, plans[i], frameBudgetMs)
                : await ReconcileFlagOnServerAsync(owner, space, ray, flag, stepOffset, frameBudgetMs);
            if (unreachable == null)
                return; // shutting down
            _unreachable[flag] = unreachable;
            RememberDisabled(flag, unreachable);
            (_useBakedGraph ? _bakedGraph : _progressGraph)?.Disable(flag, unreachable);
            if (partialCheckpoints && cachePath != null && fingerprint != null)
                QueueCheckpoint(cachePath, fingerprint, triangleCounts);
        }

        _reconciled = true;
        await PublishAsync(fingerprint, cachePath == null ? null : cachePath + ".csr");
        if (cachePath != null && fingerprint != null)
            QueueCheckpoint(cachePath, fingerprint, triangleCounts);
        try
        {
            await _checkpoints;
        }
        catch (Exception e)
        {
            // Publication has already happened, so a checkpoint that failed for a reason WriteCheckpoint
            // did not expect costs only the resume optimisation. Letting it escape here would skip the
            // state release below and leave the rejected-triangle sets — hundreds of thousands of entries
            // on a large map — resident for the whole session over a file that did not get written.
            AppShutdown.WarnUnlessQuitting($"[nav] reconciliation checkpoint failed ({e.Message})");
        }
        Log.Print($"[nav] collision reconciliation submitted in {total.ElapsedMilliseconds} ms"
            + (field == null ? " (physics-server probes only)" : $" ({DescribeProbes()})"));
        ReleaseReconciliationState();
    }

    // What the sampling pass worked out about one flag, ready for the physics server to settle the part
    // it could not.
    private readonly record struct FlagPlan(NavmeshSurfaceSampling.FlagSurfaces Sampled,
        HashSet<int> Confirm);

    private List<FlagPlan> Plan(List<NavFlag> flags, CollisionField field, float stepOffset)
    {
        var plans = new List<FlagPlan>(flags.Count);
        foreach (NavFlag flag in flags)
        {
            // Sequential across flags, parallel across each flag's faces: one flag already saturates the
            // machine, and taking them one at a time keeps only one flag's surfaces being written at once.
            NavmeshSurfaceSampling.FlagSurfaces sampled =
                NavmeshSurfaceSampling.Sample(flag, field, stepOffset, AppShutdown.Token);
            HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, stepOffset, sampled.Surface,
                sampled.Known, sampled.Slack, sampled.Uncertain, ConfirmationMargin);
            plans.Add(new FlagPlan(sampled, confirm));
        }
        return plans;
    }

    private ProbeTally _probeTally;
    private bool _auditing;

    private struct ProbeTally
    {
        public long CpuSamples;
        public long CpuUncertain;
        public long ServerSamples;
        public long Confirmed;
        public long Reconfirmed; // faces the post-confirmation rounds added to the planned set
        public long Triangles;
        public long AuditDisagreements;
        public long AuditUnescalated;
        public long AuditVerdictDifferences;
        public long AuditDropped;
        // Where the wall clock actually goes. The physics-frame share is the only part that has to wait
        // for a tick; the rest is worker time and says whether the CPU pass is paying for itself.
        public double SampleMs;
        public double ServerMs;
        public double VerdictMs;
    }

    private string DescribeProbes()
    {
        string audit = _probeTally.AuditDisagreements > 0
            ? $", {_probeTally.AuditDisagreements:N0} audit disagreements "
                + $"({_probeTally.AuditUnescalated:N0} unescalated, "
                + $"{_probeTally.AuditVerdictDifferences:N0} verdict differences over "
                + $"{_probeTally.AuditDropped:N0} dropped faces)"
            : "";
        return $"{_probeTally.CpuSamples:N0} CPU probes, {_probeTally.ServerSamples:N0} on the physics "
            + $"server for {_probeTally.Confirmed:N0}(+{_probeTally.Reconfirmed:N0})/"
            + $"{_probeTally.Triangles:N0} faces ({_probeTally.CpuUncertain:N0} uncertain){audit}; "
            + $"sample+plan {_probeTally.SampleMs:0} ms, server {_probeTally.ServerMs:0} ms, "
            + $"verdict {_probeTally.VerdictMs:0} ms";
    }

    // How far apart two collision implementations are allowed to be before a face's verdict has to be
    // settled by the one that owns the physics. It covers the collision engine's own convex margins and
    // the residual float difference between the two ray solvers; the heightfield's triangulation is not
    // in here because CollisionField measures that per probe and reports it as slack.
    private static float ConfirmationMargin =>
        OS.GetEnvironment("UG_NAV_CONFIRM_MARGIN") is { Length: > 0 } configured
            ? Math.Max(0f, configured.ToFloat()) : 0.05f;

    // Settles one planned flag: re-probe the faces the sampling pass would not decide, then reach the
    // verdict. Everything here except the probing is arithmetic measured in single-digit milliseconds per
    // flag, and a hop to a worker to escape that would cost a whole frame — an order of magnitude more —
    // so it stays inline.
    private async Task<HashSet<int>?> ConfirmFlagAsync(Node owner, PhysicsDirectSpaceState3D space,
        PhysicsRayQueryParameters3D ray, NavFlag flag, float stepOffset, FlagPlan plan,
        double frameBudgetMs)
    {
        int count = flag.Triangles.Length / 3;
        NavmeshSurfaceSampling.FlagSurfaces sampled = plan.Sampled;
        float[] surface = sampled.Surface;
        bool[] known = sampled.Known;
        HashSet<int> confirm = _auditing ? AllTriangles(count) : plan.Confirm;


        float[] before = _auditing ? (float[])surface.Clone() : System.Array.Empty<float>();
        bool[] knownBefore = _auditing ? (bool[])known.Clone() : System.Array.Empty<bool>();

        _probeTally.CpuSamples += sampled.Samples;
        _probeTally.CpuUncertain += sampled.UncertainSamples;
        _probeTally.Triangles += count;
        _probeTally.Confirmed += plan.Confirm.Count;

        // Confirm, then look again, until nothing droppable is left resting on a height the server did not
        // measure. One pass is not enough: replacing a face's surface changes what its NEIGHBOURS compare
        // against, so a face that needed no confirming before the pass can be droppable after it — and
        // dropping it would use its own, still unmeasured, height. Rounds after the first are small (the
        // planned set already contains everything droppable by the sampled surfaces), and the loop ends
        // because the verified set only ever grows.
        var phase = Stopwatch.StartNew();
        var verified = new HashSet<int>();
        HashSet<int> round = confirm;
        while (round.Count > 0)
        {
            if (!await ProbeOnServerAsync(owner, space, ray, flag, stepOffset, round, surface, known,
                    frameBudgetMs))
                return null;
            verified.UnionWith(round);
            round = NavmeshReachability.UnverifiedDrops(flag, stepOffset, surface, known, verified);
            _probeTally.Reconfirmed += round.Count;
        }
        _probeTally.ServerMs += phase.Elapsed.TotalMilliseconds;

        // Judged against the planned set: audit mode confirms every face in round one, so the real pass's
        // later rounds cannot be observed here. ReportAudit replays them itself, against the server
        // answers it now has for every face, so what it compares is the state a normal run would publish.
        if (_auditing)
            ReportAudit(flag, before, knownBefore, surface, known, sampled, plan.Confirm, stepOffset);

        phase.Restart();
        HashSet<int> unreachable = NavmeshReachability.Unreachable(flag, stepOffset, surface, known);
        _probeTally.VerdictMs += phase.Elapsed.TotalMilliseconds;
        return _disposed || AppShutdown.IsShuttingDown ? null : unreachable;
    }

    private static HashSet<int> AllTriangles(int count)
    {
        var all = new HashSet<int>(count);
        for (int t = 0; t < count; t++)
            all.Add(t);
        return all;
    }

    // UG_NAV_PROBE_AUDIT=1: probe every face both ways and say where they part company. This is how the
    // CPU field is validated against the collision world it is standing in for — it is deliberately as
    // slow as the old path, because it does all of the old path's work plus the new.
    //
    // A disagreement is not by itself a defect. The design does not claim the two measurements are equal;
    // it claims that wherever they might differ enough to change a verdict, the server is asked. So the
    // number to watch is the last one: faces the two probes disagreed about that a normal run would NOT
    // have sent to the server. Those, and only those, are places where this could decide differently.
    private void ReportAudit(NavFlag flag, float[] cpuSurface, bool[] cpuKnown, float[] serverSurface,
        bool[] serverKnown, NavmeshSurfaceSampling.FlagSurfaces sampled, HashSet<int> wouldConfirm,
        float stepOffset)
    {
        // What a normal run would actually have had in hand: the CPU sampling everywhere, with the server's
        // answer where the confirmation pass asked for it. Comparing the verdict THAT reaches against the
        // verdict a full server probe reaches is the only comparison that decides whether this pass changes
        // the game — a surface differing where the difference cannot move a face across the step threshold
        // is a difference in a number nobody reads.
        //
        // Including the rounds after the planned set, replayed here against the server's own answers. The
        // real pass keeps confirming until nothing droppable rests on an unmeasured height, and an audit
        // that stopped at the planned set would warn about a state no normal run ever publishes.
        int count = serverSurface.Length;
        float margin = ConfirmationMargin; // an environment read and a parse; not once per face
        var hybridSurface = (float[])cpuSurface.Clone();
        var hybridKnown = (bool[])cpuKnown.Clone();
        var replayed = new HashSet<int>();
        HashSet<int> round = wouldConfirm;
        while (round.Count > 0)
        {
            foreach (int t in round)
            {
                hybridSurface[t] = serverSurface[t];
                hybridKnown[t] = serverKnown[t];
            }
            replayed.UnionWith(round);
            round = NavmeshReachability.UnverifiedDrops(flag, stepOffset, hybridSurface, hybridKnown,
                replayed);
        }
        HashSet<int> hybridDrop = NavmeshReachability.Unreachable(flag, stepOffset, hybridSurface,
            hybridKnown);
        HashSet<int> serverDrop = NavmeshReachability.Unreachable(flag, stepOffset, serverSurface,
            serverKnown);
        int verdictDifferences = 0;
        foreach (int t in hybridDrop)
            if (!serverDrop.Contains(t))
                verdictDifferences++;
        foreach (int t in serverDrop)
            if (!hybridDrop.Contains(t))
                verdictDifferences++;
        _probeTally.AuditVerdictDifferences += verdictDifferences;
        _probeTally.AuditDropped += serverDrop.Count;

        int disagreements = 0, missed = 0, invented = 0, unescalated = 0;
        float worst = 0f;
        int worstTriangle = -1;
        for (int t = 0; t < count; t++)
        {
            bool differs;
            if (cpuKnown[t] != serverKnown[t])
            {
                differs = true;
                if (serverKnown[t])
                    missed++;
                else
                    invented++;
            }
            else if (!serverKnown[t])
            {
                continue;
            }
            else
            {
                float delta = MathF.Abs(cpuSurface[t] - serverSurface[t]);
                differs = delta > margin + sampled.Slack[t];
                if (differs && delta > worst)
                {
                    worst = delta;
                    worstTriangle = t;
                }
            }
            if (!differs)
                continue;
            disagreements++;
            if (!replayed.Contains(t))
                unescalated++;
        }
        _probeTally.AuditDisagreements += disagreements;
        _probeTally.AuditUnescalated += unescalated;
        if (disagreements == 0 && verdictDifferences == 0)
            return;
        string report = $"[nav] probe audit: {disagreements}/{count} faces disagree "
            + $"({missed} the CPU field missed, {invented} it invented, {unescalated} a normal run would "
            + $"not have confirmed); worst height gap {worst:0.###} m at face {worstTriangle}; "
            + $"{verdictDifferences} verdict difference(s) against {serverDrop.Count} server-dropped faces";
        // A measured height that differs is expected — two collision implementations, one margin. A face
        // that would be KEPT here and dropped by the server, or the reverse, is not, and only that earns
        // the warning channel.
        if (verdictDifferences > 0)
            Log.PushWarning(report);
        else
            Log.Print(report);
    }

    // The named faces, re-probed against the real collision world on physics frames, inside the same
    // cooperative budget the whole scan used to run under.
    private async Task<bool> ProbeOnServerAsync(Node owner, PhysicsDirectSpaceState3D space,
        PhysicsRayQueryParameters3D ray, NavFlag flag, float stepOffset, HashSet<int> triangles,
        float[] surface, bool[] known, double frameBudgetMs)
    {
        // DirectSpaceState queries are only valid from a physics notification when Godot runs physics on
        // a separate thread, so explicitly enter one rather than relying on where the last await resumed.
        await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (_disposed || AppShutdown.IsShuttingDown)
            return false;

        var budget = Stopwatch.StartNew();
        long benchmarkStarted = Benchmark.RuntimeCounters.Start();
        foreach (int triangle in triangles)
        {
            if (_disposed || AppShutdown.IsShuttingDown)
                return false;

            Vector3 a = flag.Vertices[flag.Triangles[triangle * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(triangle * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(triangle * 3) + 2]];
            float highest = float.MinValue;
            foreach (Vector3 point in NavmeshReachability.SamplePoints(a, b, c))
            {
                // Look down from above the highest thing the agent could step onto, and stop below
                // the navmesh so a floor beneath it is never mistaken for this one's ground.
                ray.From = point + (Vector3.Up * (stepOffset + NavmeshSurfaceSampling.ProbeHeadroom));
                ray.To = point + (Vector3.Down * NavmeshSurfaceSampling.ProbeReach);
                // Disposed rather than left to the collector. Every probe returns a fresh native
                // Variant dictionary behind a finalizable wrapper, and a reconciliation pass runs
                // hundreds of thousands of them: waiting for finalization made the process hold that
                // churn as native memory it never handed back — a session reconciling PEI grew ~1.6
                // MB of RSS per second for as long as the pass ran, and stopped dead when it did.
                float ground = float.MinValue;
                using (Godot.Collections.Dictionary hit = space.IntersectRay(ray))
                    if (hit.Count > 0)
                        ground = ((Vector3)hit["position"]).Y;
                _probeTally.ServerSamples++;
                if (ground != float.MinValue)
                    highest = MathF.Max(highest, ground);

                // A triangle has seven probes. Checking only after all seven let one triangle
                // overrun the nominal 0.25 ms large-map budget by 2-7x. Yield between probes so
                // reconciliation remains invisible to the physics-frame tail while preserving
                // every sample and the exact final reachability decision.
                if (budget.Elapsed.TotalMilliseconds >= frameBudgetMs)
                {
                    Benchmark.RuntimeCounters.Record(
                        Benchmark.RuntimeCounters.Counter.NavigationReconcile, benchmarkStarted);
                    await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
                    if (_disposed || AppShutdown.IsShuttingDown)
                        return false;
                    budget.Restart();
                    benchmarkStarted = Benchmark.RuntimeCounters.Start();
                }
            }
            surface[triangle] = highest == float.MinValue ? 0f : highest;
            known[triangle] = highest != float.MinValue;
        }
        Benchmark.RuntimeCounters.Record(Benchmark.RuntimeCounters.Counter.NavigationReconcile,
            benchmarkStarted);
        return true;
    }

    // The original path, kept for every flow that never recorded a collision field: probe every face on
    // the physics server, a slice of a tick at a time.
    private async Task<HashSet<int>?> ReconcileFlagOnServerAsync(Node owner,
        PhysicsDirectSpaceState3D space, PhysicsRayQueryParameters3D ray, NavFlag flag, float stepOffset,
        double frameBudgetMs)
    {
        int count = flag.Triangles.Length / 3;
        var surface = new float[count];
        var known = new bool[count];
        _probeTally.Triangles += count;
        _probeTally.Confirmed += count;
        if (!await ProbeOnServerAsync(owner, space, ray, flag, stepOffset, AllTriangles(count), surface,
                known, frameBudgetMs))
            return null;

        HashSet<int> unreachable = await Task.Run(
            () => NavmeshReachability.Unreachable(flag, stepOffset, surface, known));
        return _disposed || AppShutdown.IsShuttingDown ? null : unreachable;
    }

    // Built on first demand rather than at startup. A server that never spawns a body wider than the
    // default pays nothing, and BakedNavGraph.Build over a 100k-face map is far too much to do on the
    // tick that asked for the route — so it goes to a worker and the query that triggered it falls
    // through this once.
    //
    // Reading _unreachable off the tick is safe HERE specifically: this only runs when _progressGraph
    // is null, which in turn only happens on the flow that never reconciles, so the dictionary is empty
    // and nobody is writing to it. The interactive flow builds the graph itself and never reaches this.
    // The assignment is a reference store, which is atomic, and the reader takes whatever it sees.
    private void EnsureWideBodyGraph()
    {
        if (_wideBodyBuild != null || _disposed)
            return;
        _wideBodyBuild = Task.Run(() =>
        {
            BakedNavGraph graph = BakedNavGraph.Build(_flags, _unreachable);
            if (!_disposed && !AppShutdown.IsShuttingDown)
                _progressGraph = graph;
        });
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

    // Every checkpoint the pass produces, written one after another on the pool and awaited once at the
    // end. Awaiting each write where it was queued cost a whole frame apiece, for the same reason the
    // sampling hop did — and unlike the sampling, nothing downstream reads the file back, so there was
    // never anything to wait for. The chain is what keeps the writes ordered, so the last one queued is
    // the one left on disk.
    private Task _checkpoints = Task.CompletedTask;

    private void QueueCheckpoint(string cachePath, string fingerprint,
        IReadOnlyList<int> triangleCounts)
    {
        // Snapshotted here, on the thread that owns the dictionary, rather than inside the write.
        var ordered = new List<HashSet<int>?>(_flags.Count);
        foreach (NavFlag flag in _flags)
            ordered.Add(_unreachable.GetValueOrDefault(flag));
        _checkpoints = _checkpoints.ContinueWith(_ => WriteCheckpoint(cachePath, fingerprint, ordered,
            triangleCounts), TaskScheduler.Default);
    }

    private void WriteCheckpoint(string cachePath, string fingerprint,
        IReadOnlyList<HashSet<int>?> ordered, IReadOnlyList<int> triangleCounts)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(cachePath)!);
            string temporary = cachePath + ".tmp";
            using (System.IO.FileStream output = System.IO.File.Create(temporary))
                NavReconcileCache.WritePartial(output, fingerprint, ordered, triangleCounts);
            System.IO.File.Move(temporary, cachePath, overwrite: true);
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            if (!_checkpointWarningIssued)
            {
                _checkpointWarningIssued = true;
                AppShutdown.WarnUnlessQuitting($"[nav] reconciliation checkpoint unavailable ({e.Message})");
            }
        }
    }

    // Once Publish has copied the rejected-triangle decisions into either Godot's regions or the
    // immutable CSR graph (and the persistent cache has been written), these sets have no remaining
    // runtime consumer. California2 can hold hundreds of thousands of HashSet entries here otherwise.
    private void ReleaseReconciliationState()
    {
        if (EnvFlag.IsOn(OS.GetEnvironment("UG_KEEP_NAV_RECONCILE_STATE"), whenUnset: false))
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
