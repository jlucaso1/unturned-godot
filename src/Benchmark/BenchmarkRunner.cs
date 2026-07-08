using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace UnturnedGodot.Benchmark;

// Self-contained Tier-1 (structural) benchmark: build the real world via WorldBuilder, walk it for
// deterministic structural metrics, print a report, and diff against a committed baseline. Runs in one
// process under `godot --headless -- --benchmark`; add `--write-baseline` to (re)capture the baseline.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class BenchmarkRunner
{
    private const string BaselineDir = "res://bench/baseline";
    private const string LatestDir = "user://bench";

    public static void Run(Node context, string unturnedPath, string mapName)
    {
        GD.Print("[benchmark] Tier 1 (structural, headless) starting...");
        WorldBuildResult world = WorldBuilder.Build(unturnedPath, mapName);
        context.AddChild(world.Terrain);
        context.AddChild(world.Objects);

        SceneMetricsResult m = SceneMetrics.Collect(new Node[] { world.Terrain, world.Objects });
        BenchmarkReport report = BuildReport(m, world, mapName);

        string json = report.ToJson();
        Write(GlobalPath($"{LatestDir}/{mapName}-latest.json"), json);
        GD.Print($"[benchmark] Report:\n{json}");

        bool writeBaseline = Array.IndexOf(OS.GetCmdlineUserArgs(), "--write-baseline") >= 0;
        string baselinePath = GlobalPath($"{BaselineDir}/{mapName}.json");

        if (writeBaseline)
        {
            Write(baselinePath, json);
            GD.Print($"[benchmark] Baseline saved to {BaselineDir}/{mapName}.json");
            return;
        }

        if (!System.IO.File.Exists(baselinePath))
        {
            GD.Print($"[benchmark] No baseline at {BaselineDir}/{mapName}.json — " +
                "run once with `-- --benchmark --write-baseline` to capture one.");
            return;
        }

        BenchmarkReport? baseline = BenchmarkReport.FromJson(System.IO.File.ReadAllText(baselinePath));
        if (baseline is null)
        {
            GD.PrintErr("[benchmark] Baseline file is unreadable; skipping diff.");
            return;
        }

        PrintDiff(baseline, report);
    }

    private static BenchmarkReport BuildReport(SceneMetricsResult m, WorldBuildResult w, string mapName)
    {
        bool headless = DisplayServer.GetName() == "headless";

        // A real rendering driver reports the adapter (e.g. "AMD Radeon RX 6600 (RADV NAVI23)"). The
        // headless dummy driver initializes no GPU, so the name comes back empty — label it explicitly
        // instead of leaving a blank that reads like a bug.
        string gpu = RenderingServer.GetVideoAdapterName();
        if (string.IsNullOrEmpty(gpu))
            gpu = headless ? "(none — headless)" : "(unknown)";
        var metrics = new SortedDictionary<string, double>(StringComparer.Ordinal)
        {
            ["nodes"] = m.Nodes,
            ["meshInstances"] = m.MeshInstances,
            ["multiMeshInstances"] = m.MultiMeshInstances,
            ["multiMeshTotalInstances"] = m.MultiMeshTotalInstances,
            ["uploadedVertices"] = m.UploadedVertices,
            ["uploadedIndices"] = m.UploadedIndices,
            ["uploadedTriangles"] = m.UploadedTriangles,
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
        if (!headless)
            metrics["maxMultiMeshSpread"] = m.MaxMultiMeshSpread;

        return new BenchmarkReport
        {
            Timestamp = Time.GetDatetimeStringFromSystem(utc: true),
            Environment = new BenchmarkEnvironment
            {
                GodotVersion = (string)Engine.GetVersionInfo()["string"],
                Os = OS.GetName(),
                Gpu = gpu,
                RenderingDriver = RenderingServer.GetCurrentRenderingDriverName(),
                RenderingMethod = RenderingServer.GetCurrentRenderingMethod(),
                Headless = headless,
                Scene = mapName,
            },
            Metrics = metrics,
        };
    }

    private static void PrintDiff(BenchmarkReport baseline, BenchmarkReport current)
    {
        if (baseline.Environment != current.Environment)
            GD.Print($"[benchmark] WARNING: environment differs from baseline " +
                $"(baseline {baseline.Environment.RenderingDriver}/{baseline.Environment.Os} vs " +
                $"current {current.Environment.RenderingDriver}/{current.Environment.Os}) — deltas may be noise.");

        // Build timings are single-sample and jittery; give them a loose threshold so run-to-run noise
        // is not flagged as a regression. Structural metrics stay at the strict default (they're exact).
        var options = new BaselineDiffOptions
        {
            ThresholdOverrides = new Dictionary<string, double>
            {
                ["build.terrain.ms"] = 0.15,
                ["build.objects.ms"] = 0.15,
                ["build.total.ms"] = 0.15,
            },
        };

        IReadOnlyList<MetricDelta> deltas = BaselineDiff.Compare(baseline.Metrics, current.Metrics, options);
        GD.Print("[benchmark] vs baseline (*.ms are advisory — noisy single samples):");
        GD.Print($"  {"metric",-26} {"baseline",16} {"current",16} {"delta",16} {"%",9}  status");
        foreach (MetricDelta d in deltas)
        {
            string pct = double.IsInfinity(d.PercentDelta) ? "  inf" : d.PercentDelta.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture);
            GD.Print($"  {d.Name,-26} {Fmt(d.Baseline),16} {Fmt(d.Current),16} " +
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
