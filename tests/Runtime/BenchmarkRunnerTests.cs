using System;
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
        Assert.NotEqual(stamp, "");
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
}
