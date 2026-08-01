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
    public void ThresholdPrefixOverride_CoversDynamicMetricNames()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdPrefixOverrides = new Dictionary<string, double>
            {
                ["gpu.frameMs.median."] = 0.10,
            },
        };
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["gpu.frameMs.median.ground_diag"] = 100 },
            new Dictionary<string, double> { ["gpu.frameMs.median.ground_diag"] = 108 }, options);

        Assert.Equal(MetricStatus.Unchanged, Delta(r, "gpu.frameMs.median.ground_diag").Status);
    }

    [Fact]
    public void ExactThresholdWinsOverTheMostSpecificPrefix()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdPrefixOverrides = new Dictionary<string, double>
            {
                ["gpu."] = 0.50,
                ["gpu.frameMs."] = 0.10,
            },
            ThresholdOverrides = new Dictionary<string, double>
            {
                ["gpu.frameMs.median.ground"] = 0.01,
            },
        };
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["gpu.frameMs.median.ground"] = 100 },
            new Dictionary<string, double> { ["gpu.frameMs.median.ground"] = 105 }, options);

        Assert.Equal(MetricStatus.Regressed, Delta(r, "gpu.frameMs.median.ground").Status);
    }

    [Fact]
    public void MostSpecificThresholdPrefixWins()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdPrefixOverrides = new Dictionary<string, double>
            {
                ["gpu."] = 0.50,
                ["gpu.frameMs."] = 0.10,
            },
        };
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["gpu.frameMs.median.ground"] = 100 },
            new Dictionary<string, double> { ["gpu.frameMs.median.ground"] = 120 }, options);

        Assert.Equal(MetricStatus.Regressed, Delta(r, "gpu.frameMs.median.ground").Status);
    }

    // Both benchmark tiers park scheduler-dependent snapshots (the foliage residency counts of a run
    // whose upload queue never drained) behind an infinite suffix threshold. Whatever the two sides
    // happen to hold, the difference is never a verdict — and the delta is still reported.
    [Fact]
    public void InfiniteThreshold_ReportsTheDeltaWithoutEverClassifyingIt()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdSuffixOverrides = new Dictionary<string, double>
            {
                ["Unsettled"] = double.PositiveInfinity,
            },
        };
        var baseline = new Dictionary<string, double>
        {
            ["runtime.foliage.residentChunksUnsettled"] = 94,
            ["runtime.foliage.residentChunks"] = 94,
            ["runtime.foliage.zeroBaselineUnsettled"] = 0,
        };
        var current = new Dictionary<string, double>
        {
            ["runtime.foliage.residentChunksUnsettled"] = 12,
            ["runtime.foliage.residentChunks"] = 12,
            ["runtime.foliage.zeroBaselineUnsettled"] = 5_000_000,
        };

        IReadOnlyList<MetricDelta> r = BaselineDiff.Compare(baseline, current, options);

        Assert.Equal(MetricStatus.Unchanged, Delta(r, "runtime.foliage.residentChunksUnsettled").Status);
        // A zero baseline takes the denominator fallback; that must not escape the infinite threshold.
        Assert.Equal(MetricStatus.Unchanged, Delta(r, "runtime.foliage.zeroBaselineUnsettled").Status);
        // The settled key keeps the strict default and is still classified — the suffix rule must not
        // leak onto it. Fewer resident chunks is less memory, so the same drop reads as an improvement.
        Assert.Equal(MetricStatus.Improved, Delta(r, "runtime.foliage.residentChunks").Status);
        Assert.Equal(-82, Delta(r, "runtime.foliage.residentChunksUnsettled").AbsoluteDelta);
    }

    [Fact]
    public void ThresholdSuffixOverride_CoversDynamicSubsystemTimingsButNotCounts()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdSuffixOverrides = new Dictionary<string, double>
            {
                [".totalMs"] = 0.15,
                [".meanMs"] = 0.15,
                [".maxMs"] = 0.15,
            },
        };
        var baseline = new Dictionary<string, double>
        {
            ["runtime.subsystem.Navigation.totalMs"] = 100,
            ["runtime.subsystem.Navigation.meanMs"] = 100,
            ["runtime.subsystem.Navigation.maxMs"] = 100,
            ["runtime.subsystem.Navigation.calls"] = 100,
        };
        var current = baseline.ToDictionary(pair => pair.Key, _ => 110.0);

        IReadOnlyList<MetricDelta> r = BaselineDiff.Compare(baseline, current, options);

        Assert.Equal(MetricStatus.Unchanged, Delta(r, "runtime.subsystem.Navigation.totalMs").Status);
        Assert.Equal(MetricStatus.Unchanged, Delta(r, "runtime.subsystem.Navigation.meanMs").Status);
        Assert.Equal(MetricStatus.Unchanged, Delta(r, "runtime.subsystem.Navigation.maxMs").Status);
        Assert.Equal(MetricStatus.Regressed, Delta(r, "runtime.subsystem.Navigation.calls").Status);
    }

    [Fact]
    public void MostSpecificPrefixOrSuffixWins()
    {
        var options = new BaselineDiffOptions
        {
            ThresholdPrefixOverrides = new Dictionary<string, double> { ["runtime."] = 0.50 },
            ThresholdSuffixOverrides = new Dictionary<string, double>
            {
                [".subsystem.Audio.maxMs"] = 0.10,
            },
        };
        var r = BaselineDiff.Compare(
            new Dictionary<string, double> { ["runtime.subsystem.Audio.maxMs"] = 100 },
            new Dictionary<string, double> { ["runtime.subsystem.Audio.maxMs"] = 120 }, options);

        Assert.Equal(MetricStatus.Regressed, Delta(r, "runtime.subsystem.Audio.maxMs").Status);
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
