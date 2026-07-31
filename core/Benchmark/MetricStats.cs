using System;
using System.Collections.Generic;

namespace UnturnedGodot.Benchmark;

// Pure aggregation over a set of samples (e.g. repeated build timings or per-frame render times).
// Deterministic and engine-free so the whole thing is unit-testable. Percentile uses linear
// interpolation between closest ranks (the "R-7" method, same as NumPy's default).
public static class MetricStats
{
    public static double Min(IReadOnlyList<double> values) => Reduce(values, double.MaxValue, Math.Min);

    public static double Max(IReadOnlyList<double> values) => Reduce(values, double.MinValue, Math.Max);

    public static double Mean(IReadOnlyList<double> values)
    {
        RequireNonEmpty(values);
        double sum = 0;
        foreach (double v in values)
            sum += v;
        return sum / values.Count;
    }

    public static double Median(IReadOnlyList<double> values) => Percentile(values, 50.0);

    // p in [0, 100]. Interpolates between the two closest ranks.
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        RequireNonEmpty(values);
        if (p < 0.0 || p > 100.0)
            throw new ArgumentOutOfRangeException(nameof(p), p, "Percentile must be within [0, 100].");

        var sorted = new double[values.Count];
        for (int i = 0; i < values.Count; i++)
            sorted[i] = values[i];
        Array.Sort(sorted);

        if (sorted.Length == 1)
            return sorted[0];

        double rank = p / 100.0 * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper)
            return sorted[lower];

        double weight = rank - lower;
        return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
    }

    // Population standard deviation (divides by N). Deterministic dispersion for A/B runs.
    public static double StdDev(IReadOnlyList<double> values)
    {
        RequireNonEmpty(values);
        double mean = Mean(values);
        double sumSq = 0;
        foreach (double v in values)
        {
            double d = v - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / values.Count);
    }

    private static double Reduce(IReadOnlyList<double> values, double seed, Func<double, double, double> fn)
    {
        RequireNonEmpty(values);
        double acc = seed;
        foreach (double v in values)
            acc = fn(acc, v);
        return acc;
    }

    private static void RequireNonEmpty(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            throw new ArgumentException("Cannot compute a statistic over zero samples.", nameof(values));
    }
}
