using System;
using System.Collections.Generic;

namespace UnturnedGodot.Data;

public readonly record struct FoliageGroupKey(int ChunkX, int ChunkY, Guid Asset);

// Compact CSR-like grouping: one flat run array plus offsets replaces one List object/backing array per
// render chunk. The two passes preserve both first-seen chunk order and run order within every chunk.
public sealed class FoliageGroups
{
    public FoliageGroupKey[] Keys { get; }
    public FoliageInstances[] Runs { get; }
    public int[] Starts { get; }
    public int Count => Keys.Length;
    public long StorageBytes => (long)Keys.Length * (sizeof(int) * 2 + 16) +
        (long)Runs.Length * IntPtr.Size + (long)Starts.Length * sizeof(int);

    private FoliageGroups(FoliageGroupKey[] keys, FoliageInstances[] runs, int[] starts)
    {
        Keys = keys;
        Runs = runs;
        Starts = starts;
    }

    public static FoliageGroups Build(IReadOnlyList<FoliageTile> tiles, int chunkTiles,
        IReadOnlySet<Guid> availableAssets)
    {
        var index = new Dictionary<FoliageGroupKey, int>();
        var keys = new List<FoliageGroupKey>();
        var counts = new List<int>();
        var pending = new List<(int Group, FoliageInstances Run)>();
        foreach (FoliageTile tile in tiles)
        {
            int cx = (int)Math.Floor(tile.X / (double)chunkTiles);
            int cy = (int)Math.Floor(tile.Y / (double)chunkTiles);
            foreach (FoliageInstances run in tile.Instances)
            {
                if (!availableAssets.Contains(run.Asset))
                    continue;
                var key = new FoliageGroupKey(cx, cy, run.Asset);
                if (!index.TryGetValue(key, out int group))
                {
                    group = keys.Count;
                    index.Add(key, group);
                    keys.Add(key);
                    counts.Add(0);
                }
                counts[group]++;
                pending.Add((group, run));
            }
        }

        var starts = new int[keys.Count + 1];
        for (int i = 0; i < counts.Count; i++)
            starts[i + 1] = starts[i] + counts[i];
        var cursors = (int[])starts.Clone();
        var runs = new FoliageInstances[starts[^1]];
        foreach ((int group, FoliageInstances run) in pending)
            runs[cursors[group]++] = run;
        return new FoliageGroups(keys.ToArray(), runs, starts);
    }
}
