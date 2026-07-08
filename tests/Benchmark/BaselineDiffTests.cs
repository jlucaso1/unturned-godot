using System;
using System.Collections.Generic;
using System.Linq;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests.Benchmark;

public class BaselineDiffTests
{
    private static MetricDelta Delta(IReadOnlyList<MetricDelta> r, string name) => r.First(d => d.Name == name);

    [Fact]
    public void AddedAndRemovedMetrics()
    {
        var baseline = new Dictionary<string, double> { ["gone"] = 10 };
        var current = new Dictionary<string, double> { ["fresh"] = 5 };

        IReadOnlyList<MetricDelta> r = BaselineDiff.Compare(baseline, current);

        Assert.Equal(MetricStatus.Removed, Delta(r, "gone").Status);
        Assert.Null(Delta(r, "gone").Current);
        Assert.Equal(MetricStatus.Added, Delta(r, "fresh").Status);
        Assert.Null(Delta(r, "fresh").Baseline);
    }

    [Fact]
    public void LowerIsBetter_ImprovedAndRegressed()
    {
        var baseline = new Dictionary<string, double> { ["ms"] = 100, ["draws"] = 50 };
        var current = new Dictionary<string, double> { ["ms"] = 80, ["draws"] = 60 };

        IReadOnlyList<MetricDelta> r = BaselineDiff.Compare(baseline, current);

        Assert.Equal(MetricStatus.Improved, Delta(r, "ms").Status);    // decreased -> better
        Assert.Equal(MetricStatus.Regressed, Delta(r, "draws").Status); // increased -> worse
        Assert.Equal(-20, Delta(r, "ms").AbsoluteDelta);
        Assert.Equal(-20, Delta(r, "ms").PercentDelta, 6);
    }

    [Fact]
    public void HigherIsBetter_Fps()
    {
        var options = new BaselineDiffOptions { HigherIsBetter = new HashSet<string> { "fps" } };
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["fps"] = 60 },
            new Dictionary<string, double> { ["fps"] = 75 }, options);
        Assert.Equal(MetricStatus.Improved, Delta(r, "fps").Status); // increased fps -> better
    }

    [Fact]
    public void WithinThreshold_IsUnchanged()
    {
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["ms"] = 100 },
            new Dictionary<string, double> { ["ms"] = 100.5 }); // 0.5% < default 1%
        Assert.Equal(MetricStatus.Unchanged, Delta(r, "ms").Status);
    }

    [Fact]
    public void ThresholdOverride_SuppressesNoisyMetric()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdOverrides = new Dictionary<string, double> { ["build.ms"] = 0.5 },
        };
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["build.ms"] = 100 },
            new Dictionary<string, double> { ["build.ms"] = 120 }, options); // 20% < 50% override
        Assert.Equal(MetricStatus.Unchanged, Delta(r, "build.ms").Status);
    }

    [Fact]
    public void ZeroBaseline_HandlesPercentAndClassification()
    {
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["a"] = 0, ["b"] = 0 },
            new Dictionary<string, double> { ["a"] = 5, ["b"] = 0 });
        Assert.Equal(double.PositiveInfinity, Delta(r, "a").PercentDelta); // nonzero change from 0
        Assert.Equal(MetricStatus.Regressed, Delta(r, "a").Status);        // denom falls back to 1
        Assert.Equal(0.0, Delta(r, "b").PercentDelta);                     // 0 -> 0
        Assert.Equal(MetricStatus.Unchanged, Delta(r, "b").Status);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var map = new Dictionary<string, double>();
        Assert.Throws<ArgumentNullException>(() => BaselineDiff.Compare(null!, map));
        Assert.Throws<ArgumentNullException>(() => BaselineDiff.Compare(map, null!));
    }
}
