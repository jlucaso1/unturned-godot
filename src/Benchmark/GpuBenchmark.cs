using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

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
    private const int SampleFrames = 40;

    public static async Task RunAsync(Node context, string unturnedPath, string mapName)
    {
        SceneTree tree = context.GetTree();
        try
        {
            if (DisplayServer.GetName() == "headless")
            {
                GD.PrintErr("[benchmark] Tier 2 needs a real rendering driver — run windowed (drop --headless).");
                return;
            }

            GD.Print("[benchmark] Tier 2 (GPU, windowed) starting...");
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
            camera.Far = Mathf.Max(bounds.Size.X, bounds.Size.Z) * 3f + 1000f;
            IReadOnlyList<(string name, Transform3D xform)> poses = Poses(bounds);

            var frameMs = new List<double>();
            var drawCalls = new List<double>();
            var primitives = new List<double>();
            var renderObjects = new List<double>();

            foreach ((string name, Transform3D xform) in poses)
            {
                camera.GlobalTransform = xform;
                await WarmupAsync(context, tree);

                // Absorb the post-jump transition (culling recompute, one-off hitches) before sampling,
                // so the frame-time tail (p95/max) reflects steady state rather than the camera move.
                for (int s = 0; s < SettleFrames; s++)
                    await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

                var poseFrameMs = new List<double>();
                ulong last = Time.GetTicksUsec();
                for (int i = 0; i < SampleFrames; i++)
                {
                    await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                    ulong now = Time.GetTicksUsec();
                    poseFrameMs.Add((now - last) / 1000.0);
                    last = now;
                    drawCalls.Add(Mon(Performance.Monitor.RenderTotalDrawCallsInFrame));
                    primitives.Add(Mon(Performance.Monitor.RenderTotalPrimitivesInFrame));
                    renderObjects.Add(Mon(Performance.Monitor.RenderTotalObjectsInFrame));
                }
                frameMs.AddRange(poseFrameMs);
                GD.Print($"[benchmark] pose '{name}': frameMs median {MetricStats.Median(poseFrameMs):0.00}, " +
                    $"drawCalls {Mon(Performance.Monitor.RenderTotalDrawCallsInFrame):0}");
            }

            // Optional visual check: UG_SHOT=<path> saves a PNG from a low near-ground flyover — the only
            // vantage where directional shadows (capped ~100 m from the camera) actually render, so
            // shadow-quality changes (e.g. cascade count) can be compared side by side, not just by timing.
            string shotPath = System.Environment.GetEnvironmentVariable("UG_SHOT") ?? "";
            if (shotPath.Length > 0)
            {
                Vector3 c = bounds.Position + bounds.Size * 0.5f;
                float sea = bounds.Position.Y;
                camera.GlobalTransform = Look(
                    new Vector3(c.X, sea + 45f, c.Z),
                    new Vector3(c.X + 90f, sea + 8f, c.Z + 40f),
                    Vector3.Up);
                for (int i = 0; i < 4; i++)
                    await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                context.GetViewport().GetTexture().GetImage().SavePng(shotPath);
                GD.Print($"[benchmark] screenshot saved: {shotPath}");
            }

            SceneMetricsResult sm = SceneMetrics.Collect(new Node[] { world.Terrain, world.Objects, world.Foliage });
            BenchmarkReport report = BuildReport(mapName, frameMs, drawCalls, primitives, renderObjects, sm, poses.Count);
            BenchmarkRunner.Finish(report, $"{mapName}-gpu", DiffOptions(),
                "gpu.frameMs.* are wall-clock medians — noisy, and CPU-bound when draw-call limited");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[benchmark] Tier 2 failed: {e}");
        }
        finally
        {
            tree.Quit();
        }
    }

    private static BenchmarkReport BuildReport(string mapName, List<double> frameMs, List<double> drawCalls,
        List<double> primitives, List<double> renderObjects, SceneMetricsResult sm, int poseCount) => new()
        {
            Timestamp = BenchmarkRunner.Timestamp(),
            Environment = BenchmarkRunner.BuildEnvironment(mapName),
            Metrics = new SortedDictionary<string, double>(StringComparer.Ordinal)
            {
                // Only the median is reported: at sub-millisecond frames the p95/max tail is dominated by
                // periodic hitches (GC, OS scheduling) that swing ±30% run-to-run, so they produced false
                // regressions. Tail latency would need a longer, steadier harness to mean anything.
                ["gpu.frameMs.median"] = MetricStats.Median(frameMs),
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
                ["samplesPerPose"] = SampleFrames,
            },
        };

    // Wall-clock and memory metrics are jittery run-to-run; loosen their thresholds so noise is not a
    // regression. Counts (draw calls, primitives) are deterministic per view and stay strict.
    private static BaselineDiffOptions DiffOptions() => new()
    {
        ThresholdOverrides = new Dictionary<string, double>
        {
            ["gpu.frameMs.median"] = 0.10,
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
        GD.Print($"[benchmark] shadow: enabled={sun.ShadowEnabled} maxDist={sun.DirectionalShadowMaxDistance:0} " +
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
    private static IReadOnlyList<(string, Transform3D)> Poses(Aabb bounds)
    {
        Vector3 c = bounds.Position + bounds.Size * 0.5f;
        float ext = Mathf.Max(bounds.Size.X, bounds.Size.Z);
        float y = ext * 0.5f;
        // A low pass over one quadrant that only frames a fraction of the map. This is where spatial
        // partitioning pays off (#2): off-screen regions get frustum-culled, so it is the pose that
        // separates "one giant MultiMesh per type" from "one per region".
        Vector3 q = new(c.X - ext * 0.25f, c.Y, c.Z - ext * 0.25f);
        return new List<(string, Transform3D)>
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
