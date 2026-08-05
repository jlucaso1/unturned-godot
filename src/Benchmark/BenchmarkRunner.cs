using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace UnturnedGodot.Benchmark;

// Self-contained Tier-1 (structural) benchmark: build the real world via WorldBuilder, walk it for
// deterministic structural metrics, print a report, and diff against a committed baseline. Runs in one
// process under `godot --headless -- --benchmark`; add `--write-baseline` to (re)capture the baseline.
// Shared report plumbing (environment, baseline write/diff) is reused by the Tier-2 GPU benchmark.
public static class BenchmarkRunner
{
    private const string BaselineDir = "res://bench/baseline";
    private const string LatestDir = "user://bench";

    public static void Run(Node context, string unturnedPath, string mapName)
    {
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--profile-loop") >= 0)
        {
            ProfileLoop(unturnedPath, mapName);
            return;
        }

        Log.Print("[benchmark] Tier 1 (structural) starting...");
        WorldBuildResult world = WorldBuilder.Build(unturnedPath, mapName);
        context.AddChild(world.Terrain);
        context.AddChild(world.Objects);
        context.AddChild(world.Vehicles);
        context.AddChild(world.Foliage); // in the tree like the game runs it — and freed with the scene

        SceneMetricsResult m = SceneMetrics.Collect(
            new Node[] { world.Terrain, world.Objects, world.Vehicles, world.Foliage });
        BenchmarkReport report = BuildStructuralReport(m, world, mapName);

        Finish(report, mapName, new BaselineDiffOptions
        {
            // Build timings are single-sample and jittery; give them a loose threshold so run-to-run
            // noise is not flagged as a regression. Structural metrics stay strict (they are exact).
            ThresholdOverrides = new Dictionary<string, double>
            {
                ["build.terrain.ms"] = 0.15,
                ["build.objects.ms"] = 0.15,
                ["build.total.ms"] = 0.15,
            },
        }, "*.ms are advisory — noisy single samples");
    }

    private static BenchmarkReport BuildStructuralReport(SceneMetricsResult m, WorldBuildResult w, string mapName)
    {
        BenchmarkEnvironment env = BuildEnvironment(mapName);
        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal)
        {
            ["nodes"] = m.Nodes,
            ["meshInstances"] = m.MeshInstances,
            ["multiMeshInstances"] = m.MultiMeshInstances,
            ["multiMeshTotalInstances"] = m.MultiMeshTotalInstances,
            ["uploadedVertices"] = m.UploadedVertices,
            ["uploadedIndices"] = m.UploadedIndices,
            ["uploadedTriangles"] = m.UploadedTriangles,
            ["estimatedGeometryBytes"] = GeometryBytes.Estimate(m.UploadedVertices, m.UploadedIndices),
            ["uniqueMeshes"] = m.UniqueMeshes,
            ["uniqueMaterials"] = m.UniqueMaterials,
            ["tileCount"] = w.TileCount,
            ["placedObjects"] = w.PlacedObjectCount,
            ["objectsWithMesh"] = w.ObjectsWithMesh,
            ["build.terrain.ms"] = w.TerrainMs,
            ["build.objects.ms"] = w.ObjectsMs,
            ["build.total.ms"] = w.TerrainMs + w.ObjectsMs,
        };

        // MultiMesh instance transforms are only stored by a real rendering driver; under the headless
        // dummy driver they read back as identity, so the spread would be a misleading 0. Emit it only
        // when a real driver is active (Tier 2 windowed run).
        if (!env.Headless)
            metrics["maxMultiMeshSpread"] = m.MaxMultiMeshSpread;

        return new BenchmarkReport { Timestamp = Timestamp(), Environment = env, Metrics = metrics };
    }

    // Rebuilds the world in a tight loop, keeping the process alive, so an external sampling profiler can
    // attach and stop WHILE it runs. dotnet-trace corrupts traces when the target exits abruptly (which
    // Godot's native Quit does), so the scenario must stay live across the profiler's stop. Usage:
    //   godot --headless --path . -- --benchmark --profile-loop &
    //   dotnet-trace collect -p <pid> --format speedscope --duration 00:00:08   # stops while still looping
    private static void ProfileLoop(string unturnedPath, string mapName)
    {
        double seconds = double.TryParse(System.Environment.GetEnvironmentVariable("UG_PROFILE_SECS"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double s) ? s : 30.0;
        Log.Print($"[benchmark] profile-loop: rebuilding world for {seconds:0}s — attach dotnet-trace now, " +
            "stop it BEFORE this ends (a clean trace needs the process still alive at stop).");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int i = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            WorldBuildResult w = WorldBuilder.Build(unturnedPath, mapName);
            w.Terrain.Free();
            w.Objects.Free();
            w.Vehicles.Free();
            w.Foliage.Free(); // leaking this left ~1k MultiMesh RIDs per rebuild, poisoning long traces
            i++;
            Log.Print($"[benchmark] profile-loop: build {i} done at {sw.Elapsed.TotalSeconds:0.0}s");
        }
        Log.Print($"[benchmark] profile-loop: {i} builds in {sw.Elapsed.TotalSeconds:0.0}s");
    }

    internal static BenchmarkEnvironment BuildEnvironment(string mapName)
    {
        bool headless = DisplayServer.GetName() == "headless";

        // A real rendering driver reports the adapter (e.g. "AMD Radeon RX 6600 (RADV NAVI23)"). The
        // headless dummy driver initializes no GPU, so the name comes back empty — label it explicitly
        // instead of leaving a blank that reads like a bug.
        string gpu = RenderingServer.GetVideoAdapterName();
        if (string.IsNullOrEmpty(gpu))
            gpu = headless ? "(none — headless)" : "(unknown)";

        return new BenchmarkEnvironment
        {
            GodotVersion = (string)Engine.GetVersionInfo()["string"],
            Os = OS.GetName(),
            Gpu = gpu,
            RenderingDriver = RenderingServer.GetCurrentRenderingDriverName(),
            RenderingMethod = RenderingServer.GetCurrentRenderingMethod(),
            Headless = headless,
            Scene = mapName,
        };
    }

    internal static string Timestamp() => Time.GetDatetimeStringFromSystem(utc: true);

    // Writes the latest report, and either (re)captures the baseline (with --write-baseline) or diffs
    // against it. baselineKey names the baseline file, so Tier 1 and Tier 2 keep separate baselines.
    internal static void Finish(BenchmarkReport report, string baselineKey, BaselineDiffOptions diffOptions, string diffNote)
    {
        string json = report.ToJson();
        Write(GlobalPath($"{LatestDir}/{baselineKey}-latest.json"), json);
        Log.Print($"[benchmark] Report:\n{json}");

        bool writeBaseline = Array.IndexOf(OS.GetCmdlineUserArgs(), "--write-baseline") >= 0;
        string baselinePath = GlobalPath($"{BaselineDir}/{baselineKey}.json");

        if (writeBaseline)
        {
            Write(baselinePath, json);
            Log.Print($"[benchmark] Baseline saved to {BaselineDir}/{baselineKey}.json");
            return;
        }

        if (!System.IO.File.Exists(baselinePath))
        {
            Log.Print($"[benchmark] No baseline at {BaselineDir}/{baselineKey}.json — " +
                "run once with `--write-baseline` to capture one.");
            return;
        }

        BenchmarkReport? baseline = BenchmarkReport.FromJson(System.IO.File.ReadAllText(baselinePath));
        if (baseline is null)
        {
            Log.PrintErr("[benchmark] Baseline file is unreadable; skipping diff.");
            return;
        }

        PrintDiff(baseline, report, diffOptions, diffNote);
    }

    private static void PrintDiff(BenchmarkReport baseline, BenchmarkReport current, BaselineDiffOptions options, string note)
    {
        if (baseline.Environment != current.Environment)
            Log.Print("[benchmark] WARNING: environment differs from baseline " +
                $"(baseline {baseline.Environment.RenderingDriver}/{baseline.Environment.Gpu} vs " +
                $"current {current.Environment.RenderingDriver}/{current.Environment.Gpu}) — deltas may be noise.");

        IReadOnlyList<MetricDelta> deltas = BaselineDiff.Compare(baseline.Metrics, current.Metrics, options);
        Log.Print($"[benchmark] vs baseline ({note}):");
        Log.Print($"  {"metric",-28} {"baseline",16} {"current",16} {"delta",16} {"%",9}  status");
        foreach (MetricDelta d in deltas)
        {
            string pct = double.IsInfinity(d.PercentDelta) ? "  inf" : d.PercentDelta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
            Log.Print($"  {d.Name,-28} {Fmt(d.Baseline),16} {Fmt(d.Current),16} " +
                $"{Fmt(d.AbsoluteDelta),16} {pct,8}  {Tag(d.Status)}");
        }
    }

    private static string Fmt(double? value)
    {
        if (value is not double v)
            return "-";
        return v == Math.Floor(v) && Math.Abs(v) < 1e15
            ? ((long)v).ToString("N0", CultureInfo.InvariantCulture)
            : v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string Tag(MetricStatus s) => s switch
    {
        MetricStatus.Improved => "IMPROVED",
        MetricStatus.Regressed => "REGRESSED",
        MetricStatus.Added => "added",
        MetricStatus.Removed => "removed",
        _ => "·",
    };

    private static string GlobalPath(string resOrUser) => ProjectSettings.GlobalizePath(resOrUser);

    private static void Write(string globalPath, string content)
    {
        string? dir = System.IO.Path.GetDirectoryName(globalPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(globalPath, content);
    }
}
