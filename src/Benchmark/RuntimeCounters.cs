using System;
using System.Diagnostics;
using System.Threading;

namespace UnturnedGodot.Benchmark;

// Opt-in subsystem timings for Tier 3. Production runs pay only a volatile boolean read; counters are
// enabled after benchmark warmup and disabled before report generation.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public static class RuntimeCounters
{
    public enum Counter
    {
        NetworkServer,
        NetworkClient,
        PlayerPhysics,
        PlayerMoveAndSlide,
        PlayerStep,
        NavigationReconcile,
        ZombiesView,
        Count,
    }

    public readonly record struct Sample(long Calls, double TotalMs, double MeanMs, double MaxMs);

    private static readonly long[] ElapsedTicks = new long[(int)Counter.Count];
    private static readonly long[] Calls = new long[(int)Counter.Count];
    private static readonly long[] MaxTicks = new long[(int)Counter.Count];
    private static int Enabled;

    public static void ResetAndEnable()
    {
        for (int i = 0; i < (int)Counter.Count; i++)
        {
            Interlocked.Exchange(ref ElapsedTicks[i], 0);
            Interlocked.Exchange(ref Calls[i], 0);
            Interlocked.Exchange(ref MaxTicks[i], 0);
        }
        Volatile.Write(ref Enabled, 1);
    }

    public static void Disable() => Volatile.Write(ref Enabled, 0);

    public static long Start() => Volatile.Read(ref Enabled) != 0 ? Stopwatch.GetTimestamp() : 0;

    public static void Record(Counter counter, long started)
    {
        if (started == 0)
            return;
        RecordTicks(counter, Stopwatch.GetTimestamp() - started);
    }

    public static void RecordTicks(Counter counter, long ticks)
    {
        if (ticks <= 0 || Volatile.Read(ref Enabled) == 0)
            return;
        int index = (int)counter;
        Interlocked.Add(ref ElapsedTicks[index], ticks);
        Interlocked.Increment(ref Calls[index]);
        long observed = Volatile.Read(ref MaxTicks[index]);
        while (ticks > observed)
        {
            long prior = Interlocked.CompareExchange(ref MaxTicks[index], ticks, observed);
            if (prior == observed)
                break;
            observed = prior;
        }
    }

    public static Sample Read(Counter counter)
    {
        int index = (int)counter;
        long calls = Volatile.Read(ref Calls[index]);
        double totalMs = Volatile.Read(ref ElapsedTicks[index]) * 1000.0 / Stopwatch.Frequency;
        double maxMs = Volatile.Read(ref MaxTicks[index]) * 1000.0 / Stopwatch.Frequency;
        return new Sample(calls, totalMs, calls == 0 ? 0.0 : totalMs / calls, maxMs);
    }
}
