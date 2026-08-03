using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;

namespace UnturnedGodot.Benchmark;

// Tier-2 benchmark: needs a real rendering driver (run windowed, not --headless). Drives the camera
// through a fixed set of poses derived from the scene bounds — reproducible and view-agnostic, so
// view-dependent metrics (draw calls, primitives, frame time) are comparable across runs and not
// cherry-picked. Warmup ends when pipeline compilation counts stop moving (objective, not a guessed
// frame count). Godot exposes no GPU-time API to C#, so frame time is wall-clock (median/p95 over
// sampled frames) and draw calls / primitives / VRAM come from Performance monitors.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class GpuBenchmark
{
    private const int WarmupMaxFrames = 40;
    private const int WarmupStableFrames = 5;
    private const int SettleFrames = 6;   // discarded after a camera jump so transition hitches stay out of the tail
    private const int MinSampleFrames = 40;
    private const ulong MinSampleUsec = 1_000_000; // Performance monitors refresh on a coarse interval

    public static async Task RunAsync(Node context, string unturnedPath, string mapName)
    {
        SceneTree tree = context.GetTree();
        bool failed = true;
        try
        {
            if (DisplayServer.GetName() == "headless")
            {
                Log.PrintErr("[benchmark] Tier 2 needs a real rendering driver — run windowed (drop --headless).");
                return;
            }

            Log.Print("[benchmark] Tier 2 (GPU, windowed) starting...");
            WorldBuildResult world = WorldBuilder.Build(unturnedPath, mapName);
            context.AddChild(world.Terrain);
            context.AddChild(world.Objects);
            context.AddChild(world.Foliage);
            AddEnvironment(context);

            var camera = new Camera3D { Name = "BenchCamera", Current = true };
            context.AddChild(camera);

            // One frame so the render server populates instance buffers and AABBs before we read them.
            await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

            Aabb bounds = SceneBounds(context);
            float sceneFar = Mathf.Max(bounds.Size.X, bounds.Size.Z) * 3f + 1000f;
            IReadOnlyList<(string name, Transform3D xform)> poses = Poses(bounds, world.Heights);

            var perPose = new List<PoseMetrics>();
            var frameMs = new List<double>();
            var processMs = new List<double>();
            var drawCalls = new List<double>();
            var primitives = new List<double>();
            var renderObjects = new List<double>();

            foreach ((string name, Transform3D xform) in poses)
            {
                // Ground poses represent gameplay and must use PlayerCamera's 4 km far plane. The broad
                // overview poses still need the full scene range to keep their whole-map diagnostic value.
                camera.Far = name.StartsWith("ground", StringComparison.Ordinal) ? 4000f : sceneFar;
                camera.GlobalTransform = xform;
                await WarmupAsync(context, tree);

                // Absorb the post-jump transition (culling recompute, one-off hitches) before sampling,
                // so the frame-time tail (p95/max) reflects steady state rather than the camera move.
                for (int s = 0; s < SettleFrames; s++)
                    await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

                var poseFrameMs = new List<double>();
                var poseProcessMs = new List<double>();
                var poseDrawCalls = new List<double>();
                var posePrimitives = new List<double>();
                var poseRenderObjects = new List<double>();
                ulong poseStarted = Time.GetTicksUsec();
                ulong last = poseStarted;
                do
                {
                    await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                    ulong now = Time.GetTicksUsec();
                    poseFrameMs.Add((now - last) / 1000.0);
                    last = now;
                    poseProcessMs.Add(Mon(Performance.Monitor.TimeProcess) * 1000.0);
                    poseDrawCalls.Add(Mon(Performance.Monitor.RenderTotalDrawCallsInFrame));
                    posePrimitives.Add(Mon(Performance.Monitor.RenderTotalPrimitivesInFrame));
                    poseRenderObjects.Add(Mon(Performance.Monitor.RenderTotalObjectsInFrame));
                }
                while (poseFrameMs.Count < MinSampleFrames || Time.GetTicksUsec() - poseStarted < MinSampleUsec);
                frameMs.AddRange(poseFrameMs);
                processMs.AddRange(poseProcessMs);
                drawCalls.AddRange(poseDrawCalls);
                primitives.AddRange(posePrimitives);
                renderObjects.AddRange(poseRenderObjects);
                var metrics = new PoseMetrics(name,
                    MetricStats.Median(poseFrameMs),
                    MetricStats.Median(poseProcessMs),
                    MetricStats.Median(poseDrawCalls),
                    MetricStats.Median(posePrimitives),
                    MetricStats.Median(poseRenderObjects));
                perPose.Add(metrics);
                Log.Print($"[benchmark] pose '{name}': frame {metrics.FrameMs:0.00} ms, " +
                    $"CPU monitor {metrics.ProcessMs:0.00} ms, draws {metrics.DrawCalls:0}, " +
                    $"primitives {metrics.Primitives:0}, objects {metrics.RenderObjects:0}");
            }

            // Snapshot the final sampled pose before an optional screenshot moves the camera elsewhere.
            // Residency metrics therefore describe the same pose regardless of UG_SHOT.
            bool foliageSettled = await FoliageBenchmarkSettling.WaitAsync(context, tree);
            SceneMetricsResult sm = SceneMetrics.Collect(new Node[] { world.Terrain, world.Objects, world.Foliage });
            BenchmarkReport report = BuildReport(mapName, frameMs, processMs, drawCalls, primitives,
                renderObjects, sm, poses.Count, perPose);
            AddFoliageMetrics(tree, report.Metrics, foliageSettled);

            // Optional visual check: UG_SHOT=<path> saves a PNG from a low near-ground flyover — the only
            // vantage where directional shadows (capped ~100 m from the camera) actually render, so
            // shadow-quality changes (e.g. cascade count) can be compared side by side, not just by timing.
            string shotPath = System.Environment.GetEnvironmentVariable("UG_SHOT") ?? "";
            if (shotPath.Length > 0)
            {
                // Capture the exact ground-level benchmark pose. The former bounds-centre shot could land
                // outside the terrain on sparse maps and show no nearby shadow or foliage at all, making
                // visually different settings produce byte-identical "validation" images.
                foreach ((string name, Transform3D xform) in poses)
                    if (name == "ground_diag")
                    {
                        camera.Far = 4000f;
                        camera.GlobalTransform = xform;
                        break;
                    }
                for (int i = 0; i < 20; i++)
                    await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                context.GetViewport().GetTexture().GetImage().SavePng(shotPath);
                Log.Print($"[benchmark] screenshot saved: {shotPath}");
            }

            BenchmarkRunner.Finish(report, $"{mapName}-gpu", DiffOptions(),
                "gpu.frameMs.* are wall-clock medians — noisy, and CPU-bound when draw-call limited; "
                    + "foliage residency counts compare only with foliage.settled=1 on both sides");
            failed = false;
        }
        catch (Exception e)
        {
            Log.PrintErr($"[benchmark] Tier 2 failed: {e}");
        }
        finally
        {
            // Every way out that does not reach Finish leaves no report behind. Quitting zero from here
            // would tell scripts/run-benchmark.sh — and anything automating it — that a measurement was
            // taken, so the wrapper would accept a run that measured nothing.
            tree.Quit(failed ? 1 : 0);
        }
    }

    private readonly record struct PoseMetrics(string Name, double FrameMs, double ProcessMs,
        double DrawCalls, double Primitives, double RenderObjects);

    private static void AddFoliageMetrics(SceneTree tree, SortedDictionary<string, double> metrics,
        bool includeResidencySnapshot)
    {
        foreach (Node node in tree.GetNodesInGroup("foliage_streaming"))
            if (node is FoliageStreamingRenderer foliage)
            {
                metrics["foliage.indexedChunks"] = foliage.IndexedChunks;
                metrics["foliage.indexedInstances"] = foliage.IndexedInstances;
                // Always report what is resident, under its own keys when the queue never drained. See
                // RuntimeBenchmark.AddFoliageMetrics for why the snapshot is no longer withheld and why
                // an unsettled one is kept out of the settled keys' comparison.
                bool settled = includeResidencySnapshot && foliage.IsSettled;
                string state = settled ? "" : "Unsettled";
                metrics["foliage.settled"] = settled ? 1 : 0;
                metrics[$"foliage.residentChunks{state}"] = foliage.ResidentChunks;
                metrics[$"foliage.residentInstances{state}"] = foliage.ResidentInstances;
                metrics[$"foliage.residentBufferBytes{state}"] = foliage.ResidentBufferBytes;
                metrics["foliage.maxQueued"] = foliage.MaximumQueued;
                metrics["foliage.truncatedAdmissions"] = foliage.TruncatedAdmissions;
                metrics["foliage.maxDeferredPrefetch"] = foliage.MaximumDeferredPrefetch;
                metrics["foliage.maxDecodedBytes"] = foliage.MaximumDecodedBytes;
                // Tier 2 builds the world synchronously and jumps the camera between poses, so it never
                // warms: the key is reported anyway so the two tiers' foliage sections stay comparable.
                metrics["foliage.prewarmedChunks"] = foliage.PrewarmedChunks;
                metrics["foliage.prewarm.totalMs"] = foliage.PrewarmTotalMs;
                metrics["foliage.emergencyVisibleLoads"] = foliage.EmergencyVisibleLoads;
                metrics["foliage.emergencyVisible.totalMs"] = foliage.EmergencyVisibleTotalMs;
                metrics["foliage.emergencyVisible.maxMs"] = foliage.EmergencyVisibleMaxMs;
                metrics["foliage.visibleSetMisses"] = foliage.VisibleSetMisses;
                metrics["foliage.retiredChunks"] = foliage.RetiredChunks;
                metrics["foliage.staleResults"] = foliage.StaleResults;
                metrics["foliage.decodeFailures"] = foliage.DecodeFailures;
                break;
            }
    }

    private static BenchmarkReport BuildReport(string mapName, List<double> frameMs, List<double> processMs,
        List<double> drawCalls, List<double> primitives, List<double> renderObjects, SceneMetricsResult sm,
        int poseCount, List<PoseMetrics> perPose)
    {
        var report = new BenchmarkReport
        {
            Timestamp = BenchmarkRunner.Timestamp(),
            Environment = BenchmarkRunner.BuildEnvironment(mapName),
            Metrics = new SortedDictionary<string, double>(StringComparer.Ordinal)
            {
                // Only the median is reported: at sub-millisecond frames the p95/max tail is dominated by
                // periodic hitches (GC, OS scheduling) that swing ±30% run-to-run, so they produced false
                // regressions. Tail latency would need a longer, steadier harness to mean anything.
                ["gpu.frameMs.median"] = MetricStats.Median(frameMs),
                ["cpu.processMonitorMs.median"] = MetricStats.Median(processMs),
                ["gpu.drawCalls.median"] = MetricStats.Median(drawCalls),
                ["gpu.drawCalls.min"] = MetricStats.Min(drawCalls),
                ["gpu.drawCalls.max"] = MetricStats.Max(drawCalls),
                ["gpu.primitives.median"] = MetricStats.Median(primitives),
                ["gpu.primitives.min"] = MetricStats.Min(primitives),
                ["gpu.primitives.max"] = MetricStats.Max(primitives),
                ["gpu.renderObjects.max"] = MetricStats.Max(renderObjects),
                ["gpu.videoMemBytes"] = Mon(Performance.Monitor.RenderVideoMemUsed),
                ["gpu.bufferMemBytes"] = Mon(Performance.Monitor.RenderBufferMemUsed),
                ["gpu.textureMemBytes"] = Mon(Performance.Monitor.RenderTextureMemUsed),
                ["gpu.pipelineCompilations"] = PipelineTotal(),
                ["maxMultiMeshSpread"] = sm.MaxMultiMeshSpread,
                ["poses"] = poseCount,
                ["samplesPerPose.min"] = MinSampleFrames,
                ["sampleDurationMs.min"] = MinSampleUsec / 1000.0,
            },
        };
        // Per-pose medians too: an optimization that helps one view and hurts another vanishes in the
        // aggregate median, so the report keeps each camera's own number diffable.
        foreach (PoseMetrics pose in perPose)
        {
            report.Metrics[$"gpu.frameMs.median.{pose.Name}"] = pose.FrameMs;
            report.Metrics[$"cpu.processMonitorMs.median.{pose.Name}"] = pose.ProcessMs;
            report.Metrics[$"gpu.drawCalls.median.{pose.Name}"] = pose.DrawCalls;
            report.Metrics[$"gpu.primitives.median.{pose.Name}"] = pose.Primitives;
            report.Metrics[$"gpu.renderObjects.median.{pose.Name}"] = pose.RenderObjects;
        }
        return report;
    }

    // Wall-clock and memory metrics are jittery run-to-run; loosen their thresholds so noise is not a
    // regression. Counts (draw calls, primitives) are deterministic per view and stay strict.
    private static BaselineDiffOptions DiffOptions() => new()
    {
        // Losing settlement is a regression, not an improvement: a baseline that drained its queue
        // against a current run that timed out means less of the map was ever submitted.
        HigherIsBetter = new HashSet<string>(StringComparer.Ordinal) { "foliage.settled" },
        ThresholdPrefixOverrides = new Dictionary<string, double>
        {
            ["gpu.frameMs.median."] = 0.10,
            ["cpu.processMonitorMs.median."] = 0.10,
        },
        // See RuntimeBenchmark.DiffOptions: a mid-fill residency snapshot is scheduler-dependent, so two
        // unsettled runs must not classify their difference as a regression or an improvement. The two
        // timing suffixes are wall-clock like everything else here, and Tier 3 already loosens them for
        // its subsystem counters; Tier 2 needs them for the foliage emergency-decode cost.
        ThresholdSuffixOverrides = new Dictionary<string, double>
        {
            ["Unsettled"] = double.PositiveInfinity,
            [".totalMs"] = 0.15,
            [".maxMs"] = 0.15,
        },
        ThresholdOverrides = new Dictionary<string, double>
        {
            ["gpu.frameMs.median"] = 0.10,
            ["cpu.processMonitorMs.median"] = 0.10,
            ["gpu.videoMemBytes"] = 0.05,
            ["gpu.bufferMemBytes"] = 0.05,
            ["gpu.textureMemBytes"] = 0.05,
        },
    };

    private static async Task WarmupAsync(Node context, SceneTree tree)
    {
        int stable = 0;
        double prev = PipelineTotal();
        for (int f = 0; f < WarmupMaxFrames && stable < WarmupStableFrames; f++)
        {
            await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            double now = PipelineTotal();
            stable = now == prev ? stable + 1 : 0;
            prev = now;
        }
    }

    private static double PipelineTotal() =>
        Mon(Performance.Monitor.PipelineCompilationsCanvas) +
        Mon(Performance.Monitor.PipelineCompilationsMesh) +
        Mon(Performance.Monitor.PipelineCompilationsSurface) +
        Mon(Performance.Monitor.PipelineCompilationsDraw) +
        Mon(Performance.Monitor.PipelineCompilationsSpecialization);

    private static double Mon(Performance.Monitor m) => Performance.GetMonitor(m);

    private static float EnvFloat(string name, float fallback) =>
        float.TryParse(System.Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;

    private static int EnvInt(string name, int fallback) =>
        int.TryParse(System.Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;

    private static void AddEnvironment(Node context)
    {
        // Shadow knobs are env-tunable for the #6 sweep: UG_SHADOW (0/1), UG_SHADOW_DIST (max distance,
        // metres), UG_SHADOW_MODE (0=Orthogonal, 2=2 splits, 4=4 splits). Unset = Godot defaults.
        var sun = new DirectionalLight3D
        {
            Name = "BenchSun",
            RotationDegrees = new Vector3(-50, -30, 0),
            ShadowEnabled = EnvInt("UG_SHADOW", 1) != 0,
            // Match DayNightController so Tier 2 measures the shipping shadow workload by default.
            DirectionalShadowMaxDistance = 64f,
            DirectionalShadowSplit1 = 0.25f,
            DirectionalShadowBlendSplits = true,
        };
        float dist = EnvFloat("UG_SHADOW_DIST", -1f);
        if (dist > 0f)
            sun.DirectionalShadowMaxDistance = dist;
        // Default matches the game (2 splits, #6); UG_SHADOW_MODE overrides it for sweeps.
        sun.DirectionalShadowMode = EnvInt("UG_SHADOW_MODE", 2) switch
        {
            0 => DirectionalLight3D.ShadowMode.Orthogonal,
            4 => DirectionalLight3D.ShadowMode.Parallel4Splits,
            _ => DirectionalLight3D.ShadowMode.Parallel2Splits,
        };
        Log.Print($"[benchmark] shadow: enabled={sun.ShadowEnabled} maxDist={sun.DirectionalShadowMaxDistance:0} " +
            $"mode={sun.DirectionalShadowMode}");
        context.AddChild(sun);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
        };
        context.AddChild(new WorldEnvironment { Environment = env, Name = "BenchEnv" });
    }

    // A representative set of camera poses derived from the scene's own bounds: one straight-down
    // overhead plus three obliques from different sides. Content-relative, so it stays fair on any map.
    private static IReadOnlyList<(string, Transform3D)> Poses(Aabb bounds, HeightmapSampler heights)
    {
        Vector3 c = bounds.Position + bounds.Size * 0.5f;
        float ext = Mathf.Max(bounds.Size.X, bounds.Size.Z);
        float y = ext * 0.5f;
        // A low pass over one quadrant that only frames a fraction of the map. This is where spatial
        // partitioning pays off (#2): off-screen regions get frustum-culled, so it is the pose that
        // separates "one giant MultiMesh per type" from "one per region".
        Vector3 q = new(c.X - ext * 0.25f, c.Y, c.Z - ext * 0.25f);
        var poses = new List<(string, Transform3D)>
        {
            // Straight down needs a horizontal up vector (Up would be colinear with the view direction).
            ("overhead", Look(c + new Vector3(0, ext * 0.9f, 0), c, Vector3.Forward)),
            ("oblique_n", Look(c + new Vector3(0, y, ext * 0.55f), c, Vector3.Up)),
            ("oblique_e", Look(c + new Vector3(ext * 0.55f, y, 0), c, Vector3.Up)),
            ("oblique_s", Look(c + new Vector3(0, y, -ext * 0.55f), c, Vector3.Up)),
            ("zoom", Look(q + new Vector3(0, ext * 0.18f, 0), q, Vector3.Forward)),
            // A tight, near-ground view over the quadrant — closer to what a player actually sees, and
            // the strongest case for culling (most of the map is off-screen).
            ("tight", Look(q + new Vector3(0, ext * 0.06f, 0), q, Vector3.Forward)),
        };
        if (TryGroundPoint(bounds, heights, out Vector3 ground))
        {
            // True gameplay-height views. Visibility ranges, foliage chunk culling and near shadows do not
            // participate in the elevated poses above, so without these a foliage "optimization" can look
            // neutral even though it changes the player's frame completely.
            poses.Add(("ground", Look(ground + new Vector3(0, 3f, 0),
                ground + new Vector3(0, 2f, -100f), Vector3.Up)));
            poses.Add(("ground_diag", Look(ground + new Vector3(0, 12f, 0),
                ground + new Vector3(80f, 2f, -80f), Vector3.Up)));
        }
        if (EnvFlag.IsOn(OS.GetEnvironment("UG_FOLIAGE_TRAVERSAL"), whenUnset: false))
            AddFoliageTraversalPoses(poses, bounds, heights);
        return poses;
    }

    private static void AddFoliageTraversalPoses(List<(string, Transform3D)> poses, Aabb bounds,
        HeightmapSampler heights)
    {
        Vector3 centre = bounds.Position + bounds.Size * 0.5f;
        float stepX = bounds.Size.X / 8f;
        float stepZ = bounds.Size.Z / 8f;
        ReadOnlySpan<(int X, int Z)> offsets =
        [
            (-3, -3), (3, 3), (-3, 3), (3, -3),
            (-2, 0), (2, 0), (0, -2), (0, 2),
        ];
        int added = 0;
        foreach ((int ox, int oz) in offsets)
        {
            float x = centre.X + ox * stepX;
            float z = centre.Z + oz * stepZ;
            if (!TerrainCoordinates.TrySampleGodotHeight(heights, x, z, out float y))
                continue;
            var point = new Vector3(x, y + 3f, z);
            poses.Add(($"foliage_traverse_{added}", Look(point, point + new Vector3(0, -1f, -100f),
                Vector3.Up)));
            if (++added == 4)
                break;
        }
    }

    private static bool TryGroundPoint(Aabb bounds, HeightmapSampler heights, out Vector3 point)
    {
        Vector3 centre = bounds.Position + bounds.Size * 0.5f;
        // Search centre-first over a small deterministic grid. Object bounds can extend beyond terrain,
        // particularly on workshop maps, so the geometric centre itself is not guaranteed to be land.
        ReadOnlySpan<(int X, int Z)> offsets =
        [
            (0, 0), (-1, 0), (1, 0), (0, -1), (0, 1),
            (-1, -1), (1, -1), (-1, 1), (1, 1),
            (-2, 0), (2, 0), (0, -2), (0, 2),
        ];
        float step = Mathf.Min(bounds.Size.X, bounds.Size.Z) / 8f;
        foreach ((int ox, int oz) in offsets)
        {
            float x = centre.X + ox * step;
            float z = centre.Z + oz * step;
            if (TerrainCoordinates.TrySampleGodotHeight(heights, x, z, out float y))
            {
                point = new Vector3(x, y, z);
                return true;
            }
        }
        point = default;
        return false;
    }

    private static Transform3D Look(Vector3 pos, Vector3 target, Vector3 up) =>
        new Transform3D(Basis.Identity, pos).LookingAt(target, up);

    private static Aabb SceneBounds(Node root)
    {
        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        WalkBounds(root, ref min, ref max, ref any);
        return any ? new Aabb(min, max - min) : new Aabb(Vector3.Zero, Vector3.One);
    }

    private static void WalkBounds(Node node, ref Vector3 min, ref Vector3 max, ref bool any)
    {
        if (node is VisualInstance3D vi)
        {
            Aabb a = TransformAabb(vi.GlobalTransform, vi.GetAabb());
            Vector3 lo = a.Position;
            Vector3 hi = a.Position + a.Size;
            min = new Vector3(Mathf.Min(min.X, lo.X), Mathf.Min(min.Y, lo.Y), Mathf.Min(min.Z, lo.Z));
            max = new Vector3(Mathf.Max(max.X, hi.X), Mathf.Max(max.Y, hi.Y), Mathf.Max(max.Z, hi.Z));
            any = true;
        }
        foreach (Node child in node.GetChildren())
            WalkBounds(child, ref min, ref max, ref any);
    }

    private static Aabb TransformAabb(Transform3D t, Aabb a)
    {
        Vector3 min = t * a.Position;
        Vector3 max = min;
        for (int i = 1; i < 8; i++)
        {
            var corner = a.Position + new Vector3(
                (i & 1) != 0 ? a.Size.X : 0f,
                (i & 2) != 0 ? a.Size.Y : 0f,
                (i & 4) != 0 ? a.Size.Z : 0f);
            Vector3 p = t * corner;
            min = new Vector3(Mathf.Min(min.X, p.X), Mathf.Min(min.Y, p.Y), Mathf.Min(min.Z, p.Z));
            max = new Vector3(Mathf.Max(max.X, p.X), Mathf.Max(max.Y, p.Y), Mathf.Max(max.Z, p.Z));
        }
        return new Aabb(min, max - min);
    }
}
