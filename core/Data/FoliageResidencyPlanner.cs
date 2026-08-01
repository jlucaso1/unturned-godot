using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

public readonly record struct FoliageResidencyItem(int Index, Vector3 Centre, float VisibilityRadius);

public sealed record FoliageResidencyPlan(
    IReadOnlyList<int> VisibleMissing,
    IReadOnlyList<int> Prefetch,
    IReadOnlyList<int> Retire,
    bool PrefetchTruncated);

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
            else if (resident.Contains(item.Index))
            {
                float unloadRadius = loadRadius + unloadHysteresis;
                if (distanceSquared > unloadRadius * unloadRadius)
                    retire.Add(item.Index);
            }
        }

        visible.Sort(CompareDistance);
        prefetch.Sort(CompareDistance);
        bool truncated = prefetch.Count > maximumPrefetch;
        if (truncated)
            prefetch.RemoveRange(maximumPrefetch, prefetch.Count - maximumPrefetch);
        return new FoliageResidencyPlan(Indices(visible), Indices(prefetch), retire, truncated);
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
