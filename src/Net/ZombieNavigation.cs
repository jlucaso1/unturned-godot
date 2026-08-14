using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot;

// Turns the level's PRE-BAKED navmesh (Environment/Navigation_<N>.dat) into the ZombiePathQuery used by
// the Seeker port. The source is Recast tile output: adjacent tiles can meet in T-junctions, where one
// side has a vertex in the middle of the other side's edge. Godot's NavigationServer only joins exact
// edge pairs inside a region and therefore severs those surfaces. BakedNavGraph preserves the authored
// topology, including those joins, and works headless.
public sealed class ZombieNavigation
{
    private BakedNavGraph? _bakedGraph;
    private bool _ready;
    private bool _disposed;

    public ZombiePathQuery Query { get; }
    public ZombieNavmeshProject ProjectToSurface { get; }
    public ZombieNavmeshSegmentProbe SupportsLocalSegment { get; }

    public bool IsReady => !_disposed && _ready && _bakedGraph != null;

    // Pooled A* workspaces the graph holds across every flag. Reported by Tier 3 because the pool is
    // never drained: it grows to the concurrency the session actually reached and stays there, and until
    // now the only figure for it was an estimate.
    public int SearchWorkspaceCount => _bakedGraph?.SearchWorkspaceCount ?? 0;

    public static ZombieNavigation? Build(IReadOnlyList<NavFlag>? flags,
        ZombieMoveResolver? moveResolver = null, string? levelDir = null)
    {
        if (flags == null || flags.Count == 0)
            return null;
        return new ZombieNavigation(flags, publishImmediately: true, levelDir, moveResolver);
    }

    // Interactive loading parses the baked data early, but deliberately does not build its graph yet.
    // Reconciliation publishes a progressive graph once the collision world exists, then its final graph.
    private static IReadOnlyList<NavFlag>? PreloadedFlags;
    private static string? PreloadedLevelDir;

    public static void Preload(string levelDir)
    {
        if (PreloadedFlags != null)
            return;
        PreloadedFlags = LevelNavmesh.Load(System.IO.Path.Combine(levelDir, "Environment"));
        PreloadedLevelDir = levelDir;
    }

    public static ZombieNavigation? TakePreloaded(ZombieMoveResolver? moveResolver = null)
    {
        IReadOnlyList<NavFlag>? flags = PreloadedFlags;
        string? levelDir = PreloadedLevelDir;
        PreloadedFlags = null;
        PreloadedLevelDir = null;
        return flags is { Count: > 0 }
            ? new ZombieNavigation(flags, publishImmediately: false, levelDir, moveResolver) : null;
    }

    // Drops parsed data nobody claimed. A load that fails between Preload and TakePreloaded must not let
    // the next map's session consume the failed map's navmesh.
    public static void DiscardPreloaded()
    {
        PreloadedFlags = null;
        PreloadedLevelDir = null;
    }

    private readonly string? _levelDir;
    private readonly BakedNavPortalProbe? _portalProbe;

    private ZombieNavigation(IReadOnlyList<NavFlag> flags, bool publishImmediately,
        string? levelDir = null, ZombieMoveResolver? moveResolver = null)
    {
        _levelDir = levelDir;
        _portalProbe = moveResolver == null
            ? null
            : (from, to, radius) => moveResolver(from, to, radius);
        _flags = flags;
        if (publishImmediately)
            Publish();

        bool debug = EnvFlag.IsOn(OS.GetEnvironment("NAV_DEBUG"), whenUnset: false);
        Query = (Vector3 from, Vector3 to, List<Vector3> path, float radius) =>
        {
            bool found = _bakedGraph?.TryPath(from, to, path, radius) == true;
            if (debug && found)
                RecordDetour(from, to, path);
            return found;
        };
        ProjectToSurface = (Vector3 point, out Vector3 projected) =>
        {
            if (_bakedGraph != null)
                return _bakedGraph.TryProject(point, out projected);
            projected = default;
            return false;
        };
        SupportsLocalSegment = (from, to, radius) =>
            _bakedGraph?.SupportsLocalSegment(from, to, radius) == true;
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

    private static void RecordDetour(Vector3 from, Vector3 to, IReadOnlyList<Vector3> points)
    {
        float direct = from.DistanceTo(to);
        if (direct < 1f)
            return; // too short for the ratio to mean anything

        float walked = 0f;
        for (int i = 1; i < points.Count; i++)
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

    // Publishes the final graph synchronously for flows that do not run interactive reconciliation.
    private void Publish()
    {
        if (_disposed)
            return;

        var watch = Stopwatch.StartNew();
        BakedNavGraph graph = BakedNavGraph.Build(_flags, _unreachable, _portalProbe,
            validateIndexedPortals: true);
        graph.PreserveRuntimeEvidenceFrom(_bakedGraph);
        _bakedGraph = graph;
        _ready = true;
        int graphTriangles = 0, graphDropped = 0;
        foreach (NavFlag flag in _flags)
        {
            graphTriangles += flag.Triangles.Length / 3;
            if (_unreachable.TryGetValue(flag, out HashSet<int>? skip))
                graphDropped += skip.Count;
        }
        Log.Print($"[nav] baked graph ready: {graphTriangles - graphDropped} triangles, "
            + $"{graphDropped} dropped in {watch.ElapsedMilliseconds} ms");
    }

    // California2's shared-edge graph takes roughly 100 ms to assemble. Reconciliation itself is
    // cooperative, so doing that final pure-CPU build on the main thread would reintroduce exactly
    // the hitch the frame-budgeted probing avoids (including on a persistent-cache hit).
    private async Task PublishAsync(string? fingerprint = null, string? graphCachePath = null)
    {
        var watch = Stopwatch.StartNew();
        string? cacheWarning = null;
        (BakedNavGraph graph, bool cacheHit) = await Task.Run(() =>
        {
            if (fingerprint != null && graphCachePath != null && System.IO.File.Exists(graphCachePath))
            {
                if (BakedNavGraph.TryReadFile(graphCachePath, fingerprint, _flags,
                        out BakedNavGraph? cached,
                        _portalProbe, validateIndexedPortals: true))
                    return (cached!, true);
            }
            BakedNavGraph built = BakedNavGraph.Build(_flags, _unreachable, _portalProbe,
                validateIndexedPortals: true);
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

        graph.PreserveRuntimeEvidenceFrom(_bakedGraph);
        _bakedGraph = graph;
        _ready = true;
        int graphTriangles = 0, graphDropped = 0;
        foreach (NavFlag flag in _flags)
        {
            graphTriangles += flag.Triangles.Length / 3;
            if (_unreachable.TryGetValue(flag, out HashSet<int>? skip))
                graphDropped += skip.Count;
        }
        Log.Print($"[nav] baked graph ready: {graphTriangles - graphDropped} triangles, {graphDropped} dropped "
            + $"in {watch.ElapsedMilliseconds} ms off the main thread "
            + $"({(cacheHit ? "CSR cache hit" : "CSR built")})");
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
    // Reconciliation finished AND the graph it produced is the one answering queries.
    public IReadOnlyDictionary<int, IReadOnlySet<int>> DisabledFaces =>
        _reconciled && IsReady ? _disabledByFlag : Empty;

    private static readonly Dictionary<int, IReadOnlySet<int>> Empty = new();

    private readonly Dictionary<int, IReadOnlySet<int>> _disabledByFlag = new();
    private bool _reconciled;

    // Only kept when a capture could ask for it. This doubles the rejected-face storage while
    // reconciliation runs and then holds the copy for the lifetime of the navigation — and
    // ReleaseReconciliationState exists precisely because that set reaches hundreds of thousands of
    // entries on a large map. With REPRO=0 no ReproService is created and nothing can ever read it.
    private static readonly bool CaptureEnabled =
        EnvFlag.IsOn(OS.GetEnvironment("REPRO"), whenUnset: true);

    // Whether DisabledFaces will hold anything at all. Read once at type load, so nothing can turn it on
    // afterwards — which makes it the difference between "this pass pruned nothing" and "this build is
    // not recording what it pruned", and those are the same empty dictionary to anyone reading it.
    internal static bool RecordsDisabledFaces => CaptureEnabled;

    // Snapshot one flag's disabled set at the moment it is decided, keyed by position in _flags.
    //
    // An EMPTY set removes rather than skips. A partial cache is loaded into here and then, if partial
    // resumption is off, thrown away and every flag recomputed; a flag that came back non-empty from
    // the cache and recomputes to empty would otherwise keep its old entry, and the dump would claim
    // faces the live graph actually has. Last writer wins is the only rule that holds here.
    private void RememberDisabled(NavFlag flag, HashSet<int> unreachable)
    {
        if (!CaptureEnabled)
            return;
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
    // the sill beside it, so the centre alone lies). The highest clearance above the authored plane at
    // those exact points wins. A face whose real surface rises more than one step above that plane is
    // not a route; continuous slopes, kerbs and stair treads remain untouched.
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
        // Diagnostic/benchmark aid: isolates graph publication cost from collision probing.
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
                    string fp = NavReconcileCache.Fingerprint(_levelDir, modelCache, colliderGuids,
                        collision?.CollisionPolicies);
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
                            await PublishAsync(fingerprint, cachePath + ".csr");
                            _reconciled = true;
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


        // Always expose navigation before the cooperative collision scan starts, then update it in place
        // as each flag's rejected faces are known.
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
            _bakedGraph?.Disable(flag, unreachable);
            if (partialCheckpoints && cachePath != null && fingerprint != null)
                QueueCheckpoint(cachePath, fingerprint, triangleCounts);
        }

        await PublishAsync(fingerprint, cachePath == null ? null : cachePath + ".csr");
        _reconciled = true;
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
            HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, stepOffset,
                sampled.Clearance, sampled.Known, sampled.Slack, sampled.Uncertain,
                ConfirmationMargin);
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
        float[] clearance = sampled.Clearance;
        bool[] known = sampled.Known;
        HashSet<int> confirm = _auditing ? AllTriangles(count) : plan.Confirm;


        float[] before = _auditing ? (float[])clearance.Clone() : System.Array.Empty<float>();
        bool[] knownBefore = _auditing ? (bool[])known.Clone() : System.Array.Empty<bool>();

        _probeTally.CpuSamples += sampled.Samples;
        _probeTally.CpuUncertain += sampled.UncertainSamples;
        _probeTally.Triangles += count;
        _probeTally.Confirmed += plan.Confirm.Count;

        // Confirm, then enforce the invariant that nothing droppable rests on a clearance the server did
        // not measure. Clearances are face-local, so a confirmation cannot change a neighbour's verdict;
        // retaining the loop keeps that invariant explicit and shared with the audit replay.
        var phase = Stopwatch.StartNew();
        var verified = new HashSet<int>();
        HashSet<int> round = confirm;
        while (round.Count > 0)
        {
            if (!await ProbeOnServerAsync(owner, space, ray, flag, stepOffset, round, clearance, known,
                    frameBudgetMs))
                return null;
            verified.UnionWith(round);
            round = NavmeshReachability.UnverifiedDrops(flag, stepOffset, clearance, known, verified);
            _probeTally.Reconfirmed += round.Count;
        }
        _probeTally.ServerMs += phase.Elapsed.TotalMilliseconds;

        // Judged against the planned set: audit mode confirms every face in round one, so the real pass's
        // later rounds cannot be observed here. ReportAudit replays them itself, against the server
        // answers it now has for every face, so what it compares is the state a normal run would publish.
        if (_auditing)
            ReportAudit(flag, before, knownBefore, clearance, known, sampled, plan.Confirm, stepOffset);

        phase.Restart();
        HashSet<int> unreachable = NavmeshReachability.Unreachable(flag, stepOffset, clearance, known);
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
    private void ReportAudit(NavFlag flag, float[] cpuClearance, bool[] cpuKnown,
        float[] serverClearance, bool[] serverKnown, NavmeshSurfaceSampling.FlagSurfaces sampled,
        HashSet<int> wouldConfirm, float stepOffset)
    {
        // What a normal run would actually have had in hand: the CPU sampling everywhere, with the server's
        // answer where the confirmation pass asked for it. Comparing the verdict THAT reaches against the
        // verdict a full server probe reaches is the only comparison that decides whether this pass changes
        // the game — a clearance differing where the difference cannot move a face across the step threshold
        // is a difference in a number nobody reads.
        //
        // Including the rounds after the planned set, replayed here against the server's own answers. The
        // real pass keeps confirming until nothing droppable rests on an unmeasured clearance, and an audit
        // that stopped at the planned set would warn about a state no normal run ever publishes.
        int count = serverClearance.Length;
        float margin = ConfirmationMargin; // an environment read and a parse; not once per face
        var hybridClearance = (float[])cpuClearance.Clone();
        var hybridKnown = (bool[])cpuKnown.Clone();
        var replayed = new HashSet<int>();
        HashSet<int> round = wouldConfirm;
        while (round.Count > 0)
        {
            foreach (int t in round)
            {
                hybridClearance[t] = serverClearance[t];
                hybridKnown[t] = serverKnown[t];
            }
            replayed.UnionWith(round);
            round = NavmeshReachability.UnverifiedDrops(flag, stepOffset, hybridClearance, hybridKnown,
                replayed);
        }
        HashSet<int> hybridDrop = NavmeshReachability.Unreachable(flag, stepOffset, hybridClearance,
            hybridKnown);
        HashSet<int> serverDrop = NavmeshReachability.Unreachable(flag, stepOffset, serverClearance,
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
                float delta = MathF.Abs(cpuClearance[t] - serverClearance[t]);
                // RequiredClimb may difference two samples, each of which can drift by the margin and
                // measured heightfield slack in the opposite direction.
                differs = delta > 2f * (margin + sampled.Slack[t]);
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
            + $"not have confirmed); worst clearance gap {worst:0.###} m at face {worstTriangle}; "
            + $"{verdictDifferences} verdict difference(s) against {serverDrop.Count} server-dropped faces";
        // A measured clearance that differs is expected — two collision implementations, one margin. A face
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
        float[] clearance, bool[] known, double frameBudgetMs)
    {
        // DirectSpaceState queries are only valid from a physics notification when Godot runs physics on
        // a separate thread, so explicitly enter one rather than relying on where the last await resumed.
        await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.PhysicsFrame);
        if (_disposed || AppShutdown.IsShuttingDown)
            return false;

        var budget = Stopwatch.StartNew();
        long benchmarkStarted = Benchmark.RuntimeCounters.Start();
        var surfacePoints = new Vector3[7];
        var surfaceY = new float[7];
        foreach (int triangle in triangles)
        {
            if (_disposed || AppShutdown.IsShuttingDown)
                return false;

            Vector3 a = flag.Vertices[flag.Triangles[triangle * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(triangle * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(triangle * 3) + 2]];
            float highestClearance = float.MinValue;
            int hitSamples = 0;
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
                {
                    float sampleClearance = ground - point.Y;
                    highestClearance = MathF.Max(highestClearance, sampleClearance);
                    surfacePoints[hitSamples] = point;
                    surfaceY[hitSamples++] = ground;
                }

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
            clearance[triangle] = highestClearance == float.MinValue ? 0f
                : NavmeshReachability.RequiredClimbForFace(a, b, c, stepOffset, highestClearance,
                    surfacePoints.AsSpan(0, hitSamples), surfaceY.AsSpan(0, hitSamples));
            known[triangle] = highestClearance != float.MinValue;
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
        var clearance = new float[count];
        var known = new bool[count];
        _probeTally.Triangles += count;
        _probeTally.Confirmed += count;
        if (!await ProbeOnServerAsync(owner, space, ray, flag, stepOffset, AllTriangles(count), clearance,
                known, frameBudgetMs))
            return null;

        HashSet<int> unreachable = await Task.Run(
            () => NavmeshReachability.Unreachable(flag, stepOffset, clearance, known));
        return _disposed || AppShutdown.IsShuttingDown ? null : unreachable;
    }

    private async Task PublishProgressGraphAsync()
    {
        if (_bakedGraph != null)
            return;
        BakedNavGraph graph = await Task.Run(
            () => BakedNavGraph.Build(_flags, _unreachable, _portalProbe,
                validateIndexedPortals: true));
        if (_disposed || AppShutdown.IsShuttingDown)
            return;
        _bakedGraph = graph;
        _ready = true;
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

    // Once Publish has copied the rejected-triangle decisions into the CSR graph (and the persistent
    // cache has been written), these sets have no remaining runtime consumer. California2 can hold
    // hundreds of thousands of HashSet entries here otherwise.
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

    public void Free()
    {
        if (_disposed)
            return;
        _disposed = true;
        _unreachable.Clear();
        _bakedGraph = null;
        _ready = false;
    }
}
