using System;
using System.Collections.Generic;

namespace UnturnedGodot.Benchmark;

public enum MetricStatus
{
    Unchanged,
    Improved,
    Regressed,
    Added,   // present in current, absent in baseline
    Removed, // present in baseline, absent in current
}

public sealed record MetricDelta(
    string Name,
    double? Baseline,
    double? Current,
    double AbsoluteDelta,
    double PercentDelta,
    MetricStatus Status);

// Compares a current run against a baseline, metric by metric. Generic over the metric map so it needs
// no knowledge of what was measured. "Better" direction is per-metric: most metrics (vertices, draw
// calls, bytes, milliseconds) are lower-is-better; a few (fps) are higher-is-better.
public sealed class BaselineDiffOptions
{
    // A change smaller than this fraction of the baseline is treated as noise (Unchanged).
    public double RelativeThreshold { get; init; } = 0.01;

    // Per-metric threshold overrides. Use for inherently noisy single-sample metrics (e.g. build
    // timings) so run-to-run jitter is not reported as a regression.
    public IReadOnlyDictionary<string, double> ThresholdOverrides { get; init; } =
        new Dictionary<string, double>();

    // Prefix policies cover metric families whose final segment is dynamic (for example benchmark pose
    // names). The most-specific matching prefix wins; an exact override still takes precedence.
    public IReadOnlyDictionary<string, double> ThresholdPrefixOverrides { get; init; } =
        new Dictionary<string, double>();

    // Metrics where a larger value is an improvement. Everything else is lower-is-better.
    public IReadOnlySet<string> HigherIsBetter { get; init; } = new HashSet<string>();
}

public static class BaselineDiff
{
    public static IReadOnlyList<MetricDelta> Compare(
        IReadOnlyDictionary<string, double> baseline,
        IReadOnlyDictionary<string, double> current,
        BaselineDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(current);
        options ??= new BaselineDiffOptions();

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string k in baseline.Keys)
            names.Add(k);
        foreach (string k in current.Keys)
            names.Add(k);

        var results = new List<MetricDelta>(names.Count);
        foreach (string name in names)
        {
            bool inBase = baseline.TryGetValue(name, out double b);
            bool inCurr = current.TryGetValue(name, out double c);

            if (!inBase)
            {
                results.Add(new MetricDelta(name, null, c, c, 0.0, MetricStatus.Added));
                continue;
            }
            if (!inCurr)
            {
                results.Add(new MetricDelta(name, b, null, -b, 0.0, MetricStatus.Removed));
                continue;
            }

            double abs = c - b;
            double pct = b != 0.0 ? abs / b * 100.0 : (abs == 0.0 ? 0.0 : double.PositiveInfinity);
            results.Add(new MetricDelta(name, b, c, abs, pct, Classify(name, b, c, options)));
        }
        return results;
    }

    private static MetricStatus Classify(string name, double baseline, double current, BaselineDiffOptions options)
    {
        double denom = baseline != 0.0 ? Math.Abs(baseline) : 1.0;
        double relative = (current - baseline) / denom;
        double threshold = ThresholdFor(name, options);
        if (Math.Abs(relative) < threshold)
            return MetricStatus.Unchanged;

        bool higherIsBetter = options.HigherIsBetter.Contains(name);
        bool increased = current > baseline;
        bool better = increased == higherIsBetter;
        return better ? MetricStatus.Improved : MetricStatus.Regressed;
    }

    private static double ThresholdFor(string name, BaselineDiffOptions options)
    {
        if (options.ThresholdOverrides.TryGetValue(name, out double exact))
            return exact;

        double threshold = options.RelativeThreshold;
        int bestLength = -1;
        foreach ((string prefix, double candidate) in options.ThresholdPrefixOverrides)
            if (prefix.Length > bestLength && name.StartsWith(prefix, StringComparison.Ordinal))
            {
                threshold = candidate;
                bestLength = prefix.Length;
            }
        return threshold;
    }
}
