using System;
using System.Collections.Generic;

namespace UnturnedGodot.Data;

// Incremental XZ partitioner used while placements are produced. It avoids first materializing one
// map-wide list and then copying every value into its final cell; insertion order within each cell is
// stable and negative coordinates use mathematical floor, matching the world partition rules.
public sealed class SpatialBuckets<T>
{
    private readonly float _cellSize;
    private readonly Dictionary<(int X, int Z), List<T>> _groups = new();
    public IReadOnlyDictionary<(int X, int Z), List<T>> Groups => _groups;
    public int Count { get; private set; }

    public SpatialBuckets(float cellSize)
    {
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        _cellSize = cellSize;
    }

    public void Add(float x, float z, T value)
    {
        var cell = ((int)MathF.Floor(x / _cellSize), (int)MathF.Floor(z / _cellSize));
        if (!_groups.TryGetValue(cell, out List<T>? values))
            _groups[cell] = values = new List<T>();
        values.Add(value);
        Count++;
    }

    public List<T> Flatten()
    {
        var result = new List<T>(Count);
        foreach (List<T> values in _groups.Values) result.AddRange(values);
        return result;
    }
}
