using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

public readonly record struct FoliageResidencyItem(int Index, Vector3 Centre, float VisibilityRadius);

public sealed record FoliageResidencyPlan(
    IReadOnlyList<int> VisibleMissing,
    IReadOnlyList<int> Prefetch,
    IReadOnlyList<int> Retire,
    bool PrefetchTruncated,
    // How many in-range chunks this plan had to leave out because the pending bound was already spent.
    // PrefetchTruncated only says the cap was hit; this says by how far, which is what sizing the bound
    // against a map's chunk count needs.
    int PrefetchDeferred);

// Pure spatial policy shared by tests and the renderer. VisibilityRadius already contains the authored
// draw distance, chunk extent, and scaled mesh radius; prefetch and hysteresis only add safety/lifecycle
// margins and therefore cannot reduce the current visible set.
public static class FoliageResidencyPlanner
{
    public static FoliageResidencyPlan Plan(Vector3 focus, IReadOnlyList<FoliageResidencyItem> items,
        IReadOnlySet<int> resident, IReadOnlySet<int> pending, float prefetchMargin,
        float unloadHysteresis, int maximumPrefetch)
    {
        if (prefetchMargin < 0f)
            throw new ArgumentOutOfRangeException(nameof(prefetchMargin));
        if (unloadHysteresis < 0f)
            throw new ArgumentOutOfRangeException(nameof(unloadHysteresis));
        if (maximumPrefetch < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPrefetch));

        var visible = new List<(int Index, float DistanceSquared)>();
        var prefetch = new List<(int Index, float DistanceSquared)>();
        var retire = new List<int>();
        foreach (FoliageResidencyItem item in items)
        {
            float distanceSquared = focus.DistanceSquaredTo(item.Centre);
            float visibleRadius = Math.Max(0f, item.VisibilityRadius);
            float loadRadius = visibleRadius + prefetchMargin;
            if (!resident.Contains(item.Index))
            {
                if (distanceSquared <= visibleRadius * visibleRadius)
                    visible.Add((item.Index, distanceSquared));
                else if (!pending.Contains(item.Index) && distanceSquared <= loadRadius * loadRadius)
                    prefetch.Add((item.Index, distanceSquared));
            }
            else
            {
                float unloadRadius = loadRadius + unloadHysteresis;
                if (distanceSquared > unloadRadius * unloadRadius)
                    retire.Add(item.Index);
            }
        }

        visible.Sort(CompareDistance);
        prefetch.Sort(CompareDistance);
        // maximumPrefetch is the renderer's total pending-work bound, not an allowance for this plan
        // alone. Active decodes survive replans and must consume slots, otherwise the caller can stop
        // enqueueing midway through this list without PrefetchTruncated recording the omitted work.
        int availablePrefetch = Math.Max(0, maximumPrefetch - pending.Count);
        bool truncated = prefetch.Count > availablePrefetch;
        int deferred = truncated ? prefetch.Count - availablePrefetch : 0;
        if (truncated)
            prefetch.RemoveRange(availablePrefetch, deferred);
        return new FoliageResidencyPlan(Indices(visible), Indices(prefetch), retire, truncated, deferred);
    }

    // The order and grouping of a warm pass: everything a plan asked for, visible set first, cut into
    // batches whose decoded transforms fit the renderer's own steady-state byte cap. The warm pass decodes
    // a whole batch off the main thread before uploading it, so without this bound a map whose spawn ring
    // is larger than the cap would hold every chunk's transforms at once — the exact peak the streaming
    // renderer exists to avoid. A chunk larger than the whole cap still gets its own batch rather than
    // being dropped: refusing to warm it would only hand it back to the synchronous path this replaces.
    public static IReadOnlyList<int[]> WarmBatches(FoliageResidencyPlan plan, Func<int, long> decodedBytes,
        long byteLimit)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(decodedBytes);
        if (byteLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLimit));

        var batches = new List<int[]>();
        var current = new List<int>();
        long bytes = 0;
        foreach (int index in Ordered(plan))
        {
            long size = Math.Max(0, decodedBytes(index));
            if (current.Count > 0 && bytes + size > byteLimit)
            {
                batches.Add(current.ToArray());
                current.Clear();
                bytes = 0;
            }
            current.Add(index);
            bytes += size;
        }
        if (current.Count > 0)
            batches.Add(current.ToArray());
        return batches;
    }

    // Visible before prefetch: both lists are already near-first, and the visible set is the one whose
    // absence costs a synchronous decode on the frame the player appears.
    private static IEnumerable<int> Ordered(FoliageResidencyPlan plan)
    {
        foreach (int index in plan.VisibleMissing)
            yield return index;
        foreach (int index in plan.Prefetch)
            yield return index;
    }

    private static int CompareDistance((int Index, float DistanceSquared) a,
        (int Index, float DistanceSquared) b)
    {
        int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
        return distance != 0 ? distance : a.Index.CompareTo(b.Index);
    }

    private static int[] Indices(List<(int Index, float DistanceSquared)> values)
    {
        var result = new int[values.Count];
        for (int i = 0; i < result.Length; i++) result[i] = values[i].Index;
        return result;
    }
}
