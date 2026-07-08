using System;
using System.Collections.Generic;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests.Benchmark;

public class MetricStatsTests
{
    private static readonly double[] Sample = { 4, 1, 3, 2, 5 };

    [Fact]
    public void BasicStatistics()
    {
        Assert.Equal(1, MetricStats.Min(Sample));
        Assert.Equal(5, MetricStats.Max(Sample));
        Assert.Equal(3, MetricStats.Mean(Sample));
        Assert.Equal(3, MetricStats.Median(Sample));
        Assert.Equal(Math.Sqrt(2), MetricStats.StdDev(Sample), 6);
        Assert.Equal(1, MetricStats.Mad(Sample));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(50, 3)]
    [InlineData(100, 5)]
    [InlineData(25, 2)]
    [InlineData(75, 4)]
    public void Percentile(double p, double expected)
    {
        Assert.Equal(expected, MetricStats.Percentile(Sample, p), 6);
    }

    [Fact]
    public void Percentile_Interpolates()
    {
        Assert.Equal(2.5, MetricStats.Percentile(new double[] { 1, 2, 3, 4 }, 50), 6);
    }

    [Fact]
    public void SingleElement()
    {
        var one = new double[] { 42 };
        Assert.Equal(42, MetricStats.Percentile(one, 37));
        Assert.Equal(0, MetricStats.StdDev(one));
    }

    [Fact]
    public void Empty_Throws()
    {
        var empty = Array.Empty<double>();
        Assert.Throws<ArgumentException>(() => MetricStats.Mean(empty));
        Assert.Throws<ArgumentException>(() => MetricStats.Min(empty));
    }

    [Fact]
    public void Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => MetricStats.Mean(null!));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percentile_OutOfRange_Throws(double p)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MetricStats.Percentile(Sample, p));
    }
}
