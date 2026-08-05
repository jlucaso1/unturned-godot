using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// What a benchmark run writes down, and what it refuses to conclude.
//
// A number is only a measurement if you can say what machine produced it, so every report carries the
// environment it was taken on — and the diff refuses to call a change a regression when that environment
// moved, because a different GPU or rendering driver makes the two runs incomparable in ways no threshold
// can rescue.
//
// The tiers themselves are not driven here, and the file says why rather than leaving a hole: Run and
// RunAsync load a whole map, measure it, write a report and then QUIT the process to report their exit
// status. A test that called one would end the suite mid-run, and everything after it would be reported
// as neither passed nor failed. What is covered is everything the tiers do that is not leaving.
public class BenchmarkRunnerTests : TestClass
{
    public BenchmarkRunnerTests(Node testScene) : base(testScene) { }

    // The environment is never blank. A headless run initializes no GPU, so the adapter name comes back
    // empty — and a report with an empty GPU field reads like a bug in the reporter rather than a fact
    // about the run, so it says so in words instead.
    [Test]
    public void TheEnvironmentNeverReportsABlankMachine()
    {
        BenchmarkEnvironment environment = BenchmarkRunner.BuildEnvironment("PEI");

        Assert.NotEmpty(environment.GodotVersion);
        Assert.NotEmpty(environment.Os);
        Assert.NotEmpty(environment.Gpu);
        Assert.Equal("PEI", environment.Scene);

        // This suite runs headless, so the adapter is deliberately labelled rather than left blank.
        Assert.True(environment.Headless, "the runtime suite is expected to run headless");
        Assert.Contains("headless", environment.Gpu, StringComparison.OrdinalIgnoreCase);
    }

    // Two runs on this machine describe the same machine. The diff compares environments to decide
    // whether its deltas mean anything, so an environment that varied between two runs on one box would
    // make every comparison warn about noise that is not there.
    [Test]
    public void TwoRunsOnOneMachineDescribeTheSameMachine()
    {
        Assert.Equal(BenchmarkRunner.BuildEnvironment("PEI"), BenchmarkRunner.BuildEnvironment("PEI"));
    }

    // The scene is part of the environment, which is what keeps two maps' numbers from being compared as
    // if they were the same measurement.
    [Test]
    public void TheSceneIsPartOfWhatIsBeingCompared()
    {
        Assert.NotEqual(BenchmarkRunner.BuildEnvironment("PEI"),
            BenchmarkRunner.BuildEnvironment("Washington"));
    }

    // Every report is stamped, and stamped in UTC. A report without one cannot be ordered against
    // another, which is the first thing anyone does with two of them.
    [Test]
    public void EveryReportIsStamped()
    {
        string stamp = BenchmarkRunner.Timestamp();

        Assert.NotEmpty(stamp);

        // UTC, and readable as a date. Two reports are ordered against each other before anything else
        // is done with them, and a stamp in local time silently reorders runs from two machines — which
        // a "not empty" check cannot see, and neither can a switch of utc:true to utc:false.
        Assert.True(DateTime.TryParse(stamp, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal
            | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime parsed),
            $"the stamp '{stamp}' is not a date anything can order");
        Assert.True(Math.Abs((DateTime.UtcNow - parsed).TotalHours) < 1.0,
            $"the stamp '{stamp}' is an hour or more from UTC, so it is local time");
    }

    // With a baseline, the run diffs against it — and that diff is the only reason a tier exists. A
    // number nobody compares is a number nobody reads.
    //
    // The baseline lives beside the repo rather than in user:// and is per-machine by design (it holds
    // wall-clock timings measured on whoever ran them), which is why the directory is gitignored. These
    // write one under a name nothing else uses and take it away again.
    [Test]
    public void WithABaselineTheRunDiffsAgainstIt()
    {
        using var baseline = new Baseline(Report(("diff.probe.ms", 1000.0), ("draw.calls", 500.0)));

        // Slower and heavier than the baseline, which is what a regression looks like.
        BenchmarkRunner.Finish(Report(("diff.probe.ms", 2000.0), ("draw.calls", 800.0)),
            baseline.Key, new BaselineDiffOptions(), "");

        Assert.True(System.IO.File.Exists(baseline.LatestPath), "the diffing run wrote no report");

        // The comparison was REPORTED. Finish writes *-latest.json before it even looks for a baseline,
        // so the file alone would still be there if the whole diff branch were deleted — and a diff
        // nobody printed is a diff nobody reads.
        Assert.Contains(Said(), line => line.Contains("diff.probe.ms", StringComparison.Ordinal));
    }

    // A metric the baseline never had is reported as new rather than as a change from zero. Every added
    // counter would otherwise arrive as an infinite regression.
    [Test]
    public void AMetricTheBaselineNeverHadIsNew()
    {
        using var baseline = new Baseline(Report(("load.total.ms", 1000.0)));

        BenchmarkRunner.Finish(Report(("load.total.ms", 1000.0), ("newcounter.probe.ms", 42.0)),
            baseline.Key, new BaselineDiffOptions(), "a counter that did not exist before");

        Assert.True(System.IO.File.Exists(baseline.LatestPath));
        Assert.Contains(Said(), line => line.Contains("newcounter.probe.ms", StringComparison.Ordinal));
    }

    // A baseline that parses to nothing is skipped, and says so. This is the case the code guards, and
    // its own message — "Baseline file is unreadable; skipping diff" — states the intent for the whole
    // family: a damaged baseline costs the comparison, not the run.
    [Test]
    public void ABaselineThatParsesToNothingIsSkipped()
    {
        using var baseline = new Baseline("null");

        BenchmarkRunner.Finish(Report(("load.total.ms", 1000.0)), baseline.Key,
            new BaselineDiffOptions(), "");

        Assert.True(System.IO.File.Exists(baseline.LatestPath),
            "an unreadable baseline stopped the run from reporting at all");
    }

    // And a baseline that is not JSON at all is skipped on the same terms, rather than taking the run
    // down. This used to throw, and the throw landed at the very END of a tier — after the measurement
    // was complete — so a run paid its whole cost and then died on the file it was going to compare
    // against. A baseline is a file on a developer's disk: hand-edited, half-written, truncated by a full
    // disk, copied from a machine running another schema.
    [Test]
    public void ABaselineThatIsNotJsonIsSkippedToo()
    {
        using var baseline = new Baseline("this is not a report");

        BenchmarkRunner.Finish(Report(("load.total.ms", 1000.0)), baseline.Key,
            new BaselineDiffOptions(), "");

        Assert.True(System.IO.File.Exists(baseline.LatestPath),
            "an unparseable baseline stopped the run from reporting at all");
    }

    // A baseline from ANOTHER machine is diffed, but its deltas are called noise. A different GPU or
    // rendering driver makes two runs incomparable in ways no threshold can rescue, and reporting those
    // deltas silently would have someone chase a regression that is a different graphics card.
    [Test]
    public void ABaselineFromAnotherMachineIsCalledNoise()
    {
        var elsewhere = new BenchmarkReport
        {
            Timestamp = BenchmarkRunner.Timestamp(),
            Environment = BenchmarkRunner.BuildEnvironment("PEI") with
            {
                Gpu = "Some Other GPU",
                RenderingDriver = "vulkan",
                Headless = false,
            },
            Metrics = new System.Collections.Generic.SortedDictionary<string, double>
            {
                ["load.total.ms"] = 1000.0,
            },
        };

        using var baseline = new Baseline(elsewhere);

        BenchmarkRunner.Finish(Report(("load.total.ms", 1500.0)), baseline.Key,
            new BaselineDiffOptions(), "");

        Assert.True(System.IO.File.Exists(baseline.LatestPath));

        // The WARNING was printed. Finish writes the report before it reads the baseline at all, so the
        // file alone is there whether or not the environment comparison still happens — and a delta
        // reported without it has someone chasing a regression that is a different graphics card.
        Assert.Contains(Said(),
            line => line.Contains("environment differs", StringComparison.OrdinalIgnoreCase));
    }

    // A run with no baseline to compare against still writes its report, and says what to do about the
    // missing baseline instead of failing. This is the first run on any new machine — and on CI, every
    // run — so a tier that refused here would report nothing on exactly the boxes it exists for.
    [Test]
    public void ARunWithNoBaselineStillWritesItsReport()
    {
        string key = "runtime-tests-" + Guid.NewGuid().ToString("N")[..8];
        var report = new BenchmarkReport
        {
            Timestamp = BenchmarkRunner.Timestamp(),
            Environment = BenchmarkRunner.BuildEnvironment("PEI"),
            Metrics = new System.Collections.Generic.SortedDictionary<string, double>
            {
                ["load.total.ms"] = 1234.5,
            },
        };

        string latest = ProjectSettings.GlobalizePath($"user://bench/{key}-latest.json");
        try
        {
            BenchmarkRunner.Finish(report, key, new BaselineDiffOptions(), "");

            Assert.True(System.IO.File.Exists(latest), "the run wrote no report at all");

            // And what it wrote is the report, readable back as itself: a run whose output could not be
            // reopened would look like a successful measurement of nothing.
            BenchmarkReport? written = BenchmarkReport.FromJson(System.IO.File.ReadAllText(latest));
            Assert.NotNull(written);
            Assert.Equal(1234.5, written!.Metrics["load.total.ms"]);
            Assert.Equal(report.Environment, written.Environment);
        }
        finally
        {
            try
            {
                System.IO.File.Delete(latest);
            }
            catch (System.IO.IOException)
            {
                // A leftover report is not worth failing a test over.
            }
        }
    }

    // --- helpers -------------------------------------------------------------------------------------

    // The diff has no return value — it prints — so the log is the only place its conclusions are
    // observable from. Searched whole rather than sliced from a mark: Log.Tail is a fixed-size RING, so
    // in a full suite run an index taken before the call no longer points where it did. Every string
    // matched below is unique to the report this test just wrote.
    private static List<string> Said() => Log.Tail();

    private static BenchmarkReport Report(params (string Name, double Value)[] metrics)
    {
        var values = new System.Collections.Generic.SortedDictionary<string, double>();
        foreach ((string name, double value) in metrics)
            values[name] = value;

        return new BenchmarkReport
        {
            Timestamp = BenchmarkRunner.Timestamp(),
            Environment = BenchmarkRunner.BuildEnvironment("PEI"),
            Metrics = values,
        };
    }

    // A baseline on disk under a name nothing else uses, taken away again with the test. The directory
    // is gitignored precisely because these are per-machine, so writing one here leaves nothing behind
    // that anyone would commit.
    private sealed class Baseline : IDisposable
    {
        public string Key { get; } = "runtime-tests-" + Guid.NewGuid().ToString("N")[..8];

        public string LatestPath => ProjectSettings.GlobalizePath($"user://bench/{Key}-latest.json");

        private string BaselinePath => ProjectSettings.GlobalizePath($"res://bench/baseline/{Key}.json");

        public Baseline(BenchmarkReport report) : this(report.ToJson()) { }

        public Baseline(string contents)
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BaselinePath)!);
            System.IO.File.WriteAllText(BaselinePath, contents);
        }

        public void Dispose()
        {
            foreach (string leftover in new[] { BaselinePath, LatestPath })
            {
                try
                {
                    System.IO.File.Delete(leftover);
                }
                catch (System.IO.IOException)
                {
                    // A leftover benchmark file is not worth failing a test over.
                }
            }
        }
    }
}
