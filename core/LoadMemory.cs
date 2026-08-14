using System;
using System.Diagnostics;
using System.IO;

namespace UnturnedGodot;

// Hands a one-time load's transient heap back to the OS.
//
// Loading allocates in bursts nothing else in the session comes close to — mesh-cache reads, the foliage
// transform pool, native Godot.Collections.Array copies, and on a cold load the whole-bundle decode. The
// workstation collector keeps the segments it grew for those rather than returning them, so steady-state
// RSS stays inflated long after the work is done: a cold PEI session sat ~390 MB above a warm one for as
// long as it ran, with a 400 MB heap holding 12 MB of live objects.
//
// So every piece of one-time work compacts once when it finishes, off the steady frame loop. "When it
// finishes" is the part that matters: a reclaim that runs before the last of that work — as the loader's
// did, ahead of the deferred audio extraction — leaves exactly the transient it was meant to return.
public static class LoadMemory
{
    public static void Reclaim(string what)
    {
        var sw = Stopwatch.StartNew();
        long before = ProcessRssMib();
        int passes = int.TryParse(Environment.GetEnvironmentVariable("UG_RECLAIM_PASSES"), out int configured)
            ? Math.Clamp(configured, 0, 2)
            : 1;
        // Zero is the A/B control: it prices the compaction itself, which matters most for the reclaims
        // that land after the player is already moving. Not a shipped setting — without it a session
        // keeps every segment the one-time work grew.
        if (passes == 0)
        {
            HostLog.Print($"[mem] {what} reclaim: skipped (UG_RECLAIM_PASSES=0), RSS {before} MB");
            return;
        }
        // Re-armed for each pass: CompactOnce applies to the NEXT blocking gen-2 collection and then
        // resets itself, so the two-pass control was measuring a second collection that left the large
        // object heap alone — not the two compactions it is documented as.
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        if (passes == 2)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        if (before > 0) // Linux only (from /proc); skip the line where RSS is unavailable
            HostLog.Print($"[mem] {what} reclaim: RSS {before} -> {ProcessRssMib()} MB " +
                $"(managed {GC.GetTotalMemory(false) >> 20} MB, {passes} pass(es)) " +
                $"in {sw.ElapsedMilliseconds} ms");
    }

    // VmRSS from /proc/self/status in MiB (Linux); 0 elsewhere. Diagnostic only.
    public static long ProcessRssMib()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("VmRSS:", StringComparison.Ordinal))
                    return long.Parse(line.Split(':')[1].Trim().Split(' ')[0]) / 1024;
        }
        catch (Exception) { /* non-Linux or unreadable — diagnostic only */ }
        return 0;
    }
}
