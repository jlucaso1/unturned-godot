using System.Collections.Generic;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.Tests.Benchmark;

public class BenchmarkReportTests
{
    [Fact]
    public void RoundTrip_FullyPopulated()
    {
        var report = new BenchmarkReport
        {
            SchemaVersion = "1",
            Timestamp = "2026-07-08T00:00:00Z",
            Environment = new BenchmarkEnvironment
            {
                GodotVersion = "4.7",
                Os = "Linux",
                Gpu = "Test GPU",
                RenderingDriver = "vulkan",
                RenderingMethod = "forward_plus",
                Headless = true,
                Scene = "PEI",
            },
            Metrics = new SortedDictionary<string, double> { ["draws"] = 390, ["build.ms"] = 12.5 },
        };

        BenchmarkReport? back = BenchmarkReport.FromJson(report.ToJson());

        Assert.NotNull(back);
        Assert.Equal("2026-07-08T00:00:00Z", back!.Timestamp);
        Assert.Equal("PEI", back.Environment.Scene);
        Assert.True(back.Environment.Headless);
        Assert.Equal(390, back.Metrics["draws"]);
        Assert.Equal(12.5, back.Metrics["build.ms"]);
    }

    [Fact]
    public void RoundTrip_Defaults_NullTimestamp()
    {
        var report = new BenchmarkReport();
        Assert.Null(report.Timestamp);

        BenchmarkReport? back = BenchmarkReport.FromJson(report.ToJson());

        Assert.NotNull(back);
        Assert.Null(back!.Timestamp);
        Assert.Equal("1", back.SchemaVersion);
        Assert.Empty(back.Metrics);
        Assert.Equal("", back.Environment.Os);
    }

    [Fact]
    public void ToJson_UsesCamelCase()
    {
        string json = new BenchmarkReport().ToJson();
        Assert.Contains("schemaVersion", json);
        Assert.Contains("renderingDriver", json);
    }

    [Fact]
    public void FromJson_JsonNull_ReturnsNull()
    {
        Assert.Null(BenchmarkReport.FromJson("null"));
    }
}
