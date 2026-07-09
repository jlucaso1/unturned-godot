using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One nav flag's PRE-BAKED navmesh, deserialized from Environment/Navigation_<N>.dat — the exact
// triangles Unturned's editor baked (A* Pathfinding Project RecastGraph tiles; format decompiled
// from UnturnedNavmesh_ASPFP.Serialize). Everything is converted to Godot space here: coordinates
// mirror Z and scale from Int3 millimetres to metres, and the per-tile meshes are welded into one
// indexed mesh so polygons connect across tile borders.
public sealed class NavFlag
{
    // The NON-expanded navmesh bounds (forcedBounds): LevelNavigation.checkNavigation tests these,
    // while Bounds.dat carries the same boxes expanded by BOUNDS_SIZE (64 m).
    public Vector3 Center;
    public Vector3 Size;

    public Vector3[] Vertices = Array.Empty<Vector3>();
    public int[] Triangles = Array.Empty<int>(); // 3 indices per triangle

    // LevelNavigation.checkNavigation's per-flag test: the point sits inside the non-expanded box.
    public bool ContainsXZ(Vector3 point) =>
        Mathf.Abs(point.X - Center.X) <= Size.X * 0.5f &&
        Mathf.Abs(point.Z - Center.Z) <= Size.Z * 0.5f;
}

public static class LevelNavmesh
{
    // LevelNavigation.load scans Navigation_<i>.dat by index and stops after five consecutive
    // missing files (maps may have gaps where flags were deleted in the editor).
    public static List<NavFlag> Load(string environmentDir)
    {
        var flags = new List<NavFlag>();
        int consecutiveNotFound = 0;
        for (int index = 0; consecutiveNotFound < 5; index++)
        {
            string path = Path.Combine(environmentDir, $"Navigation_{index}.dat");
            if (!File.Exists(path))
            {
                consecutiveNotFound++;
                continue;
            }
            consecutiveNotFound = 0;
            NavFlag? flag = Read(path);
            if (flag != null)
                flags.Add(flag);
        }
        return flags;
    }

    // UnturnedNavmesh_ASPFP.Deserialize, byte for byte: version, forcedBounds center+size,
    // tileXCount/tileZCount, then per tile (z-major) the ushort-counted triangle indices and
    // Int3 world-space vertices (millimetres).
    public static NavFlag? Read(string path)
    {
        using var river = new River(path);
        byte version = river.ReadByte();
        if (version == 0)
            return null;

        Vector3 center = river.ReadSingleVector3();
        Vector3 size = river.ReadSingleVector3();
        center.Z = -center.Z; // Unity -> Godot mirror (sizes are mirror-invariant)
        byte tileXCount = river.ReadByte();
        byte tileZCount = river.ReadByte();

        // Weld identical vertices across tiles by their exact Int3 key: RecastGraph tiles duplicate
        // the vertices along shared borders, and Godot only connects polygons over shared indices.
        var vertexIndex = new Dictionary<(int X, int Y, int Z), int>();
        var vertices = new List<Vector3>();
        var triangles = new List<int>();

        int tileCount = tileXCount * tileZCount;
        for (int t = 0; t < tileCount; t++)
        {
            ushort indexCount = river.ReadUInt16();
            var tris = new ushort[indexCount];
            for (int i = 0; i < indexCount; i++)
                tris[i] = river.ReadUInt16();

            ushort vertCount = river.ReadUInt16();
            var map = new int[vertCount];
            for (int v = 0; v < vertCount; v++)
            {
                int x = river.ReadInt32();
                int y = river.ReadInt32();
                int z = river.ReadInt32();
                (int, int, int) key = (x, y, -z); // mirror in the integer domain: exact welds
                if (!vertexIndex.TryGetValue(key, out int welded))
                {
                    welded = vertices.Count;
                    vertexIndex[key] = welded;
                    vertices.Add(new Vector3(x / 1000f, y / 1000f, -z / 1000f));
                }
                map[v] = welded;
            }

            // The Z-mirror flips the winding; reverse each triangle so the surface keeps facing up.
            for (int i = 0; i + 2 < indexCount; i += 3)
            {
                triangles.Add(map[tris[i]]);
                triangles.Add(map[tris[i + 2]]);
                triangles.Add(map[tris[i + 1]]);
            }
        }

        return new NavFlag
        {
            Center = center,
            Size = size,
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    // LevelNavigation.checkNavigation: is the point inside ANY flag's non-expanded navmesh box?
    public static bool CheckNavigation(IReadOnlyList<NavFlag> flags, Vector3 point)
    {
        for (int i = 0; i < flags.Count; i++)
            if (flags[i].ContainsXZ(point))
                return true;
        return false;
    }

    // Projects a point onto the navmesh with an XZ-first rule: prefer the triangle directly under
    // or over the point on the closest LEVEL (smallest |dy|); when no triangle contains the XZ,
    // take the closest triangle edge in XZ. A hunt target standing just off the mesh then snaps to
    // a stable nearby point on its own floor — a raw 3D closest-point query flips between the
    // street beside it and a basement three metres below as things move, which zigzags the route.
    public static bool SnapXZ(IReadOnlyList<NavFlag> flags, Vector3 point, out Vector3 snapped)
    {
        snapped = point;
        float bestContainedDy = float.MaxValue;
        float bestEdgeScore = float.MaxValue;
        Vector3 bestEdge = point;
        bool contained = false;

        foreach (NavFlag flag in flags)
        {
            // Cheap reject: outside the flag's box (with a margin for the tile overhang).
            if (Mathf.Abs(point.X - flag.Center.X) > (flag.Size.X * 0.5f) + 16f
                || Mathf.Abs(point.Z - flag.Center.Z) > (flag.Size.Z * 0.5f) + 16f)
                continue;

            Vector3[] v = flag.Vertices;
            int[] t = flag.Triangles;
            for (int i = 0; i + 2 < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                // Degenerate (zero-area) triangles "contain" every point under the sign rule and
                // would hijack the snap; with them gone, every remaining edge has real length.
                float area2 = (((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z)));
                if (MathF.Abs(area2) < 1e-6f)
                    continue;
                float d1 = ((point.X - b.X) * (a.Z - b.Z)) - ((a.X - b.X) * (point.Z - b.Z));
                float d2 = ((point.X - c.X) * (b.Z - c.Z)) - ((b.X - c.X) * (point.Z - c.Z));
                float d3 = ((point.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (point.Z - a.Z));
                bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
                bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
                if (!(neg && pos))
                {
                    float y = (a.Y + b.Y + c.Y) / 3f;
                    float dy = Mathf.Abs(y - point.Y);
                    if (dy < bestContainedDy)
                    {
                        bestContainedDy = dy;
                        snapped = new Vector3(point.X, y, point.Z);
                        contained = true;
                    }
                }
                else if (!contained)
                {
                    // Closest point on the triangle's edges in XZ, with a light vertical penalty
                    // so same-level geometry wins over floors above/below.
                    ClosestOnEdge(a, b, point, ref bestEdgeScore, ref bestEdge);
                    ClosestOnEdge(b, c, point, ref bestEdgeScore, ref bestEdge);
                    ClosestOnEdge(c, a, point, ref bestEdgeScore, ref bestEdge);
                }
            }
        }

        if (contained)
            return true;
        if (bestEdgeScore < float.MaxValue)
        {
            snapped = bestEdge;
            return true;
        }
        return false;
    }

    private static void ClosestOnEdge(Vector3 a, Vector3 b, Vector3 point,
        ref float bestScore, ref Vector3 best)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z;
        // Non-degenerate triangles (the caller filters them) can't have zero-length edges.
        float lengthSquared = (dx * dx) + (dz * dz);
        float t = Mathf.Clamp((((point.X - a.X) * dx) + ((point.Z - a.Z) * dz)) / lengthSquared, 0f, 1f);
        Vector3 candidate = a.Lerp(b, t);
        float ddx = point.X - candidate.X, ddz = point.Z - candidate.Z;
        float dy = point.Y - candidate.Y;
        float score = (ddx * ddx) + (ddz * ddz) + (0.25f * dy * dy);
        if (score < bestScore)
        {
            bestScore = score;
            best = candidate;
        }
    }
}
