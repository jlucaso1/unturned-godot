using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    private NavFlagSnapIndex? _snapIndex;

    // The XZ bucketing SnapXZ walks instead of every triangle in the flag. Built on demand rather than
    // in the reader, because Vertices and Triangles are assignable and a great deal of code — the
    // survey, the editor dock, the repro harness, every test — builds a flag by hand rather than by
    // reading one. Keyed on the two arrays by reference, so a flag whose geometry is replaced indexes
    // the new geometry instead of answering from the old.
    //
    // Racing callers may each build one. They are immutable and equivalent, so whichever write lands
    // last is as good as the other; the alternative is a lock on the hot path of every repath.
    internal NavFlagSnapIndex SnapIndex
    {
        get
        {
            NavFlagSnapIndex? index = Volatile.Read(ref _snapIndex);
            if (index != null && index.Describes(Vertices, Triangles))
                return index;
            index = NavFlagSnapIndex.Build(Vertices, Triangles);
            Volatile.Write(ref _snapIndex, index);
            return index;
        }
    }
}

// Uniform XZ buckets over one flag's triangles, in CSR form: a triangle is entered in every cell its XZ
// bounding box touches, so the cell holding a point holds every triangle that could contain it, and the
// rings around that cell reach everything that could be nearest to it.
//
// This is the same index BakedNavGraph builds for the same question one layer down, and for the same
// reason it records there: "California 2 has 266k triangles across its flags and every repath snaps two
// endpoints; the scan alone was half a million containment tests per query, several times a second per
// zombie." That index cannot be borrowed, because it holds only the triangles the collision
// reconciliation left enabled and SnapXZ deliberately ignores that mask — it answers "where is the
// baked ground here", not "where may this body walk".
//
// Costs about two ints per triangle (PEI's 42,642 triangles index in roughly 340 KB across its 19
// flags), which is why it is built on the first snap rather than on every load: a session that never
// hosts zombies never pays for it.
internal sealed class NavFlagSnapIndex
{
    private readonly Vector3[] _vertices;
    private readonly int[] _triangles;
    private readonly int[] _cellStart;
    private readonly int[] _cellItems;
    private readonly int _side;
    private readonly float _cellSize;
    private readonly float _minX;
    private readonly float _minZ;

    // How far out the rings can usefully go from a cell: enough to reach every other cell in the grid.
    public int Reach(int column, int row) => Math.Max(
        Math.Max(column, _side - 1 - column), Math.Max(row, _side - 1 - row));

    public int Side => _side;

    // The closest any point in the cell holding the query can be to any point in a cell `ring` cells
    // away: the rings between them are whole cells wide. Zero for the first two rings, where the two
    // cells touch. This is what lets the walk stop — a triangle registered in a further ring is at
    // least this far off in XZ, and SnapXZ's score is never smaller than the XZ distance squared.
    public float NearestAtRing(int ring) => (ring - 1) * _cellSize;

    public bool IsEmpty => _cellItems.Length == 0;

    private NavFlagSnapIndex(Vector3[] vertices, int[] triangles, int[] cellStart, int[] cellItems,
        int side, float cellSize, float minX, float minZ)
    {
        _vertices = vertices;
        _triangles = triangles;
        _cellStart = cellStart;
        _cellItems = cellItems;
        _side = side;
        _cellSize = cellSize;
        _minX = minX;
        _minZ = minZ;
    }

    public bool Describes(Vector3[] vertices, int[] triangles) =>
        ReferenceEquals(_vertices, vertices) && ReferenceEquals(_triangles, triangles);

    public int Column(float x) => Math.Clamp((int)((x - _minX) / _cellSize), 0, _side - 1);

    public int Row(float z) => Math.Clamp((int)((z - _minZ) / _cellSize), 0, _side - 1);

    // The triangles registered in one cell, in ascending order — the order the flat scan visited them
    // in, which is what makes a tie between two of them break the same way here as it did there.
    public ReadOnlySpan<int> Cell(int column, int row)
    {
        int cell = (row * _side) + column;
        int start = _cellStart[cell];
        return _cellItems.AsSpan(start, _cellStart[cell + 1] - start);
    }

    public static NavFlagSnapIndex Build(Vector3[] vertices, int[] triangles)
    {
        int count = triangles.Length / 3;
        if (count == 0 || vertices.Length == 0)
            return new NavFlagSnapIndex(vertices, triangles, Array.Empty<int>(), Array.Empty<int>(),
                0, 1f, 0f, 0f);

        float minX = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxZ = float.MinValue;
        foreach (Vector3 vertex in vertices)
        {
            minX = MathF.Min(minX, vertex.X);
            maxX = MathF.Max(maxX, vertex.X);
            minZ = MathF.Min(minZ, vertex.Z);
            maxZ = MathF.Max(maxZ, vertex.Z);
        }

        // A few triangles per cell without letting a huge flag explode the grid — BakedNavGraph's own
        // sizing, kept the same so the two indexes over the same geometry have the same shape.
        int side = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(count / 4f)), 1, 256);
        float cellSize = MathF.Max(MathF.Max(maxX - minX, maxZ - minZ) / side, 0.001f);

        // Degenerate triangles are indexed with the rest. They can never win a snap — SnapXZ rejects
        // them on area before it looks at anything else — but leaving them out would make that
        // rejection unreachable from the indexed path, and the reject is the reason a zero-area face
        // cannot hijack the answer by "containing" every point under the sign rule.
        var counts = new int[(side * side) + 1];
        int[] cellStart = Array.Empty<int>();
        int[] cellItems = Array.Empty<int>();
        for (int pass = 0; pass < 2; pass++)
        {
            for (int triangle = 0; triangle < count; triangle++)
            {
                Vector3 a = vertices[triangles[triangle * 3]];
                Vector3 b = vertices[triangles[(triangle * 3) + 1]];
                Vector3 c = vertices[triangles[(triangle * 3) + 2]];
                int x0 = Bucket(MathF.Min(a.X, MathF.Min(b.X, c.X)), minX, cellSize, side);
                int x1 = Bucket(MathF.Max(a.X, MathF.Max(b.X, c.X)), minX, cellSize, side);
                int z0 = Bucket(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)), minZ, cellSize, side);
                int z1 = Bucket(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)), minZ, cellSize, side);
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = (z * side) + x;
                        if (pass == 0)
                            counts[cell + 1]++;
                        else
                            cellItems[cellStart[cell] + counts[cell]++] = triangle;
                    }
            }

            if (pass != 0)
                continue;

            cellStart = new int[(side * side) + 1];
            for (int cell = 0; cell < side * side; cell++)
                cellStart[cell + 1] = cellStart[cell] + counts[cell + 1];
            cellItems = new int[cellStart[side * side]];
            Array.Clear(counts);
        }

        return new NavFlagSnapIndex(vertices, triangles, cellStart, cellItems, side, cellSize,
            minX, minZ);
    }

    private static int Bucket(float value, float origin, float cellSize, int side) =>
        Math.Clamp((int)((value - origin) / cellSize), 0, side - 1);
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
        try
        {
            return ReadFlag(path);
        }
        catch (EndOfStreamException)
        {
            // A truncated flag is DROPPED, not kept in part — which is the opposite of what Objects.dat
            // does with the same damage, and deliberately so. A placement before a cut is a whole
            // placement; half a flag's triangle list is not half a navmesh, it is a navmesh with holes
            // that correspond to nothing in the world. Zombies would path confidently through whatever
            // the reader failed to see.
            //
            // Dropping it puts the map back on the road every map without a navmesh takes: zombies steer
            // directly. That is worse than pathing and much better than pathing wrongly.
            GD.PushWarning($"[nav] {Path.GetFileName(path)} ends mid-flag; dropping it, so this region "
                + "steers directly instead of pathing");
            return null;
        }
    }

    private static NavFlag? ReadFlag(string path)
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
    // Walked over each flag's XZ buckets rather than over every triangle in it. The flat scan this
    // replaces visited every triangle of every candidate flag, and did NOT stop when it found one
    // containing the point — it ran to the end looking for the smallest |dy|, and for each triangle
    // that did not contain the point it ran three edge projections. On PEI that is 42,642 triangles
    // per snap and two snaps per repath; on California 2 it is 266k. Measured on PEI (PerfHarness --
    // navsnap), one snap cost ~0.064 ms, so a saturated repath budget cost ~1.0 ms of an 80 ms server
    // tick — none of it inside the ~1.3 ms MapGetPath median that ZombieSystem prices its budget
    // against, because it runs before the query is issued.
    //
    // The scoring is unchanged, deliberately and testably so: the answer this returns is the same
    // point the flat scan returned, down to the tie-breaks. Two things make that true.
    //
    // First, the two branches are separated into two passes. In the flat scan the edge branch is
    // guarded by `!contained`, so once any triangle contained the point the edge candidates stopped
    // being updated — and if none ever did, the guard was never false and the branch ran for every
    // triangle. So "no triangle anywhere contains the point" is exactly the case the edge answer is
    // used in, and running the two as separate passes computes the same two answers.
    //
    // Second, the buckets are visited in a different order than the flat scan, so ties cannot be left
    // to whoever is seen first. A tie is settled explicitly, on the flag and triangle the candidate
    // came from, which is the order the flat scan would have met them in. Nothing outside the walk can
    // tie: a triangle beyond the ring the walk stops at scores strictly worse than what is already in
    // hand, because its XZ distance alone already exceeds it.
    public static bool SnapXZ(IReadOnlyList<NavFlag> flags, Vector3 point, out Vector3 snapped)
    {
        snapped = point;
        float bestContainedDy = float.MaxValue;
        int containedFlag = -1, containedTriangle = -1;

        // Pass one: the triangle the point stands on or under, on the closest level. Every such
        // triangle has the point inside its XZ bounding box, so all of them are registered in the one
        // cell the point falls in and nothing outside that cell has to be looked at.
        for (int f = 0; f < flags.Count; f++)
        {
            NavFlag flag = flags[f];
            if (!Candidate(flag, point))
                continue;
            NavFlagSnapIndex index = flag.SnapIndex;
            if (index.IsEmpty)
                continue;

            Vector3[] v = flag.Vertices;
            int[] t = flag.Triangles;
            foreach (int triangle in index.Cell(index.Column(point.X), index.Row(point.Z)))
            {
                int i = triangle * 3;
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                // Degenerate (zero-area) triangles "contain" every point under the sign rule and
                // would hijack the snap; with them gone, every remaining edge has real length.
                float area2 = (((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z)));
                if (MathF.Abs(area2) < 1e-6f)
                    continue;
                if (!ContainsXZ(a, b, c, point))
                    continue;
                float y = HeightAt(a, b, c, area2, point);
                float dy = Mathf.Abs(y - point.Y);
                if (dy < bestContainedDy
                    || (dy == bestContainedDy && f == containedFlag && triangle < containedTriangle))
                {
                    bestContainedDy = dy;
                    containedFlag = f;
                    containedTriangle = triangle;
                    snapped = new Vector3(point.X, y, point.Z);
                }
            }
        }

        if (containedFlag >= 0)
            return true;

        // Pass two: nothing covers this XZ, so take the closest triangle edge instead — with a light
        // vertical penalty, so same-level geometry wins over the floors above and below it.
        float bestEdgeScore = float.MaxValue;
        Vector3 bestEdge = point;
        int edgeFlag = -1, edgeTriangle = -1;
        for (int f = 0; f < flags.Count; f++)
        {
            NavFlag flag = flags[f];
            if (!Candidate(flag, point))
                continue;
            NavFlagSnapIndex index = flag.SnapIndex;
            if (index.IsEmpty)
                continue;

            Vector3[] v = flag.Vertices;
            int[] t = flag.Triangles;
            int centreColumn = index.Column(point.X), centreRow = index.Row(point.Z);
            int reach = index.Reach(centreColumn, centreRow);
            for (int ring = 0; ring <= reach; ring++)
            {
                // Every point of a triangle lies inside its own bounding box, so a triangle whose
                // closest edge point is `d` away in XZ is registered in a cell no further out than
                // ring d/cell + 1. The score is that distance squared plus a non-negative vertical
                // term, so once this ring's nearest possible cell is further than the best score,
                // nothing beyond it can match — let alone beat — what is already held.
                if (ring > 0)
                {
                    float nearest = index.NearestAtRing(ring);
                    if (nearest > 0f && nearest * nearest > bestEdgeScore)
                        break;
                }

                for (int row = centreRow - ring; row <= centreRow + ring; row++)
                {
                    if (row < 0 || row >= index.Side)
                        continue;
                    bool edgeRow = row == centreRow - ring || row == centreRow + ring;
                    for (int column = centreColumn - ring; column <= centreColumn + ring; column++)
                    {
                        if (column < 0 || column >= index.Side)
                            continue;
                        if (!edgeRow && column != centreColumn - ring && column != centreColumn + ring)
                            continue; // the inside of the ring was covered by a smaller one

                        foreach (int triangle in index.Cell(column, row))
                        {
                            int i = triangle * 3;
                            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                            float area2 = (((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z)));
                            if (MathF.Abs(area2) < 1e-6f)
                                continue;
                            // The flat scan only reached its edge branch for triangles that did not
                            // contain the point. Nothing here does — pass one would have returned —
                            // but a triangle registered in a neighbouring cell can still cover a
                            // point this one does not, so the same rejection has to stand.
                            if (ContainsXZ(a, b, c, point))
                                continue;
                            ClosestOnEdge(a, b, point, f, triangle,
                                ref bestEdgeScore, ref bestEdge, ref edgeFlag, ref edgeTriangle);
                            ClosestOnEdge(b, c, point, f, triangle,
                                ref bestEdgeScore, ref bestEdge, ref edgeFlag, ref edgeTriangle);
                            ClosestOnEdge(c, a, point, f, triangle,
                                ref bestEdgeScore, ref bestEdge, ref edgeFlag, ref edgeTriangle);
                        }
                    }
                }
            }
        }

        if (edgeFlag >= 0)
        {
            snapped = bestEdge;
            return true;
        }
        return false;
    }

    // Cheap reject: outside the flag's box (with a margin for the tile overhang).
    private static bool Candidate(NavFlag flag, Vector3 point) =>
        Mathf.Abs(point.X - flag.Center.X) <= (flag.Size.X * 0.5f) + 16f
        && Mathf.Abs(point.Z - flag.Center.Z) <= (flag.Size.Z * 0.5f) + 16f;

    // The sign rule the flat scan used, unchanged: the point is on the same side of all three edges,
    // or on one of them. Split out so the two passes cannot drift apart.
    private static bool ContainsXZ(in Vector3 a, in Vector3 b, in Vector3 c, Vector3 point)
    {
        float d1 = ((point.X - b.X) * (a.Z - b.Z)) - ((a.X - b.X) * (point.Z - b.Z));
        float d2 = ((point.X - c.X) * (b.Z - c.Z)) - ((b.X - c.X) * (point.Z - c.Z));
        float d3 = ((point.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (point.Z - a.Z));
        bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
        bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
        return !(neg && pos);
    }

    // The surface height under the point, not the triangle's average. A baked face can be tens of metres
    // across and carry the whole rise of a ramp or hillside, so the centroid is not the ground anywhere
    // except the middle: on a 40 m face rising 8 m, every point on it snapped to 2.67 — 2.5 m too high at
    // the bottom edge and 5.1 m too low at the top corner, a 7.8 m spread collapsed to one number.
    //
    // That number is also what the dy comparison above uses to choose between triangles that overlap in
    // XZ, which is the "do not snap onto another storey" rule the callers rely on. Deciding it against a
    // face's average rather than the ground under the point is exactly wrong on the geometry that has
    // storeys in the first place — stairs and ramps.
    //
    // Barycentric in XZ; the denominator is twice the signed area, which is the area2 the caller already
    // computed and already rejected for being degenerate.
    private static float HeightAt(in Vector3 a, in Vector3 b, in Vector3 c, float area2, Vector3 point)
    {
        float w1 = (((b.Z - c.Z) * (point.X - c.X)) + ((c.X - b.X) * (point.Z - c.Z))) / area2;
        float w2 = (((c.Z - a.Z) * (point.X - c.X)) + ((a.X - c.X) * (point.Z - c.Z))) / area2;
        return (w1 * a.Y) + (w2 * b.Y) + ((1f - w1 - w2) * c.Y);
    }

    // `flag` and `triangle` are where this edge came from, and they exist only to settle a tie. The
    // flat scan resolved one by whoever it happened to meet first, which was the lowest triangle index
    // of the lowest flag; the buckets are walked in a different order, so that has to be said out loud
    // instead of falling out of the loop. Flags are still walked in order, so a tie against an earlier
    // flag's candidate is rejected by the flag test alone, exactly as `score < bestScore` used to
    // reject it. Ties between the three edges of one triangle are rejected the same way — the first of
    // them wins, which is what the strict comparison did.
    private static void ClosestOnEdge(Vector3 a, Vector3 b, Vector3 point, int flag, int triangle,
        ref float bestScore, ref Vector3 best, ref int bestFlag, ref int bestTriangle)
    {
        float dx = b.X - a.X, dz = b.Z - a.Z;
        // Non-degenerate triangles (the caller filters them) can't have zero-length edges.
        float lengthSquared = (dx * dx) + (dz * dz);
        float t = Mathf.Clamp((((point.X - a.X) * dx) + ((point.Z - a.Z) * dz)) / lengthSquared, 0f, 1f);
        Vector3 candidate = a.Lerp(b, t);
        float ddx = point.X - candidate.X, ddz = point.Z - candidate.Z;
        float dy = point.Y - candidate.Y;
        float score = (ddx * ddx) + (ddz * ddz) + (0.25f * dy * dy);
        if (score < bestScore
            || (score == bestScore && flag == bestFlag && triangle < bestTriangle))
        {
            bestScore = score;
            best = candidate;
            bestFlag = flag;
            bestTriangle = triangle;
        }
    }
}
