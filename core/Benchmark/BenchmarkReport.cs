using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnturnedGodot.Benchmark;

// The environment the run happened in. Recorded so a baseline is only ever compared against a run
// from a comparable machine/renderer — a diff across different GPUs or rendering methods is noise.
public sealed record BenchmarkEnvironment
{
    public string GodotVersion { get; init; } = "";
    public string Os { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string RenderingDriver { get; init; } = "";
    public string RenderingMethod { get; init; } = "";
    public bool Headless { get; init; }
    public string Scene { get; init; } = "";
}

// A single benchmark result: the environment, plus the numeric metrics as a name→value map. Keeping
// metrics as a sorted map (not typed fields) makes BaselineDiff generic and lets us add metrics later
// without touching the diff/serialization code. Timings live under the "*.ms" naming convention.
public sealed record BenchmarkReport
{
    public string SchemaVersion { get; init; } = "1";
    public string? Timestamp { get; init; }
    public BenchmarkEnvironment Environment { get; init; } = new();
    public SortedDictionary<string, double> Metrics { get; init; } = new();

    public string ToJson() => JsonSerializer.Serialize(this, BenchmarkJson.Default.BenchmarkReport);

    public static BenchmarkReport? FromJson(string json) =>
        JsonSerializer.Deserialize(json, BenchmarkJson.Default.BenchmarkReport);
}

// Source-generated serialization: AOT/trim-safe (the game project publishes with PublishAot=true) and
// zero external dependencies.
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchmarkReport))]
public partial class BenchmarkJson : JsonSerializerContext
{
}
