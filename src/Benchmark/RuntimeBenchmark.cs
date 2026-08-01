using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace UnturnedGodot.Benchmark;

// Tier 3 runs through the real interactive load (player, physics, networking, zombies, lighting and
// streamed objects) and samples the stationary spawn view. Tier 2 deliberately excludes those systems;
// keeping this separate makes a render-only improvement distinguishable from a gameplay CPU regression.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class RuntimeBenchmark
{
    public static async Task RunAsync(Node context, string mapName, double seconds, double loadMs)
    {
        SceneTree tree = context.GetTree();
        try
        {
            seconds = Math.Clamp(seconds, 3.0, 120.0);
            Log.Print($"[benchmark] Tier 3 (interactive) loaded in {loadMs:0} ms; " +
                $"warming up and sampling for {seconds:0.0}s...");

            // Let pipeline compilation, texture application and the loading-screen fade leave the sample.
            ulong warmupUntil = Time.GetTicksUsec() + 2_000_000;
            while (Time.GetTicksUsec() < warmupUntil)
                await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);

            RuntimeCounters.ResetAndEnable();

            var frameMs = new List<double>();
            var processMs = new List<double>();
            var physicsMs = new List<double>();
            var draws = new List<double>();
            var primitives = new List<double>();
            var objects = new List<double>();
            var withPhysicsFrameMs = new List<double>();
            var withoutPhysicsFrameMs = new List<double>();

            ulong started = Time.GetTicksUsec();
            ulong until = started + (ulong)(seconds * 1_000_000.0);
            ulong last = started;
            ulong lastPhysicsFrame = Engine.GetPhysicsFrames();
            while (Time.GetTicksUsec() < until)
            {
                await context.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                ulong now = Time.GetTicksUsec();
                double elapsedMs = (now - last) / 1000.0;
                frameMs.Add(elapsedMs);
                last = now;
                ulong physicsFrame = Engine.GetPhysicsFrames();
                (physicsFrame != lastPhysicsFrame ? withPhysicsFrameMs : withoutPhysicsFrameMs).Add(elapsedMs);
                lastPhysicsFrame = physicsFrame;
                processMs.Add(Mon(Performance.Monitor.TimeProcess) * 1000.0);
                physicsMs.Add(Mon(Performance.Monitor.TimePhysicsProcess) * 1000.0);
                draws.Add(Mon(Performance.Monitor.RenderTotalDrawCallsInFrame));
                primitives.Add(Mon(Performance.Monitor.RenderTotalPrimitivesInFrame));
                objects.Add(Mon(Performance.Monitor.RenderTotalObjectsInFrame));
            }

            double medianFrame = MetricStats.Median(frameMs);
            RuntimeCounters.Disable();
            var report = new BenchmarkReport
            {
                Timestamp = BenchmarkRunner.Timestamp(),
                Environment = BenchmarkRunner.BuildEnvironment(mapName),
                Metrics = new SortedDictionary<string, double>(StringComparer.Ordinal)
                {
                    ["interactive.loadMs"] = loadMs,
                    ["runtime.frameMs.median"] = medianFrame,
                    ["runtime.frameMs.p90"] = MetricStats.Percentile(frameMs, 90),
                    ["runtime.frameMs.p95"] = MetricStats.Percentile(frameMs, 95),
                    ["runtime.frameMs.p99"] = MetricStats.Percentile(frameMs, 99),
                    ["runtime.frameMs.max"] = MetricStats.Percentile(frameMs, 100),
                    ["runtime.framesOver4_17Ms.percent"] = PercentageOver(frameMs, 1000.0 / 240.0),
                    ["runtime.framesOver8_33Ms.percent"] = PercentageOver(frameMs, 1000.0 / 120.0),
                    ["runtime.fps.fromMedian"] = 1000.0 / medianFrame,
                    ["runtime.processMonitorMs.median"] = MetricStats.Median(processMs),
                    ["runtime.physicsMonitorMs.median"] = MetricStats.Median(physicsMs),
                    ["runtime.drawCalls.median"] = MetricStats.Median(draws),
                    ["runtime.primitives.median"] = MetricStats.Median(primitives),
                    ["runtime.renderObjects.median"] = MetricStats.Median(objects),
                    ["runtime.rssBytes"] = ProcessMemory.RssBytes(),
                    ["runtime.managedBytes"] = GC.GetTotalMemory(false),
                    // Sample after all frame metrics: this collection is benchmark bookkeeping only and
                    // distinguishes live owner metadata from dead upload records awaiting a future GC.
                    ["runtime.managedLiveBytes"] = GC.GetTotalMemory(true),
                    ["runtime.nodeCount"] = Mon(Performance.Monitor.ObjectNodeCount),
                    ["runtime.videoMemoryBytes"] = Mon(Performance.Monitor.RenderVideoMemUsed),
                    ["runtime.samples"] = frameMs.Count,
                    ["runtime.sampleSeconds"] = seconds,
                    ["runtime.scriptedMovement"] = OS.GetEnvironment("UG_RUNTIME_BENCH_MOVE") == "1" ? 1 : 0,
                },
            };
            AddFrameBucket(report.Metrics, "withPhysics", withPhysicsFrameMs);
            AddFrameBucket(report.Metrics, "withoutPhysics", withoutPhysicsFrameMs);
            AddCounterMetrics(report.Metrics);
            BenchmarkRunner.Finish(report, $"{mapName}-runtime", DiffOptions(),
                "timings are advisory; counts are deterministic for the same spawn view");
        }
        catch (Exception e)
        {
            Log.PrintErr($"[benchmark] Tier 3 failed: {e}");
        }
        finally
        {
            RuntimeCounters.Disable();
            AppShutdown.RequestQuit(tree);
        }
    }

    private static double Mon(Performance.Monitor monitor) => Performance.GetMonitor(monitor);

    // Every wall-clock timing is subject to scheduler noise. Prefixes cover the frame/monitor families,
    // while suffixes cover every dynamically named subsystem without loosening its deterministic call
    // count. Keeping this family-based also covers future percentiles and counters automatically.
    private static BaselineDiffOptions DiffOptions() => new()
    {
        HigherIsBetter = new HashSet<string>(StringComparer.Ordinal)
        {
            "runtime.fps.fromMedian",
            "runtime.samples",
        },
        ThresholdPrefixOverrides = new Dictionary<string, double>
        {
            ["runtime.frameMs"] = 0.15,
            ["runtime.processMonitorMs."] = 0.15,
            ["runtime.physicsMonitorMs."] = 0.15,
        },
        ThresholdSuffixOverrides = new Dictionary<string, double>
        {
            [".totalMs"] = 0.15,
            [".meanMs"] = 0.15,
            [".maxMs"] = 0.15,
        },
        ThresholdOverrides = new Dictionary<string, double>
        {
            ["interactive.loadMs"] = 0.10,
            ["runtime.rssBytes"] = 0.05,
            ["runtime.videoMemoryBytes"] = 0.05,
        },
    };

    // At or below the physics tick rate, every rendered frame can include a physics step and leave the
    // complementary bucket empty. An absent metric describes that run honestly; asking MetricStats to
    // aggregate zero samples would instead abort the entire benchmark before its report is written.
    private static void AddFrameBucket(SortedDictionary<string, double> metrics, string name,
        IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return;
        metrics[$"runtime.frameMs.{name}.median"] = MetricStats.Median(values);
        metrics[$"runtime.frameMs.{name}.p95"] = MetricStats.Percentile(values, 95);
    }

    private static void AddCounterMetrics(SortedDictionary<string, double> metrics)
    {
        foreach (RuntimeCounters.Counter counter in Enum.GetValues<RuntimeCounters.Counter>())
        {
            if (counter == RuntimeCounters.Counter.Count)
                continue;
            RuntimeCounters.Sample sample = RuntimeCounters.Read(counter);
            string prefix = $"runtime.subsystem.{counter}";
            metrics[$"{prefix}.calls"] = sample.Calls;
            metrics[$"{prefix}.totalMs"] = sample.TotalMs;
            metrics[$"{prefix}.meanMs"] = sample.MeanMs;
            metrics[$"{prefix}.maxMs"] = sample.MaxMs;
        }
    }

    private static double PercentageOver(List<double> samples, double thresholdMs)
    {
        int count = 0;
        foreach (double sample in samples)
            if (sample > thresholdMs)
                count++;
        return samples.Count == 0 ? 0.0 : count * 100.0 / samples.Count;
    }
}
