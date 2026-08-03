using System;
using System.Diagnostics;
using Godot;

namespace UnturnedGodot.Benchmark;

// UG_MEM_TRACE=<seconds>: prints one line per interval with process RSS, the managed heap's committed
// and live sizes, how much has been allocated since the last line, and the collection counts. Tier 3
// reports one RSS number at the end of its sample; a session's memory question is almost always about
// the shape between load and there — what a one-time load leaves behind against what the steady loop
// keeps churning — and that needs the timeline rather than the endpoint.
//
// Off unless the variable is set, so nothing samples /proc or forces a collection in a normal session.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed partial class MemoryTrace : Node
{
    private double _seconds;
    private double _elapsed;
    private long _lastAllocated;
    private readonly Stopwatch _since = Stopwatch.StartNew();

    // Returns the node when tracing is on, so callers add it themselves and it inherits their lifetime.
    public static MemoryTrace? Create()
    {
        string configured = OS.GetEnvironment("UG_MEM_TRACE");
        if (configured.Length == 0
            || !double.TryParse(configured, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds <= 0.0)
        {
            return null;
        }
        return new MemoryTrace { Name = "MemoryTrace", _seconds = Math.Clamp(seconds, 0.25, 60.0) };
    }

    public override void _Ready() => Log.Print("[mem] tracing: "
        + $"every {_seconds:0.##}s — rss, gc committed/live, allocated since the previous line");

    public override void _Process(double delta)
    {
        _elapsed += delta;
        if (_elapsed < _seconds)
            return;
        _elapsed = 0.0;
        Print();
    }

    private void Print()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long allocated = GC.GetTotalAllocatedBytes(precise: false);
        long since = allocated - _lastAllocated;
        _lastAllocated = allocated;
        Log.Print($"[mem] t={_since.Elapsed.TotalSeconds:0.0}s rss={ProcessMemory.RssBytes() >> 20}MB "
            + $"committed={info.TotalCommittedBytes >> 20}MB heap={info.HeapSizeBytes >> 20}MB "
            + $"frag={info.FragmentedBytes >> 20}MB live={GC.GetTotalMemory(false) >> 20}MB "
            + $"alloc+={since >> 20}MB gc={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
    }
}
