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
        bool failed = true;
        // From here until the report is written, any quit — including one this tier never asked for —
        // has to report a failure. See AppShutdown.BenchmarkInFlight.
        AppShutdown.BeginBenchmark();
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
            // Keep settling outside the timed sample, then snapshot only a stable residency state. A cold
            // disk or short benchmark can otherwise make counts depend on scheduler speed.
            bool foliageSettled = await FoliageBenchmarkSettling.WaitAsync(context, tree);
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
                    ["runtime.scriptedMovement"] = EnvFlag.IsOn(OS.GetEnvironment("UG_RUNTIME_BENCH_MOVE"), whenUnset: false) ? 1 : 0,
                },
            };
            AddFrameBucket(report.Metrics, "withPhysics", withPhysicsFrameMs);
            AddFrameBucket(report.Metrics, "withoutPhysics", withoutPhysicsFrameMs);
            AddCounterMetrics(report.Metrics);
            AddFoliageMetrics(tree, report.Metrics, foliageSettled);
            BenchmarkRunner.Finish(report, $"{mapName}-runtime", DiffOptions(),
                "timings are advisory; counts are deterministic for the same spawn view, "
                    + "except foliage residency counts, which need runtime.foliage.settled=1 on both sides");
            failed = false;
            AppShutdown.EndBenchmark();
        }
        catch (Exception e)
        {
            Log.PrintErr($"[benchmark] Tier 3 failed: {e}");
        }
        finally
        {
            RuntimeCounters.Disable();
            // A throw here leaves no report, so the status has to say so: scripts/run-benchmark.sh runtime
            // returns this code, and a zero would let automation record a failed run as a measurement.
            AppShutdown.RequestQuit(tree, failed ? 1 : 0);
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
            "runtime.foliage.settled",
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
            // A mid-fill snapshot describes how far the queue happened to get, which depends on the
            // scheduler. Two unsettled runs would otherwise diff against each other and call that
            // difference a regression. Keep the numbers, never classify them.
            ["Unsettled"] = double.PositiveInfinity,
        },
        ThresholdOverrides = new Dictionary<string, double>
        {
            ["interactive.loadMs"] = 0.10,
            ["runtime.rssBytes"] = 0.05,
            // Includes dead objects awaiting a nondeterministically timed collection. The forced-GC
            // managedLiveBytes metric remains at the strict default threshold.
            ["runtime.managedBytes"] = 0.15,
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

    private static void AddFoliageMetrics(SceneTree tree, SortedDictionary<string, double> metrics,
        bool includeResidencySnapshot)
    {
        foreach (Node node in tree.GetNodesInGroup("foliage_streaming"))
            if (node is FoliageStreamingRenderer foliage)
            {
                metrics["runtime.foliage.indexedChunks"] = foliage.IndexedChunks;
                metrics["runtime.foliage.indexedInstances"] = foliage.IndexedInstances;
                // Always record the residency snapshot: omitting it left the reports that most need it —
                // a slow or GPU-less box, where streaming is starved by the per-frame upload budget —
                // with no record of what the streamer actually kept resident. A mid-fill snapshot is not
                // comparable with a drained one, so it is recorded under its own keys instead: the
                // baseline diff then reports it as added/removed and can never read it as a regression
                // against a settled baseline's steady set.
                bool settled = includeResidencySnapshot && foliage.IsSettled;
                string state = settled ? "" : "Unsettled";
                metrics["runtime.foliage.settled"] = settled ? 1 : 0;
                metrics[$"runtime.foliage.residentChunks{state}"] = foliage.ResidentChunks;
                metrics[$"runtime.foliage.residentInstances{state}"] = foliage.ResidentInstances;
                metrics[$"runtime.foliage.residentBufferBytes{state}"] = foliage.ResidentBufferBytes;
                metrics[$"runtime.foliage.pendingChunks{state}"] = foliage.PendingChunks;
                metrics["runtime.foliage.maxQueued"] = foliage.MaximumQueued;
                metrics["runtime.foliage.truncatedAdmissions"] = foliage.TruncatedAdmissions;
                metrics["runtime.foliage.maxDeferredPrefetch"] = foliage.MaximumDeferredPrefetch;
                metrics["runtime.foliage.maxDecodedBytes"] = foliage.MaximumDecodedBytes;
                metrics["runtime.foliage.prewarmedChunks"] = foliage.PrewarmedChunks;
                metrics["runtime.foliage.prewarm.totalMs"] = foliage.PrewarmTotalMs;
                metrics["runtime.foliage.emergencyVisibleLoads"] = foliage.EmergencyVisibleLoads;
                metrics["runtime.foliage.emergencyVisible.totalMs"] = foliage.EmergencyVisibleTotalMs;
                metrics["runtime.foliage.emergencyVisible.maxMs"] = foliage.EmergencyVisibleMaxMs;
                metrics["runtime.foliage.visibleSetMisses"] = foliage.VisibleSetMisses;
                metrics["runtime.foliage.retiredChunks"] = foliage.RetiredChunks;
                metrics["runtime.foliage.staleResults"] = foliage.StaleResults;
                metrics["runtime.foliage.decodeFailures"] = foliage.DecodeFailures;
                break;
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
