using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// Splits a large baked flag into spatially local, bounded navigation regions. Godot 4.7 prepares each
// NavigationMesh on a worker; handing it an entire large-map flag at once can leave that worker merging
// polygons for minutes and make engine shutdown wait forever. Exact vertex positions are retained so the
// NavigationServer's edge connections can join neighbouring chunks without changing the authored graph.
public sealed record NavmeshRegionData(Vector3[] Vertices, int[] Triangles, int SourceTriangleCount);

public static class NavmeshPartition
{
    public const float DefaultCellSize = 128f;
    public const int DefaultMaxTriangles = 4096;

    public static List<NavmeshRegionData> Build(NavFlag flag, IReadOnlySet<int>? skip = null,
        float cellSize = DefaultCellSize, int maxTriangles = DefaultMaxTriangles)
    {
        if (cellSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (maxTriangles <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTriangles));

        var buckets = new SortedDictionary<(int X, int Z), List<int>>();
        int triangleCount = flag.Triangles.Length / 3;
        for (int triangle = 0; triangle < triangleCount; triangle++)
        {
            if (skip?.Contains(triangle) == true)
                continue;

            Vector3 a = flag.Vertices[flag.Triangles[triangle * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(triangle * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(triangle * 3) + 2]];
            float x = (a.X + b.X + c.X) / 3f;
            float z = (a.Z + b.Z + c.Z) / 3f;
            var key = ((int)MathF.Floor(x / cellSize), (int)MathF.Floor(z / cellSize));
            if (!buckets.TryGetValue(key, out List<int>? list))
                buckets[key] = list = new List<int>();
            list.Add(triangle);
        }

        var regions = new List<NavmeshRegionData>();
        foreach (List<int> bucket in buckets.Values)
            for (int start = 0; start < bucket.Count; start += maxTriangles)
                regions.Add(BuildRegion(flag, bucket, start, Math.Min(maxTriangles, bucket.Count - start)));
        return regions;
    }

    private static NavmeshRegionData BuildRegion(NavFlag flag, List<int> source, int start, int count)
    {
        var localByGlobal = new Dictionary<int, int>();
        var vertices = new List<Vector3>();
        var triangles = new int[count * 3];
        int output = 0;
        for (int i = 0; i < count; i++)
        {
            int sourceTriangle = source[start + i];
            for (int corner = 0; corner < 3; corner++)
            {
                int global = flag.Triangles[(sourceTriangle * 3) + corner];
                if (!localByGlobal.TryGetValue(global, out int local))
                {
                    local = vertices.Count;
                    localByGlobal[global] = local;
                    vertices.Add(flag.Vertices[global]);
                }
                triangles[output++] = local;
            }
        }
        return new NavmeshRegionData(vertices.ToArray(), triangles, count);
    }
}
