using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Godot;

namespace UnturnedGodot.Data;

// CPU path graph for very large pre-baked maps. Godot's NavigationServer performs a global polygon merge
// even when its input is split into small regions; at California2's 266k triangles that merge can occupy
// one engine worker for minutes and prevents process teardown. The source data is already a baked graph,
// so large maps do not need to be rasterized/merged again: shared indexed edges are its exact adjacency.
public sealed class BakedNavGraph
{
    private const uint CacheMagic = 0x43424755; // UGBC
    // Bump whenever the TOPOLOGY this builds changes, not just the blob's layout. The fingerprint
    // covers the map and the reconciliation inputs, so an unchanged map on an upgraded server matches
    // its old cache and is loaded verbatim — with none of the newer construction ever running.
    // 2: faces meeting along a line without agreeing where it ends are now joined, which is 1784 extra
    //    connections on PEI. A v1 cache carries every one of those seams still severed.
    private const int CacheVersion = 2;
    private const float EndpointSnapMargin = 64f; // Bounds.dat expands authored navigation by this distance

    // The body the routes are for. A route is a line for a point, but a zombie is a capsule, and the
    // funnel string-pulls to portal ENDPOINTS — which are mesh vertices, and at a doorway that vertex is
    // the jamb. Aiming a 0.4 m capsule at a point on the wall is what left them grinding there.
    //
    // It lives here, in Data, and ZombieInstance.Radius reads it, rather than the two carrying their own
    // 0.4f: the graph is built once for the level and cannot be per-speciality, so if the capsule is ever
    // resized the routes have to move with it or they go back to aiming at walls. Megas are wider (0.75)
    // and are not modelled — a mega does not fit through a house door in the original either.
    public const float AgentRadius = 0.4f;
    private readonly List<FlagGraph> _flags;

    private BakedNavGraph(List<FlagGraph> flags) => _flags = flags;

    public static BakedNavGraph Build(IReadOnlyList<NavFlag> flags,
        IReadOnlyDictionary<NavFlag, HashSet<int>>? unreachable = null)
    {
        var built = new List<FlagGraph>(flags.Count);
        foreach (NavFlag flag in flags)
        {
            HashSet<int>? skip = null;
            unreachable?.TryGetValue(flag, out skip);
            built.Add(new FlagGraph(flag, skip));
        }
        return new BakedNavGraph(built);
    }

    // `radius` is the body the route is FOR. It defaults to the ordinary zombie so a caller that has no
    // particular body in mind still gets a usable route, but a mega is nearly twice as wide and a route
    // built for 0.40 walks its capsule 0.30 m into every jamb it passes.
    public bool TryPath(Vector3 from, Vector3 to, List<Vector3> path, float radius = AgentRadius)
    {
        // Navigation flags are separate authored graphs. The start must belong to the graph, but a target
        // may stand in Bounds.dat's 64 m expansion around it and snap to the nearest reachable face. If the
        // target is inside another authored flag, do not silently route it on this disconnected graph.
        FlagGraph? best = null;
        int bestFrom = -1, bestTo = -1;
        Vector3 bestDestination = to;
        float bestScore = float.MaxValue;
        bool destinationInsideAny = false;
        foreach (FlagGraph flag in _flags)
            if (flag.Source.ContainsXZ(to))
            {
                destinationInsideAny = true;
                break;
            }

        foreach (FlagGraph flag in _flags)
        {
            if (!flag.Source.ContainsXZ(from))
                continue;
            bool destinationInside = flag.Source.ContainsXZ(to);
            if (!destinationInside
                && (destinationInsideAny || !ContainsExpandedXZ(flag.Source, to, EndpointSnapMargin)))
                continue;
            int a = flag.ClosestTriangle(from, out float fromScore);
            int b = flag.ClosestTriangle(to, out float toScore);
            if (a >= 0 && b >= 0 && fromScore + toScore < bestScore)
            {
                best = flag;
                bestFrom = a;
                bestTo = b;
                bestDestination = destinationInside ? to : flag.ClosestPointXZ(b, to);
                bestScore = fromScore + toScore;
            }
        }
        return best != null && best.FindPath(bestFrom, bestTo, from, bestDestination, path, radius);
    }

    private static bool ContainsExpandedXZ(NavFlag flag, Vector3 point, float margin) =>
        Mathf.Abs(point.X - flag.Center.X) <= (flag.Size.X * 0.5f) + margin
        && Mathf.Abs(point.Z - flag.Center.Z) <= (flag.Size.Z * 0.5f) + margin;

    // Progressive collision reconciliation disables faces monotonically. Keeping the original CSR edges
    // is safe: endpoint lookup and A* both reject disabled faces, while edges between surviving faces are
    // exactly the same as in a freshly rebuilt graph. This makes a usable graph available during the long
    // physics probe instead of forcing zombies back to straight-line pursuit until every flag completes.
    public int Disable(NavFlag source, IReadOnlySet<int> triangles)
    {
        foreach (FlagGraph flag in _flags)
            if (ReferenceEquals(flag.Source, source))
                return flag.Disable(triangles);
        return 0;
    }

    // Test seam: which triangle of a flag an endpoint snaps to. The spatial index only earns its place
    // if it never changes that answer, and the answer is invisible from the outside otherwise.
    internal int ClosestTriangleOf(int flag, Vector3 point) => _flags[flag].ClosestTriangle(point, out _);

    // Test seam: the shortcut's line walk. From outside it is only ever visible as waypoints that did
    // or did not disappear, which says nothing about WHY one survived.
    internal bool HasClearLine(int flag, Vector3 from, Vector3 to,
        float radius = AgentRadius, int budget = int.MaxValue)
    {
        FlagGraph graph = _flags[flag];
        return graph.HasClearLine(graph.ClosestTriangle(from, out _), from, to, radius, ref budget);
    }

    public (int Connections, long Bytes) AdjacencyStorage
    {
        get
        {
            int connections = 0;
            long bytes = 0;
            foreach (FlagGraph flag in _flags)
            {
                connections += flag.ConnectionCount;
                bytes += flag.AdjacencyBytes;
            }
            return (connections, bytes);
        }
    }

    public int SearchWorkspaceCount
    {
        get
        {
            int count = 0;
            foreach (FlagGraph flag in _flags) count += flag.WorkspaceCount;
            return count;
        }
    }

    public long BuildScratchBytes
    {
        get
        {
            long bytes = 0;
            foreach (FlagGraph flag in _flags) bytes += flag.ScratchBytes;
            return bytes;
        }
    }

    public void Write(Stream stream, string fingerprint)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(CacheMagic); writer.Write(CacheVersion); writer.Write(fingerprint);
        writer.Write(_flags.Count);
        foreach (FlagGraph flag in _flags) flag.Write(writer);
    }

    public static bool TryRead(Stream stream, string fingerprint, IReadOnlyList<NavFlag> sources,
        out BakedNavGraph? graph)
    {
        graph = null;
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != CacheMagic || reader.ReadInt32() != CacheVersion
                || reader.ReadString() != fingerprint || reader.ReadInt32() != sources.Count)
                return false;
            var flags = new List<FlagGraph>(sources.Count);
            foreach (NavFlag source in sources) flags.Add(FlagGraph.Read(reader, source));
            if (stream.Position != stream.Length) return false;
            graph = new BakedNavGraph(flags);
            return true;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException
            or ArgumentException or OverflowException)
        {
            graph = null;
            return false;
        }
    }

    private readonly record struct Connection(int To, int VertexA, int VertexB);

    // The two ends of a portal, and — for an end that was pulled in off a wall — the mesh vertex it was
    // pulled off. The funnel emits portal ends as corners, so that vertex is what a corner is turning
    // AROUND, and rounding needs to know it.
    private readonly record struct Portal(Vector3 Left, Vector3 Right, int LeftVertex, int RightVertex);
    private readonly record struct EdgeRecord(int KeyA, int KeyB, int Triangle, int A, int B, int Sequence);

    // How far off the line a vertex may sit and still be treated as ON the join. Recast writes its
    // vertices on a quantised grid, so a genuine split lands exactly; this is float slack, not a
    // tolerance for "nearly collinear".
    private const float JoinPlanarSlack = 1e-3f;
    // And how far apart in height two faces may be and still be the same floor. This is NOT
    // quantisation slack: the spanning side of a join is one straight edge in 3D while the split side
    // bends at its middle vertex, so over uneven ground the two legitimately differ by the chord's sag
    // — measured on PEI as enough to lose 3 of 60 routes' worth of joins at 0.25 m. It only has to
    // separate STOREYS, which are metres apart, so it matches the tolerance used for that elsewhere.
    private const float JoinHeightSlack = 1f;
    // Shorter overlaps than this are a corner touching a line, not a doorway. Well under the body.
    private const float JoinShortestOverlap = 0.05f;
    private readonly record struct DirectedConnection(int From, Connection Edge, long Order);

    private sealed class FlagGraph
    {
        public NavFlag Source { get; }
        private readonly Vector3[] _centres;
        private readonly int[] _edgeStart;
        private readonly Connection[] _edges;
        private readonly bool[] _enabled;
        private readonly bool[] _borderVertex;
        private readonly ConcurrentBag<SearchWorkspace> _workspaces = new();
        private int _workspaceCount;
        public int WorkspaceCount => Volatile.Read(ref _workspaceCount);

        // Faces the clearance test may hold in reach of ONE segment. It is a ceiling on pathological
        // input rather than a working budget: the reach is confined to the band of mesh within one
        // body width of the segment, so it is a small multiple of the faces the walk itself crosses
        // — about six per metre on the 1 m tessellation this data has, against the 2048 faces a whole
        // route's walking is allowed. Filling it takes a mesh finer than the body is wide, and there
        // refusing the shortcut is the safe way to be wrong: the route keeps the funnel's own
        // waypoints, exactly as an exhausted walk budget leaves it.
        private const int ClearanceLimit = 2048;

        private readonly ConcurrentBag<ClearanceReach> _clearance = new();

        // The faces already cleared for the segment being walked, so the fringe around it is tested
        // once per segment instead of once per step that passes it. Generation-stamped for the same
        // reason SearchWorkspace is: a route walks hundreds of segments, and clearing an array as
        // long as the face count for each of them would cost more than the walks do.
        private sealed class ClearanceReach
        {
            private readonly int[] _generationByTriangle;
            private readonly List<int> _pending = new();
            private int _generation;
            private int _head;

            // The GROUND under the segment, as the walk measures it: the height at each point where
            // the walk crossed into a new face, plus both ends, keyed by how far along the segment
            // that was. The spread is bounded by a distance measured in XZ alone, and XZ has no idea
            // which STOREY a face is; this is what tells it, and it is the walk's own numbers rather
            // than the straight line's, because a body ground-snaps and the line over a rise does not.
            //
            // It is filled by a measuring pass over the WHOLE segment before any clearance test reads
            // it (see TryLine). A profile that only reaches as far as the walk has got answers about
            // the far end with the height at the near end, which on a climb is metres too low — and
            // too low means a wall looks like another storey and is skipped.
            private readonly List<float> _along = new();
            private readonly List<float> _height = new();
            private bool _level;

            public ClearanceReach(int count) => _generationByTriangle = new int[count];

            // Past the ceiling. Tested once per face taken OFF the queue rather than on the way in,
            // so it is one decision rather than one at every place a face can be reached from; the
            // overshoot that allows is at most one face's worth of neighbours.
            public bool Exhausted => _pending.Count > ClearanceLimit;

            // Where the walk stood, in the order it stood there. `along` only ever increases.
            public void Mark(float along, float height)
            {
                if (_along.Count == 0)
                    _level = true;
                else if (MathF.Abs(height - _height[0]) > 1e-4f)
                    _level = false;
                _along.Add(along);
                _height.Add(height);
            }

            // The ground the body walks at `along`, held flat outside the samples' span. Flat ground —
            // which is nearly all ground, and every fixture with one storey — answers from the first
            // sample without touching the list.
            public float HeightAt(float along)
            {
                if (_along.Count == 0)
                    return 0f;
                if (_level)
                    return _height[0];
                if (along <= _along[0])
                    return _height[0];
                int last = _along.Count - 1;
                if (along >= _along[last])
                    return _height[last];
                int low = 0, high = last;
                while (low + 1 < high)
                {
                    int middle = low + ((high - low) / 2);
                    if (_along[middle] <= along)
                        low = middle;
                    else
                        high = middle;
                }
                float span = _along[high] - _along[low];
                float t = span <= 1e-9f ? 0f : (along - _along[low]) / span;
                return _height[low] + ((_height[high] - _height[low]) * t);
            }

            public void Begin()
            {
                _pending.Clear();
                _along.Clear();
                _height.Clear();
                _level = true;
                _head = 0;
                if (++_generation == int.MaxValue)
                {
                    Array.Clear(_generationByTriangle);
                    _generation = 1;
                }
            }

            public void Take(int triangle)
            {
                if (_generationByTriangle[triangle] == _generation)
                    return; // already in reach, and already tested or queued to be
                _generationByTriangle[triangle] = _generation;
                _pending.Add(triangle);
            }

            public bool TryTakeNext(out int triangle)
            {
                if (_head >= _pending.Count)
                {
                    triangle = -1;
                    return false;
                }
                triangle = _pending[_head++];
                return true;
            }
        }

        private sealed class SearchWorkspace
        {
            private readonly int[] _generationByTriangle;
            private int _generation;
            public readonly float[] Cost;
            public readonly int[] CameFrom;
            public readonly PriorityQueue<int, float> Frontier = new();
            public readonly List<int> Reverse = new();
            public readonly List<Portal> Portals = new();
            public readonly List<Vector3> Shortcut = new();
            public readonly List<int> Walls = new();

            public SearchWorkspace(int count)
            {
                _generationByTriangle = new int[count];
                Cost = new float[count];
                CameFrom = new int[count];
            }

            public void Begin()
            {
                Frontier.Clear();
                Reverse.Clear();
                Portals.Clear();
                Shortcut.Clear();
                Walls.Clear();
                if (++_generation == int.MaxValue)
                {
                    Array.Clear(_generationByTriangle);
                    _generation = 1;
                }
            }

            public float GetCost(int triangle) => _generationByTriangle[triangle] == _generation
                ? Cost[triangle] : float.PositiveInfinity;

            public int GetCameFrom(int triangle) => _generationByTriangle[triangle] == _generation
                ? CameFrom[triangle] : -1;

            public void Set(int triangle, float cost, int cameFrom)
            {
                _generationByTriangle[triangle] = _generation;
                Cost[triangle] = cost;
                CameFrom[triangle] = cameFrom;
            }
        }

        // Uniform XZ bucketing of the triangles, so snapping an endpoint onto the mesh looks at the
        // handful of triangles around it instead of all of them. California 2 has 266k triangles across
        // its flags and every repath snaps two endpoints; the scan alone was half a million containment
        // tests per query, several times a second per zombie.
        private readonly int _columns;
        private readonly int _rows;
        private readonly float _cellSize;
        private readonly float _minX;
        private readonly float _minZ;
        private readonly int[] _cellStart = Array.Empty<int>();
        private readonly int[] _cellItems = Array.Empty<int>();
        // Every edge no other face named — walls and mis-joined edges look alike from the index pairs —
        // paired up wherever two of them describe the same stretch of ground from opposite sides.
        private static List<DirectedConnection> StitchTJunctions(NavFlag source, List<EdgeRecord> borders,
            out long scratch)
        {
            var stitched = new List<DirectedConnection>();
            scratch = 0;
            if (borders.Count < 2)
                return stitched;

            // One bucket per 2 m cell, entered for every cell the edge's XZ span touches, so two edges
            // that overlap always meet in at least one bucket.
            const float Cell = 2f;
            var grid = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < borders.Count; i++)
            {
                Vector3 p = source.Vertices[borders[i].A], q = source.Vertices[borders[i].B];
                // The cells the edge CROSSES, not its bounding rectangle. A long diagonal spans a
                // rectangle with area quadratic in its length while crossing only a linear number of
                // cells, so filling the rectangle would let one 2 km edge on a sparse custom mesh
                // allocate about a million buckets during build.
                // Column by column: for each cell column the edge spans, only the rows it actually
                // reaches inside that column. A stepped traversal that visits the exact cells crossed
                // is not usable here — recast writes seams on round coordinates, so two collinear
                // edges routinely lie ON a cell boundary and land on opposite sides of it, and the
                // pair is then never compared. Closed ranges put a boundary edge in both.
                // Widened by the slack TryJoin itself allows, or stitching would depend on where the
                // world grid happens to fall: two seams at x = 1.9996 and x = 2.0004 are within
                // tolerance of each other and would join, but land either side of a 2 m column boundary
                // and are never compared — while the same geometry translated a metre would work.
                float minX = MathF.Min(p.X, q.X) - JoinPlanarSlack;
                float maxX = MathF.Max(p.X, q.X) + JoinPlanarSlack;
                int firstColumn = (int)MathF.Floor(minX / Cell), lastColumn = (int)MathF.Floor(maxX / Cell);
                float dx = q.X - p.X, dz = q.Z - p.Z;
                for (int column = firstColumn; column <= lastColumn; column++)
                {
                    float spanFrom = MathF.Max(column * Cell, minX);
                    float spanTo = MathF.Min((column + 1) * Cell, maxX);
                    float zFrom, zTo;
                    if (MathF.Abs(dx) <= 1e-6f)
                    {
                        zFrom = MathF.Min(p.Z, q.Z);
                        zTo = MathF.Max(p.Z, q.Z);
                    }
                    else
                    {
                        // Clamped to the segment's OWN span, because the column bounds carry the slack
                        // and therefore reach past both ends. Dividing that overshoot by a tiny dx
                        // extrapolates: a nearly vertical 2 km edge would report a million metres of Z
                        // for one column and index half a million rows, which is the quadratic blowup
                        // this sweep exists to avoid, reintroduced by the slack that fixed the last one.
                        float from = Math.Clamp((spanFrom - p.X) / dx, 0f, 1f);
                        float to = Math.Clamp((spanTo - p.X) / dx, 0f, 1f);
                        zFrom = p.Z + (dz * MathF.Min(from, to));
                        zTo = p.Z + (dz * MathF.Max(from, to));
                    }
                    int firstRow = (int)MathF.Floor((zFrom - JoinPlanarSlack) / Cell);
                    int lastRow = (int)MathF.Floor((zTo + JoinPlanarSlack) / Cell);
                    for (int row = firstRow; row <= lastRow; row++)
                    {
                        if (!grid.TryGetValue((column, row), out List<int>? bucket))
                            grid[(column, row)] = bucket = new List<int>();
                        bucket.Add(i);
                    }
                }
            }

            // Counted rather than estimated where it is cheap to: the grid dominates, and it is the
            // part that a bad broad phase blows up.
            long buckets = 0;
            foreach (List<int> bucket in grid.Values)
                buckets += bucket.Count;
            scratch = ((long)borders.Count * 24) + (grid.Count * 48L) + (buckets * 4L);

            var seen = new HashSet<(int, int)>();
            foreach (List<int> bucket in grid.Values)
            {
                for (int m = 0; m < bucket.Count; m++)
                    for (int n = m + 1; n < bucket.Count; n++)
                    {
                        int i = bucket[m], j = bucket[n];
                        if (i > j) (i, j) = (j, i);
                        if (!seen.Add((i, j)))
                            continue; // an edge pair straddling several cells meets more than once
                        if (!TryJoin(source, borders[i], borders[j], out int portalA, out int portalB))
                            continue;
                        long order = ((long)borders[j].Sequence << 32) | (uint)borders[i].Sequence;
                        // Oriented per DIRECTION, the way index matching already does it: it stores
                        // (newer.A, newer.B) leaving one face and (prior.A, prior.B) leaving the other,
                        // which is each triangle's own winding around the edge. That is what gives the
                        // funnel a consistent left and right. Storing one order for both directions
                        // hands the funnel a mirrored portal one way round — measured as a 3.10 m route
                        // across a seam that is 3.00 m the other way.
                        (int fromI, int toI) = Orient(source, borders[i], portalA, portalB);
                        (int fromJ, int toJ) = Orient(source, borders[j], portalA, portalB);
                        stitched.Add(new DirectedConnection(borders[i].Triangle,
                            new Connection(borders[j].Triangle, fromI, toI), order));
                        stitched.Add(new DirectedConnection(borders[j].Triangle,
                            new Connection(borders[i].Triangle, fromJ, toJ), order));
                    }
            }
            scratch += (long)seen.Count * 12L;
            return stitched;
        }

        // The portal pair ordered to run the same way as this face's OWN border edge, which is that
        // face's winding around the join.
        private static (int From, int To) Orient(NavFlag source, in EdgeRecord edge, int p1, int p2)
        {
            Vector3 a = source.Vertices[edge.A], b = source.Vertices[edge.B];
            Vector3 u = source.Vertices[p1], v = source.Vertices[p2];
            float along = ((v.X - u.X) * (b.X - a.X)) + ((v.Z - u.Z) * (b.Z - a.Z));
            return along >= 0f ? (p1, p2) : (p2, p1);
        }

        // Do two border edges describe the same stretch of ground from opposite sides? Then the portal
        // between their faces is the part they share.
        private static bool TryJoin(NavFlag source, in EdgeRecord left, in EdgeRecord right,
            out int portalA, out int portalB)
        {
            portalA = portalB = -1;
            if (left.Triangle == right.Triangle)
                return false;

            // The SAME boundary, subdivided — which is what a T-junction IS — and not two different
            // boundaries that happen to line up in plan at different heights. Sharing an endpoint is
            // what says so, and no height tolerance can: the spanning side of a real join is a straight
            // edge in 3D while the split side follows the ground, so the two differ by the chord's sag,
            // and a sag over rough ground is the same number as a low ledge. A ledge shares no vertex.
            //
            // This is why the height test is not sized against the body's step height. It does not need
            // to be: a join whose ends coincide is one surface, and the ground between them is whatever
            // the split side already describes.
            if (left.A != right.A && left.A != right.B && left.B != right.A && left.B != right.B)
                return false;

            Vector3 p = source.Vertices[left.A], q = source.Vertices[left.B];
            float ex = q.X - p.X, ez = q.Z - p.Z;
            float lengthSquared = (ex * ex) + (ez * ez);
            if (lengthSquared <= 1e-8f)
                return false;
            float length = MathF.Sqrt(lengthSquared);

            // Both ends of the other edge must sit ON this one's line. A join is a shared surface, so
            // anything off the line is a different edge that merely passes nearby.
            Vector3 r = source.Vertices[right.A], s = source.Vertices[right.B];
            if (MathF.Abs(((r.X - p.X) * ez) - ((r.Z - p.Z) * ex)) / length > JoinPlanarSlack)
                return false;
            if (MathF.Abs(((s.X - p.X) * ez) - ((s.Z - p.Z) * ex)) / length > JoinPlanarSlack)
                return false;

            float tr = (((r.X - p.X) * ex) + ((r.Z - p.Z) * ez)) / lengthSquared;
            float ts = (((s.X - p.X) * ex) + ((s.Z - p.Z) * ez)) / lengthSquared;
            float lo = MathF.Max(0f, MathF.Min(tr, ts));
            float hi = MathF.Min(1f, MathF.Max(tr, ts));
            if ((hi - lo) * length < JoinShortestOverlap)
                return false; // a corner touching the line, not a way through

            // The same floor, not one passing over another — checked at BOTH ends of the overlap. Two
            // edges with opposite slopes cross somewhere, and a midpoint test alone passes at exactly
            // the place they meet while their ends are storeys apart, joining faces that share a point
            // rather than a surface.
            if (MathF.Abs(ts - tr) <= 1e-6f)
                return false; // the other edge is a point in this direction: no surface to share
            foreach (float t in stackalloc[] { lo, hi })
            {
                float hereY = p.Y + ((q.Y - p.Y) * t);
                float thereY = r.Y + ((s.Y - r.Y) * ((t - tr) / (ts - tr)));
                if (MathF.Abs(hereY - thereY) > JoinHeightSlack)
                    return false;
            }

            // And the two faces must lie on OPPOSITE sides of the join. Two on the same side are
            // stacked or duplicated, and linking them would let a route step off the surface sideways.
            float here = SideOfEdge(source, left, p, ex, ez);
            float there = SideOfEdge(source, right, p, ex, ez);
            if (MathF.Abs(here) <= 1e-6f || MathF.Abs(there) <= 1e-6f)
                return false;
            if (here > 0f == there > 0f)
                return false;

            // The overlap's ends are ends of one edge or the other, so the portal is still a pair of
            // vertices that already exist.
            // Matched in METRES, not in a fraction of the edge. A fixed tolerance on the parameter is a
            // distance that grows with the edge: at 1e-4 of a 2 km edge it is 0.2 m, so an overlap
            // starting 0.1 m inside the spanning edge would match its endpoint instead and the portal
            // would be handed ground the other face does not own.
            float slack = JoinPlanarSlack / length;
            portalA = VertexAtParameter(lo, left.A, left.B, right.A, right.B, tr, ts, slack);
            portalB = VertexAtParameter(hi, left.A, left.B, right.A, right.B, tr, ts, slack);
            return portalA >= 0 && portalB >= 0 && portalA != portalB;
        }

        private static int VertexAtParameter(float t, int leftA, int leftB, int rightA, int rightB,
            float tr, float ts, float slack)
        {
            if (MathF.Abs(t) <= slack) return leftA;
            if (MathF.Abs(t - 1f) <= slack) return leftB;
            if (MathF.Abs(t - tr) <= slack) return rightA;
            if (MathF.Abs(t - ts) <= slack) return rightB;
            return -1;
        }

        // Which side of the join the face's own third vertex falls on.
        private static float SideOfEdge(NavFlag source, in EdgeRecord edge, Vector3 p, float ex, float ez)
        {
            int at = edge.Triangle * 3;
            for (int i = 0; i < 3; i++)
            {
                int v = source.Triangles[at + i];
                if (v == edge.A || v == edge.B)
                    continue;
                Vector3 x = source.Vertices[v];
                return ((x.X - p.X) * ez) - ((x.Z - p.Z) * ex);
            }
            return 0f;
        }

        public int ConnectionCount => _edges.Length;
        public long AdjacencyBytes => ((long)_edges.Length * 12) + ((long)_edgeStart.Length * sizeof(int));
        public long ScratchBytes { get; }

        public FlagGraph(NavFlag source, IReadOnlySet<int>? skip)
        {
            Source = source;
            int count = source.Triangles.Length / 3;
            _centres = new Vector3[count];
            _enabled = new bool[count];
            for (int triangle = 0; triangle < count; triangle++)
            {
                _enabled[triangle] = skip?.Contains(triangle) != true;
                Vector3 a = source.Vertices[source.Triangles[triangle * 3]];
                Vector3 b = source.Vertices[source.Triangles[(triangle * 3) + 1]];
                Vector3 c = source.Vertices[source.Triangles[(triangle * 3) + 2]];
                _centres[triangle] = (a + b + c) / 3f;
            }

            var records = new EdgeRecord[count * 3];
            int recordCount = 0;
            for (int triangle = 0; triangle < count; triangle++)
            {
                if (!_enabled[triangle])
                    continue;
                for (int edge = 0; edge < 3; edge++)
                {
                    int a = source.Triangles[(triangle * 3) + edge];
                    int b = source.Triangles[(triangle * 3) + ((edge + 1) % 3)];
                    records[recordCount++] = new EdgeRecord(Math.Min(a, b), Math.Max(a, b),
                        triangle, a, b, (triangle * 3) + edge);
                }
            }
            Array.Resize(ref records, recordCount);
            Array.Sort(records, static (x, y) =>
            {
                int byA = x.KeyA.CompareTo(y.KeyA);
                if (byA != 0) return byA;
                int byB = x.KeyB.CompareTo(y.KeyB);
                return byB != 0 ? byB : x.Sequence.CompareTo(y.Sequence);
            });

            // One walk of the runs answers both questions: how many directed connections index matching
            // will produce, and which edges no other face named. A run of one is a border — a wall, or
            // a join whose two sides disagree where it ends, which the stitching below tells apart.
            long directedCountLong = 0;
            var borders = new List<EdgeRecord>();
            for (int first = 0; first < records.Length;)
            {
                int last = first + 1;
                while (last < records.Length && records[last].KeyA == records[first].KeyA
                    && records[last].KeyB == records[first].KeyB) last++;
                long sharing = last - first;
                directedCountLong += sharing * (sharing - 1);
                if (sharing == 1)
                    borders.Add(records[first]);
                first = last;
            }
            var directed = new DirectedConnection[checked((int)directedCountLong)];
            int directedAt = 0;
            for (int first = 0; first < records.Length;)
            {
                int last = first + 1;
                while (last < records.Length && records[last].KeyA == records[first].KeyA
                    && records[last].KeyB == records[first].KeyB) last++;
                for (int newerAt = first + 1; newerAt < last; newerAt++)
                {
                    EdgeRecord newer = records[newerAt];
                    for (int priorAt = first; priorAt < newerAt; priorAt++)
                    {
                        EdgeRecord prior = records[priorAt];
                        long order = ((long)newer.Sequence << 32) | (uint)prior.Sequence;
                        directed[directedAt++] = new DirectedConnection(newer.Triangle,
                            new Connection(prior.Triangle, newer.A, newer.B), order);
                        directed[directedAt++] = new DirectedConnection(prior.Triangle,
                            new Connection(newer.Triangle, prior.A, prior.B), order);
                    }
                }
                first = last;
            }
            // Faces that MEET but do not agree where the join's endpoints are. Recast emits this
            // wherever two tiles are subdivided differently: one side spans the join with a single
            // edge while the other has a vertex partway along it, so no pair of vertex indices matches
            // and BOTH sides are classified as walls. Nothing above can see it, because the pair is
            // all the loop compares.
            //
            // Measured on PEI: 1784 of 37396 border edges are this shape — 4.8%, present in every one
            // of the 19 flags — and each is open ground the graph refuses to cross. Reported from play
            // as a gap between two houses that zombies walk round; routing one metre across it cost a
            // 163 m detour, and a body one centimetre wide took the same detour, which is what says
            // adjacency rather than clearance.
            //
            // The join is the OVERLAP of the two edges, and its endpoints are always endpoints of one
            // edge or the other, so the portal is still a pair of existing vertices and Connection does
            // not change shape.
            List<DirectedConnection> stitched = StitchTJunctions(source, borders, out long stitchScratch);
            if (stitched.Count > 0)
            {
                int at = directed.Length;
                Array.Resize(ref directed, at + stitched.Count);
                foreach (DirectedConnection connection in stitched)
                    directed[at++] = connection;
            }

            Array.Sort(directed, static (x, y) =>
            {
                int byTriangle = x.From.CompareTo(y.From);
                return byTriangle != 0 ? byTriangle : x.Order.CompareTo(y.Order);
            });

            _edgeStart = new int[count + 1];
            foreach (DirectedConnection connection in directed)
                _edgeStart[connection.From + 1]++;
            for (int triangle = 0; triangle < count; triangle++)
                _edgeStart[triangle + 1] += _edgeStart[triangle];
            _edges = new Connection[directed.Length];
            for (int i = 0; i < directed.Length; i++)
                _edges[i] = directed[i].Edge;
            _borderVertex = new bool[source.Vertices.Length];
            RecomputeBorderVertices();
            // The stitching broad phase allocates a grid, its buckets, the border list and the pair
            // set, and on a border-heavy map that is a real share of build memory. Leaving it out made
            // the figure the perf harness prints understate this pass to the point of being misleading
            // about capacity on large custom maps.
            ScratchBytes = ((long)records.Length * 24) + ((long)directed.Length * 24) + stitchScratch;

            // One bucket per cell, in CSR form: counts, then offsets, then the triangle ids. A triangle
            // goes in every cell its XZ bounding box touches, so the cell holding a point holds every
            // triangle that could contain it.
            if (count == 0)
                return;

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (Vector3 vertex in source.Vertices)
            {
                minX = MathF.Min(minX, vertex.X);
                maxX = MathF.Max(maxX, vertex.X);
                minZ = MathF.Min(minZ, vertex.Z);
                maxZ = MathF.Max(maxZ, vertex.Z);
            }

            // Aim at a few triangles per cell without letting a huge flag explode the grid.
            int side = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(count / 4f)), 1, 256);
            _minX = minX;
            _minZ = minZ;
            _columns = side;
            _rows = side;
            _cellSize = MathF.Max(MathF.Max(maxX - minX, maxZ - minZ) / side, 0.001f);

            var counts = new int[(side * side) + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int triangle = 0; triangle < count; triangle++)
                {
                    if (!_enabled[triangle])
                        continue;

                    Bounds(source, triangle, out int x0, out int z0, out int x1, out int z1);
                    for (int z = z0; z <= z1; z++)
                        for (int x = x0; x <= x1; x++)
                        {
                            int cell = (z * side) + x;
                            if (pass == 0)
                                counts[cell + 1]++;
                            else
                                _cellItems[_cellStart[cell] + counts[cell]++] = triangle;
                        }
                }

                if (pass != 0)
                    continue;

                _cellStart = new int[(side * side) + 1];
                for (int cell = 0; cell < side * side; cell++)
                    _cellStart[cell + 1] = _cellStart[cell] + counts[cell + 1];
                _cellItems = new int[_cellStart[side * side]];
                Array.Clear(counts);
            }
        }

        private FlagGraph(NavFlag source, Vector3[] centres, int[] edgeStart, Connection[] edges,
            bool[] enabled, int columns, int rows, float cellSize, float minX, float minZ,
            int[] cellStart, int[] cellItems)
        {
            Source = source; _centres = centres; _edgeStart = edgeStart; _edges = edges; _enabled = enabled;
            _columns = columns; _rows = rows; _cellSize = cellSize; _minX = minX; _minZ = minZ;
            _cellStart = cellStart; _cellItems = cellItems; ScratchBytes = 0;
            _borderVertex = new bool[source.Vertices.Length];
            RecomputeBorderVertices();
        }

        public void Write(BinaryWriter writer)
        {
            writer.Write(_centres.Length);
            foreach (Vector3 centre in _centres) { writer.Write(centre.X); writer.Write(centre.Y); writer.Write(centre.Z); }
            writer.Write(_enabled.Length);
            foreach (bool enabled in _enabled) writer.Write(enabled);
            WriteInts(writer, _edgeStart);
            writer.Write(_edges.Length);
            foreach (Connection edge in _edges) { writer.Write(edge.To); writer.Write(edge.VertexA); writer.Write(edge.VertexB); }
            writer.Write(_columns); writer.Write(_rows); writer.Write(_cellSize); writer.Write(_minX); writer.Write(_minZ);
            WriteInts(writer, _cellStart); WriteInts(writer, _cellItems);
        }

        public static FlagGraph Read(BinaryReader reader, NavFlag source)
        {
            int triangles = source.Triangles.Length / 3;
            int centreCount = reader.ReadInt32();
            if (centreCount != triangles) throw new InvalidDataException("CSR triangle count changed");
            var centres = new Vector3[centreCount];
            for (int i = 0; i < centres.Length; i++) centres[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int enabledCount = reader.ReadInt32();
            if (enabledCount != triangles) throw new InvalidDataException("CSR enabled count changed");
            var enabled = new bool[enabledCount];
            for (int i = 0; i < enabled.Length; i++) enabled[i] = reader.ReadBoolean();
            int[] edgeStart = ReadInts(reader, triangles + 1, triangles + 1);
            int edgeCount = reader.ReadInt32();
            if (edgeCount < 0 || edgeCount > triangles * 12) throw new InvalidDataException("CSR edge count invalid");
            var edges = new Connection[edgeCount];
            for (int i = 0; i < edges.Length; i++)
            {
                int to = reader.ReadInt32(), a = reader.ReadInt32(), b = reader.ReadInt32();
                if ((uint)to >= (uint)triangles || (uint)a >= (uint)source.Vertices.Length
                    || (uint)b >= (uint)source.Vertices.Length) throw new InvalidDataException("CSR edge invalid");
                edges[i] = new Connection(to, a, b);
            }
            if (edgeStart[0] != 0)
                throw new InvalidDataException("CSR offsets invalid");
            for (int i = 1; i < edgeStart.Length; i++)
                if ((uint)edgeStart[i] > (uint)edges.Length || edgeStart[i] < edgeStart[i - 1])
                    throw new InvalidDataException("CSR offsets invalid");
            if (edgeStart[^1] != edges.Length)
                throw new InvalidDataException("CSR offsets invalid");
            int columns = reader.ReadInt32(), rows = reader.ReadInt32();
            float cellSize = reader.ReadSingle(), minX = reader.ReadSingle(), minZ = reader.ReadSingle();
            if (columns < 0 || columns > 256 || rows < 0 || rows > 256 || !float.IsFinite(cellSize)
                || cellSize < 0f) throw new InvalidDataException("CSR grid invalid");
            int cells = checked(columns * rows);
            int expectedCellStarts = cells == 0 ? 0 : cells + 1;
            int[] cellStart = ReadInts(reader, expectedCellStarts, expectedCellStarts);
            int[] cellItems = ReadInts(reader, -1, Math.Max(triangles * 16, 1));
            // The same validation edgeStart gets, and for the same reason: ClosestTriangle walks
            // _cellItems from _cellStart[cell] to _cellStart[cell + 1] with no bounds test of its own,
            // so an offset outside the item array indexes past it. Only the terminal offset was checked
            // here, which a corrupt or truncated cache satisfies while every offset before it is
            // arbitrary — and the throw would then land in the middle of a repath, on the server tick,
            // outside the try/catch that exists in TryRead precisely to keep bad cache files harmless.
            if (cellStart.Length > 0)
            {
                if (cellStart[0] != 0)
                    throw new InvalidDataException("CSR grid offsets invalid");
                for (int i = 1; i < cellStart.Length; i++)
                    if ((uint)cellStart[i] > (uint)cellItems.Length || cellStart[i] < cellStart[i - 1])
                        throw new InvalidDataException("CSR grid offsets invalid");
            }
            if ((cellStart.Length == 0 ? 0 : cellStart[^1]) != cellItems.Length)
                throw new InvalidDataException("CSR grid offsets invalid");
            foreach (int item in cellItems) if ((uint)item >= (uint)triangles) throw new InvalidDataException("CSR grid item invalid");
            return new FlagGraph(source, centres, edgeStart, edges, enabled, columns, rows, cellSize,
                minX, minZ, cellStart, cellItems);
        }

        private static void WriteInts(BinaryWriter writer, int[] values)
        {
            writer.Write(values.Length); foreach (int value in values) writer.Write(value);
        }

        private static int[] ReadInts(BinaryReader reader, int exact, int maximum)
        {
            int count = reader.ReadInt32();
            if (count < 0 || (exact >= 0 && count != exact) || count > maximum)
                throw new InvalidDataException("CSR array length invalid");
            var values = new int[count];
            for (int i = 0; i < count; i++) values[i] = reader.ReadInt32();
            return values;
        }

        // The cell range a triangle's XZ bounding box covers, clamped to the grid.
        private void Bounds(NavFlag source, int triangle, out int x0, out int z0, out int x1, out int z1)
        {
            Vector3 a = source.Vertices[source.Triangles[triangle * 3]];
            Vector3 b = source.Vertices[source.Triangles[(triangle * 3) + 1]];
            Vector3 c = source.Vertices[source.Triangles[(triangle * 3) + 2]];
            x0 = Column(MathF.Min(a.X, MathF.Min(b.X, c.X)));
            x1 = Column(MathF.Max(a.X, MathF.Max(b.X, c.X)));
            z0 = Row(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)));
            z1 = Row(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)));
        }

        private int Column(float x) => Math.Clamp((int)((x - _minX) / _cellSize), 0, _columns - 1);

        private int Row(float z) => Math.Clamp((int)((z - _minZ) / _cellSize), 0, _rows - 1);

        // The triangle to start or end a route on: one whose XZ footprint covers the point if there is
        // one (scored by height alone, so a floor above or below does not win), otherwise the nearest
        // centre. Walks the grid outwards from the point's own cell and stops as soon as no unvisited
        // cell can beat what it already has.
        public int ClosestTriangle(Vector3 point, out float score)
        {
            int closest = -1;
            score = float.MaxValue;
            bool foundContaining = false;

            if (_cellItems.Length == 0)
            {
                for (int triangle = 0; triangle < _centres.Length; triangle++)
                    Consider(triangle, point, ref closest, ref score, ref foundContaining);
                return closest;
            }

            int centreColumn = Column(point.X);
            int centreRow = Row(point.Z);
            int reach = Math.Max(Math.Max(centreColumn, _columns - 1 - centreColumn),
                Math.Max(centreRow, _rows - 1 - centreRow));

            for (int ring = 0; ring <= reach; ring++)
            {
                // A triangle's centre lies inside its own bounding box, so a centre `d` away from the
                // point is registered no further out than ring ceil(d / cell): once the nearest edge of
                // this ring is further than the best score, nothing beyond it can win. A triangle that
                // contains the point is always in ring 0, so that case never expands at all.
                if (ring > 0)
                {
                    if (foundContaining)
                        break;

                    float nearest = (ring - 1) * _cellSize;
                    if (nearest > 0f && nearest * nearest > score)
                        break;
                }

                for (int row = centreRow - ring; row <= centreRow + ring; row++)
                {
                    if (row < 0 || row >= _rows)
                        continue;

                    bool edgeRow = row == centreRow - ring || row == centreRow + ring;
                    for (int column = centreColumn - ring; column <= centreColumn + ring; column++)
                    {
                        if (column < 0 || column >= _columns)
                            continue;
                        if (!edgeRow && column != centreColumn - ring && column != centreColumn + ring)
                            continue; // the inside of the ring was covered by a smaller one

                        int cell = (row * _columns) + column;
                        for (int at = _cellStart[cell]; at < _cellStart[cell + 1]; at++)
                            Consider(_cellItems[at], point, ref closest, ref score, ref foundContaining);
                    }
                }
            }

            return closest;
        }

        public Vector3 ClosestPointXZ(int triangle, Vector3 point)
        {
            Vector3 a = Source.Vertices[Source.Triangles[triangle * 3]];
            Vector3 b = Source.Vertices[Source.Triangles[(triangle * 3) + 1]];
            Vector3 c = Source.Vertices[Source.Triangles[(triangle * 3) + 2]];
            Vector3 best = a;
            float score = float.MaxValue;
            ConsiderEdge(a, b);
            ConsiderEdge(b, c);
            ConsiderEdge(c, a);
            return best;

            void ConsiderEdge(Vector3 start, Vector3 end)
            {
                float dx = end.X - start.X, dz = end.Z - start.Z;
                float lengthSquared = (dx * dx) + (dz * dz);
                if (lengthSquared <= 1e-8f)
                    return;
                float t = Math.Clamp((((point.X - start.X) * dx) + ((point.Z - start.Z) * dz))
                    / lengthSquared, 0f, 1f);
                Vector3 candidate = start.Lerp(end, t);
                float x = point.X - candidate.X, z = point.Z - candidate.Z;
                float candidateScore = (x * x) + (z * z);
                if (candidateScore < score)
                {
                    score = candidateScore;
                    best = candidate;
                }
            }
        }

        public int Disable(IReadOnlySet<int> triangles)
        {
            int changed = 0;
            foreach (int triangle in triangles)
            {
                if ((uint)triangle >= (uint)_enabled.Length || !_enabled[triangle])
                    continue;
                _enabled[triangle] = false;
                changed++;

                // Only the edges this face used to close can become walls, so mark those and nothing
                // else. A full rescan here would be O(triangles) on the reconciliation frame, which is
                // budgeted to 0.25 ms — measured at about 41 ms for a 266k-triangle flag, a visible
                // hitch every time a large flag finishes.
                //
                // Faces are only ever disabled, never re-enabled, so this only ever ADDS borders — the
                // safe direction, since an over-marked vertex costs a narrowed portal while an
                // under-marked one aims a body at a wall. It is exact for the edges this face closed.
                // The one thing it cannot walk back is a vertex that was already a border because of a
                // FREE edge of the face being disabled and is now interior; noticing that needs a
                // vertex-to-face index, which this does not carry.
                for (int at = _edgeStart[triangle]; at < _edgeStart[triangle + 1]; at++)
                {
                    Connection c = _edges[at];
                    if (!_enabled[c.To] || StillShared(c.To, c.VertexA, c.VertexB))
                        continue;
                    _borderVertex[c.VertexA] = true;
                    _borderVertex[c.VertexB] = true;
                }
            }
            return changed;
        }

        // Does an ENABLED face still sit across this vertex pair from `triangle`? An edge is not always
        // shared by exactly two faces — the builder connects every pair that shares one — so disabling
        // one of three leaves the other two joined and the edge is still not a wall. Assuming otherwise
        // narrows portals in the middle of open mesh: on a flat field, one disabled flap on a shared
        // diagonal bent a dead-straight run into (2, 16) -> (17, 15.55) -> (30, 16).
        //
        // The caller has already cleared `_enabled` for the face being disabled, so it excludes itself.
        private bool StillShared(int triangle, int vertexA, int vertexB)
        {
            // Across, not merely alongside. A duplicated or overlapping face shares an edge from the
            // SAME side, and counting it turns a real outer boundary into an interior edge: the wall
            // stops being tested for clearance and its vertices stop being borders, so a route can run
            // half a metre from the edge of the navmesh with the capsule hanging off it. Every use of
            // this asks "is there still floor on the other side", which is what this now answers.
            int mine = OppositeVertex(triangle, vertexA, vertexB);
            if (mine < 0)
                return false;
            float here = SideOfEdge(vertexA, vertexB, mine);
            for (int at = _edgeStart[triangle]; at < _edgeStart[triangle + 1]; at++)
            {
                Connection c = _edges[at];
                if (!_enabled[c.To])
                    continue;
                if ((c.VertexA != vertexA || c.VertexB != vertexB)
                    && (c.VertexA != vertexB || c.VertexB != vertexA))
                    continue;
                int theirs = OppositeVertex(c.To, vertexA, vertexB);
                if (theirs >= 0 && here * SideOfEdge(vertexA, vertexB, theirs) < 0f)
                    return true;
            }
            return false;
        }

        // Which vertices sit on the edge of the walkable region — the wall corners. Derived from the CSR
        // alone: an edge of an enabled triangle is a border edge when no ENABLED neighbour is listed as
        // sharing that vertex pair. A triangle has at most three connections, so this is O(3 * 3) per
        // triangle with no dictionary, which is why it can be re-derived after a cache read and after
        // every Disable rather than being serialized and going stale.
        private void RecomputeBorderVertices()
        {
            Array.Clear(_borderVertex);
            int triangles = _centres.Length;
            for (int t = 0; t < triangles; t++)
            {
                if (!_enabled[t])
                    continue;
                for (int e = 0; e < 3; e++)
                {
                    int v0 = Source.Triangles[(t * 3) + e];
                    int v1 = Source.Triangles[(t * 3) + ((e + 1) % 3)];
                    // One definition of "still has floor across it", shared with Disable and with the
                    // line walk's clearance test, so a wall cannot be a wall to one of them and not the
                    // others.
                    if (!StillShared(t, v0, v1))
                    {
                        _borderVertex[v0] = true;
                        _borderVertex[v1] = true;
                    }
                }
            }
        }

        private void Consider(int triangle, Vector3 point, ref int closest, ref float score,
            ref bool foundContaining)
        {
            if (!_enabled[triangle])
                return;

            Vector3 a = Source.Vertices[Source.Triangles[triangle * 3]];
            Vector3 b = Source.Vertices[Source.Triangles[(triangle * 3) + 1]];
            Vector3 c = Source.Vertices[Source.Triangles[(triangle * 3) + 2]];
            bool contains = ContainsXZ(a, b, c, point);
            if (foundContaining && !contains)
                return;

            Vector3 centre = _centres[triangle];
            float dx = centre.X - point.X, dz = centre.Z - point.Z, dy = centre.Y - point.Y;
            float candidate = contains ? dy * dy : (dx * dx) + (dz * dz) + (0.25f * dy * dy);
            if ((contains && !foundContaining) || candidate < score)
            {
                foundContaining = contains;
                closest = triangle;
                score = candidate;
            }
        }

        public bool FindPath(int start, int goal, Vector3 from, Vector3 to, List<Vector3> output,
            float radius)
        {
            if (start < 0 || goal < 0)
                return false;
            int begin = output.Count;
            // Every route starts at the position it was asked from. NavigationServer's paths do, and the
            // movement code reads index 0 as "where I am" and steers towards index 1 — handing it a route
            // that begins at the first portal made it skip that portal and cut the corner through whatever
            // stood between.
            output.Add(from);
            if (start == goal)
            {
                output.Add(to);
                return true;
            }

            if (!_workspaces.TryTake(out SearchWorkspace? workspace))
            {
                workspace = new SearchWorkspace(_centres.Length);
                Interlocked.Increment(ref _workspaceCount);
            }
            workspace.Begin();
            workspace.Set(start, 0f, -1);
            workspace.Frontier.Enqueue(start, Heuristic(start, goal));
            int closestReachable = start;
            float closestDistance = Heuristic(start, goal);

            try
            {
                while (workspace.Frontier.TryDequeue(out int current, out float priority))
                {
                    float currentCost = workspace.GetCost(current);
                    float expected = currentCost + Heuristic(current, goal);
                    if (priority > expected + 0.001f)
                        continue; // stale heap entry
                    if (current == goal)
                        break;
                    for (int edgeAt = _edgeStart[current]; edgeAt < _edgeStart[current + 1]; edgeAt++)
                    {
                        Connection edge = _edges[edgeAt];
                        if (!_enabled[edge.To])
                            continue;
                        float candidate = currentCost + _centres[current].DistanceTo(_centres[edge.To]);
                        if (candidate >= workspace.GetCost(edge.To))
                            continue;
                        workspace.Set(edge.To, candidate, current);
                        workspace.Frontier.Enqueue(edge.To, candidate + Heuristic(edge.To, goal));
                        float distanceToGoal = Heuristic(edge.To, goal);
                        if (distanceToGoal < closestDistance - 1e-6f
                            || (MathF.Abs(distanceToGoal - closestDistance) <= 1e-6f
                                && edge.To < closestReachable))
                        {
                            closestReachable = edge.To;
                            closestDistance = distanceToGoal;
                        }
                    }
                }

                // Match NavigationServer's partial-path behavior. A collision-pruned band or authored
                // island can disconnect the target, but the closest explored face still gives the zombie
                // a collision-aware continuation instead of making a newly aggroed zombie stand still.
                bool complete = workspace.GetCameFrom(goal) >= 0;
                int routeGoal = complete ? goal : closestReachable;
                Vector3 destination = complete ? to : _centres[routeGoal];

                List<int> reverse = workspace.Reverse;
                for (int at = routeGoal; at != start; at = workspace.GetCameFrom(at))
                    reverse.Add(at);
                reverse.Add(start);
                reverse.Reverse();

                List<Portal> portals = workspace.Portals;
                portals.Add(new Portal(from, from, -1, -1));
                for (int i = 0; i + 1 < reverse.Count; i++)
                {
                    int current = reverse[i], next = reverse[i + 1];
                    for (int edgeAt = _edgeStart[current]; edgeAt < _edgeStart[current + 1]; edgeAt++)
                    {
                        Connection edge = _edges[edgeAt];
                        if (edge.To == next)
                        {
                            Vector3 a = Source.Vertices[edge.VertexA];
                            Vector3 b = Source.Vertices[edge.VertexB];
                            Vector3 middle = (a + b) * 0.5f;
                            Vector3 travel = _centres[next] - _centres[current];
                            float side = (travel.X * (a.Z - middle.Z))
                                - (travel.Z * (a.X - middle.X));
                            // Godot's XZ ground plane has +Z opposite the usual 2D screen Y convention
                            // used by the funnel area tests, so the positive-side endpoint is the right.
                            (Vector3 left, Vector3 right) = side >= 0f ? (b, a) : (a, b);
                            (int leftVertex, int rightVertex) = side >= 0f
                                ? (edge.VertexB, edge.VertexA)
                                : (edge.VertexA, edge.VertexB);
                            portals.Add(Inset(current, left, right, leftVertex, rightVertex,
                                _borderVertex[leftVertex], _borderVertex[rightVertex], radius));
                            break;
                        }
                    }
                }
                portals.Add(new Portal(destination, destination, -1, -1));
                AppendFunnel(output, portals, destination);
                Shortcut(output, begin, start, workspace.Shortcut, radius);
                // After the shortcut, not before. Every leg the shortcut CREATED was walked and cleared
                // by the body's full width, so it needs no repair; what is left to repair is only the
                // funnel legs the shortcut could not replace, which is the smaller set. Rounding first
                // and shortcutting after measured the same routes at 31.7 mean waypoints against 27.8.
                RoundCorners(output, begin, portals, workspace.Walls, radius);
                return true;
            }
            finally
            {
                _workspaces.Add(workspace);
            }
        }

        private float Heuristic(int from, int to) => _centres[from].DistanceTo(_centres[to]);

        // Simple Stupid Funnel over the A* triangle corridor. Portal midpoints make an open tessellated
        // floor look like a slalom; the movement forward-look then turns that zigzag into a wide arc.
        // Funnel/string-pulling emits only corners forced by the corridor and is deterministic because
        // portal order and every tie follow the source triangle/index order.
        // Skin on top of the radius. Insetting by exactly the radius aims the body at the precise limit
        // of where it fits, leaving nothing for the turn: these bodies steer at 720 deg/s toward a
        // look-ahead point, so they arrive at an angle and clip the jamb they were aimed to graze. A
        // CharacterController carries a skin width for the same reason.
        private const float Clearance = 0.05f;

        // Pull a portal end in along the portal's own line, but ONLY where that end is a wall.
        //
        // This is the whole difficulty. Insetting every end fixes doorways and ruins open ground: in the
        // middle of a field a portal end is an interior vertex with walkable mesh all around it, nothing
        // to keep clear of, and shrinking those portals stops the funnel collapsing straight runs — a
        // measured 1.174 detour became 1.254 and 12 waypoints became 43. Only border ends are walls, so
        // only border ends move.
        //
        // Each end is clamped to just under half the portal so the two insets can never cross and invert
        // it; a gap narrower than the body degrades to its midpoint, which is the best a body can aim at
        // anyway — whether it physically fits is the collision resolver's call, not the graph's.
        private Portal Inset(int triangle, Vector3 left, Vector3 right,
            int leftVertex, int rightVertex, bool insetLeft, bool insetRight, float radius)
        {
            if (!insetLeft && !insetRight)
                return new Portal(left, right, -1, -1);

            Vector3 along = right - left;
            along.Y = 0f;
            float length = along.Length();
            if (length <= 1e-4f)
                return new Portal(left, right, -1, -1);

            // Each end moves INWARD along the portal, and the two ends move opposite ways: the left
            // towards the right and the right towards the left. Which way an end travels is what decides
            // whether a wall collinear with the portal is in front of it or behind it, so the direction
            // goes in rather than the portal's own orientation.
            float leftInset = insetLeft ? InsetFor(triangle, leftVertex, along, length, 1f, radius) : 0f;
            float rightInset = insetRight ? InsetFor(triangle, rightVertex, along, length, -1f, radius) : 0f;

            // Share the portal when the two ends want more of it than there is, rather than halving
            // both unconditionally. Equal demands still get half each, and a single moving end still
            // gets the whole portal — a 0.6 m portal with one wall end has room for the full 0.45 and
            // was once given 0.299, leaving the capsule on the vertex it was supposed to clear.
            float budget = length - 1e-3f;
            float total = leftInset + rightInset;
            // Whether the portal could pay what its ends asked for. A shared end is NOT on the
            // `radius + Clearance` circle around its vertex, which is what decides below whether the
            // corner rounding may use that vertex at all.
            bool paid = !(total > budget && total > 0f);
            if (!paid)
            {
                if (leftInset > 0f && rightInset > 0f)
                {
                    float half = budget * 0.5f;
                    float leftCapped = MathF.Min(leftInset, half);
                    float rightCapped = MathF.Min(rightInset, half);
                    float spare = budget - leftCapped - rightCapped;
                    if (spare > 0f)
                    {
                        if (leftCapped < leftInset)
                            leftCapped = MathF.Min(leftInset, leftCapped + spare);
                        else if (rightCapped < rightInset)
                            rightCapped = MathF.Min(rightInset, rightCapped + spare);
                    }
                    leftInset = leftCapped;
                    rightInset = rightCapped;
                }
                else
                {
                    float share = budget / total;
                    leftInset *= share;
                    rightInset *= share;
                }
            }
            if (leftInset <= 0f && rightInset <= 0f)
                return new Portal(left, right, -1, -1);

            // The vertices carried out of here are the corner rounding's whole input, and it uses them
            // as centres of `radius + Clearance` circles it pushes legs out onto. That is only sound
            // where the inset actually DELIVERED that distance: the chord being repaired is a chord of
            // the circle the two corners already sit on, and a shared portal puts them somewhere else
            // entirely — so the "circle" is no longer anchored to the corridor, or to the mesh.
            //
            // A body far wider than the corridor shares every portal, names no vertices, and keeps the
            // funnel's raw route, which is what it had before rounding existed. Without this a radius
            // of 1e6 m on a 20 m field pushed waypoints out to (-768211, -640177) — 768 km off the
            // navmesh, the same point re-inserted 47 times until the repair budget ran out — and a
            // radius of float.MaxValue overflowed that to NaN. Radius 5 and 100 did it too, in the
            // field rather than outside it: 50 waypoints, 47 of them identical.
            //
            // Reported rather than re-derived from the geometry, because the two are not the same test.
            // An end can sit at `radius + Clearance` from its OWN vertex and still have been scaled back
            // on the other end's behalf, and a scaled end can land at that distance by coincidence.
            //
            // The same argument rules out an end that was inset by MORE than the circle's radius.
            // InsetFor deliberately overshoots where a wall meets the portal at a shallow angle, since
            // travelling d along the portal only buys d*sin(angle) away from that wall — and an end
            // pushed out to 0.9 to clear a 10 degree wall is no more on the 0.45 circle than a shared
            // one is. Naming it would let Push drag the corner back onto that circle in a direction
            // nearly along the wall, undoing exactly the clearance the overshoot was paying for, and
            // Push could not tell: it validates against wall VERTICES, and the wall EDGE is what is
            // close. Only an end that got the plain radius, and got all of it, is on the circle.
            float wanted = radius + Clearance;
            bool leftOnCircle = MathF.Abs(leftInset - wanted) <= 1e-4f;
            bool rightOnCircle = MathF.Abs(rightInset - wanted) <= 1e-4f;

            Vector3 unit = along / length;
            return new Portal(left + (unit * leftInset), right - (unit * rightInset),
                paid && leftOnCircle ? leftVertex : -1, paid && rightOnCircle ? rightVertex : -1);
        }

        // How far along the portal an end must move for the body to clear the walls that MEET it.
        //
        // Moving d along the portal puts the point d*|sin(angle)| away from a wall that crosses it at
        // that angle, so a portal leaving a wall at a shallow angle has to move much further than the
        // radius. Applying the radius flat, as this used to, is only right at 90 degrees: at 10 degrees
        // it leaves 0.078 m and a 0.40 m body is still inside the wall. Measured on a plain 1 m doorway
        // with an angled approach, the flat inset left the route 0.318 m from a jamb.
        private float InsetFor(int triangle, int vertex, Vector3 along, float length, float sign,
            float radius)
        {
            Span<Vector2> walls = stackalloc Vector2[MaxWallsPerVertex];
            int count = WallsAt(triangle, vertex, walls);
            float wanted = radius + Clearance;
            if (count == 0)
                return wanted; // a border with no wall edge in reach of the fan: the flat inset is all there is

            // The direction this END travels, not the portal's. Every wall direction is oriented away
            // from the shared vertex, so the two compare directly.
            float inwardX = sign * along.X / length, inwardZ = sign * along.Z / length;
            float needed = wanted;
            for (int i = 0; i < count; i++)
            {
                Vector2 wall = walls[i];
                float wallLength = wall.Length();
                if (wallLength <= 1e-4f)
                    continue;
                // The wall leaves the vertex; moving `d` inward puts the point d*cos along it and
                // d*|sin| across it. When cos <= 0 the wall runs BEHIND the direction of travel, so the
                // nearest point of it is the vertex itself and the plain inset already clears it — which
                // is most collinear cases, and demanding the whole portal for them narrows a doorway to
                // a millimetre of its far jamb for no gain.
                float cosine = ((inwardX * wall.X) + (inwardZ * wall.Y)) / wallLength;
                if (cosine <= 0f)
                    continue;
                float sine = MathF.Abs(((inwardX * wall.Y) - (inwardZ * wall.X)) / wallLength);
                // Collinear and ahead: no distance along THIS portal gains any clearance from THAT wall.
                // Ask for the whole portal and let the share above decide; the line walk's own clearance
                // test is what still refuses a shortcut aimed down a wall it cannot get away from.
                if (sine <= 1e-3f)
                    return length;
                needed = MathF.Max(needed, wanted / sine);
            }
            return needed;
        }

        // Faces the wall search may visit around one vertex, and wall directions it may collect. Both
        // are ceilings on pathological input: a fan of a dozen faces around a single vertex, or four
        // walls meeting at one, is already past anything this data contains.
        private const int FanLimit = 12;
        private const int MaxWallsPerVertex = 4;

        // Every wall edge touching `vertex`, as an XZ direction, found by spreading over the enabled
        // faces around it. A flood rather than a rotation through the fan: it needs no winding
        // convention, it copes with the non-manifold fans the builder allows, and it is bounded by
        // construction rather than by trusting the topology to close.
        //
        // It has to reach past the face the portal is on. At a doorway the portal IS the opening, and
        // the jamb's wall runs along the face beside the one the route crosses into — so a look at just
        // the two faces either side of the portal finds no wall at all.
        private int WallsAt(int triangle, int vertex, Span<Vector2> walls)
        {
            Span<int> seen = stackalloc int[FanLimit];
            int seenCount = 0, head = 0, found = 0;
            seen[seenCount++] = triangle;

            while (head < seenCount)
            {
                int at = seen[head++];
                for (int e = 0; e < 3; e++)
                {
                    int va = Source.Triangles[(at * 3) + e];
                    int vb = Source.Triangles[(at * 3) + ((e + 1) % 3)];
                    if (va != vertex && vb != vertex)
                        continue; // not one of the fan's edges around this vertex

                    // One pass over the adjacency answers both questions this needs — whether anything
                    // enabled still shares the edge, and where to spread next. Asking StillShared first
                    // and then scanning again to step walks the same list twice, which measured at
                    // roughly double the cost of the whole search.
                    bool shared = false;
                    int mine = OppositeVertex(at, va, vb);
                    float here = mine >= 0 ? SideOfEdge(va, vb, mine) : 0f;
                    for (int i = _edgeStart[at]; i < _edgeStart[at + 1]; i++)
                    {
                        Connection c = _edges[i];
                        if (!_enabled[c.To])
                            continue;
                        if ((c.VertexA != va || c.VertexB != vb)
                            && (c.VertexA != vb || c.VertexB != va))
                            continue;
                        // Across, not merely alongside — the same rule StillShared applies. A face
                        // duplicated on this side would otherwise hide a real wall from the inset.
                        int theirs = OppositeVertex(c.To, va, vb);
                        if (theirs < 0 || here * SideOfEdge(va, vb, theirs) >= 0f)
                            continue;
                        shared = true;
                        if (seenCount < FanLimit && !Seen(seen, seenCount, c.To))
                            seen[seenCount++] = c.To;
                    }
                    if (!shared && found < walls.Length)
                    {
                        // Oriented AWAY from the shared vertex, so the caller can tell a wall that runs
                        // ahead of an inset from one that runs behind it. Unoriented, the two are the
                        // same direction up to sign and the caller cannot.
                        Vector3 o = Source.Vertices[vertex];
                        Vector3 far = Source.Vertices[va == vertex ? vb : va];
                        walls[found++] = new Vector2(far.X - o.X, far.Z - o.Z);
                    }
                }
            }
            return found;
        }

        private static bool Seen(ReadOnlySpan<int> visited, int count, int triangle)
        {
            for (int i = 0; i < count; i++)
                if (visited[i] == triangle)
                    return true;
            return false;
        }

        // A corner of the route is a portal end pulled `radius + Clearance` off a wall vertex, so it
        // sits ON the circle of that radius around it — and the leg LEAVING that corner is a chord of
        // that circle, which passes inside it. No per-vertex inset can see this. An inset is a
        // one-dimensional move along one portal: it places a POINT correctly, and says nothing about
        // the segment joining it to the next one, which belongs to a different portal.
        //
        // Measured on a plain 1 m doorway approached diagonally: two corners each exactly 0.450 m off
        // the jamb at (10, 8), and the leg joining them 0.318 m from it — 0.45 * cos(45 deg), the chord
        // of a right-angle turn. A second shape does it with one corner: the route leaves the door at
        // (11, 7.45), correctly 0.45 off the jamb at (11, 7), and heads for a destination that pulls
        // the leg back across the jamb at 0.336. The body walks the legs, not the corners.
        //
        // So the legs are repaired: where one passes a wall vertex closer than the body's radius, a
        // waypoint goes in on that vertex's `radius + Clearance` circle, turning the chord into two
        // shallower ones. Each pass at most halves the angle a leg subtends at the vertex, and a step
        // of `angle` leaves the body r * cos(angle / 2), so a handful of passes is enough for any turn
        // — four is past a hairpin.
        //
        // The threshold to repair is the RADIUS, not radius + Clearance: with the corner itself sitting
        // at radius + Clearance, no chord from it can ever reach radius + Clearance, and asking for it
        // would subdivide forever. The 0.05 m steering skin is exactly the budget this spends.
        private const int CornerRepairs = 4;

        // The wall vertices this corridor actually touches: the portal ends that were pulled in off
        // one. Consecutive portals of a corridor share their ends, so a glance back over the tail of the
        // list removes nearly every repeat; a full membership test would be quadratic in the corridor
        // and a repeat that slips through only costs one more distance test.
        //
        // Not ClearanceReach, which the clearance test uses to find walls near a segment. That reach
        // answers a different question and answers it as a BOOLEAN: it spreads from a face the walk is
        // standing on and reports whether anything is too close, never which vertex — and rounding needs
        // the vertex to push off. It would also have to be re-entered per leg, from a face these legs do
        // not carry, since half of them were inserted by this pass. And its band holds every border
        // vertex within a body's width, including ones behind a wall the corridor never turned at, while
        // a chord can only cut a corner the funnel itself turned around. The portal ends are exactly
        // those, and they are already computed.
        private const int WallLookback = 8;

        private static void CollectWalls(List<Portal> portals, List<int> walls)
        {
            foreach (Portal portal in portals)
            {
                AddWall(walls, portal.LeftVertex);
                AddWall(walls, portal.RightVertex);
            }
        }

        private static void AddWall(List<int> walls, int vertex)
        {
            if (vertex < 0)
                return;
            for (int at = walls.Count - 1, back = 0; at >= 0 && back < WallLookback; at--, back++)
                if (walls[at] == vertex)
                    return;
            walls.Add(vertex);
        }

        private void RoundCorners(List<Vector3> output, int begin, List<Portal> portals,
            List<int> walls, float radius)
        {
            CollectWalls(portals, walls);
            if (walls.Count == 0)
                return;

            float wanted = radius + Clearance;
            int budget = CornerRepairs * (output.Count - begin);
            for (int i = begin + 1; i < output.Count && budget > 0; i++)
            {
                Vector3 a = output[i - 1], b = output[i];
                int worst = Nearest(walls, a, b, radius, out float worstSquared);
                if (worst < 0)
                    continue;
                if (!Push(walls, Source.Vertices[worst], a, b, wanted, worstSquared, out Vector3 around))
                    continue;
                output.Insert(i, around);
                budget--;
                i--; // the leg now ends at the new waypoint, and it gets measured in its turn
            }
        }

        // The wall vertex a leg passes closest to, if any passes closer than the body is wide. The box
        // test is what makes the loop affordable: a corridor's wall list is every jamb it squeezes past,
        // and all but a couple of them are nowhere near any one leg.
        //
        // Closest in XZ, and on the leg's own STOREY. The wall list is every portal end of the corridor,
        // and a corridor that climbs holds the ends of the floor it climbed TO; those sit directly over
        // the legs on the floor below, which is nought metres away in plan. Measured on a wide ground
        // floor with a narrower upper floor over it, a route from (1, 0, -1.5) to (4, 3, 1): the legs
        // across the open ground floor were bent around the upper floor's boundary vertices three
        // metres overhead, and since pushing off one of them lands next to the next, the route ORBITED
        // (8, 3, 0) at 0.45 m — 25 waypoints where the corridor needed 4.
        //
        // That is worse than no repair at all. This pass runs after the shortcut, so nothing re-walks
        // what it inserts: a waypoint it invents to dodge a wall the body can never meet is never
        // cleared against the walls the body CAN meet.
        //
        // The leg's height at the closest point is read off the leg itself, which is a chord between
        // two ground-snapped waypoints. It is a storey out only where the ground breaks slope inside
        // one leg by more than StoreyTolerance, and being wrong there drops a repair — the chord-cut
        // this pass exists to fix — rather than inventing a waypoint. The test can only ever REMOVE
        // candidates, so on level ground, where every wall and every waypoint share a height, nothing
        // about this pass changes.
        private int Nearest(List<int> walls, Vector3 a, Vector3 b, float reach, out float squared)
        {
            float minX = MathF.Min(a.X, b.X) - reach, maxX = MathF.Max(a.X, b.X) + reach;
            float minZ = MathF.Min(a.Z, b.Z) - reach, maxZ = MathF.Max(a.Z, b.Z) + reach;
            int nearest = -1;
            squared = reach * reach;
            foreach (int vertex in walls)
            {
                Vector3 v = Source.Vertices[vertex];
                if (v.X < minX || v.X > maxX || v.Z < minZ || v.Z > maxZ)
                    continue;
                float candidate = PointSegmentDistanceSquaredXZ(v.X, v.Z, a.X, a.Z, b.X, b.Z,
                    out float along);
                if (candidate >= squared || !SameStorey(v.Y, a.Y + ((b.Y - a.Y) * along)))
                    continue;
                squared = candidate;
                nearest = vertex;
            }
            return nearest;
        }

        private static bool SameStorey(float wall, float leg) =>
            MathF.Abs(wall - leg) <= StoreyTolerance;

        // The waypoint that walks the leg around `centre` instead of through it: the point where the leg
        // came nearest, pushed out onto the circle the corners themselves sit on.
        //
        // It is only taken when it is an improvement against EVERY wall, not just against this one. A
        // gap narrower than the body has no point that clears both its jambs, and pushing off one of
        // them there just walks the route into the other; the funnel has already collapsed such a gap to
        // its middle, which is the best a body can aim at.
        private bool Push(List<int> walls, Vector3 centre, Vector3 a, Vector3 b, float wanted,
            float worstSquared, out Vector3 around)
        {
            around = default;
            float dx = b.X - a.X, dz = b.Z - a.Z;
            float lengthSquared = (dx * dx) + (dz * dz);
            if (lengthSquared <= 1e-8f)
                return false;
            float t = Math.Clamp((((centre.X - a.X) * dx) + ((centre.Z - a.Z) * dz)) / lengthSquared,
                0f, 1f);
            float outX = (a.X + (dx * t)) - centre.X, outZ = (a.Z + (dz * t)) - centre.Z;
            float outLength = MathF.Sqrt((outX * outX) + (outZ * outZ));
            // A leg running exactly over the vertex has no side to be pushed to; take the leg's own left
            // normal, which is a direction rather than a preference and keeps the result deterministic.
            if (outLength <= 1e-4f)
            {
                float legLength = MathF.Sqrt(lengthSquared);
                outX = -dz / legLength;
                outZ = dx / legLength;
            }
            else
            {
                outX /= outLength;
                outZ /= outLength;
            }

            around = new Vector3(centre.X + (outX * wanted), a.Y + ((b.Y - a.Y) * t),
                centre.Z + (outZ * wanted));
            float reach = MathF.Sqrt(worstSquared);
            foreach (int vertex in walls)
            {
                Vector3 v = Source.Vertices[vertex];
                float ax = v.X - around.X, az = v.Z - around.Z;
                if (MathF.Abs(ax) > reach || MathF.Abs(az) > reach)
                    continue;
                // On the same storey as the waypoint being proposed, for the same reason Nearest asks:
                // a gap is only too narrow to stand in if both sides of it are walls this body meets.
                if ((ax * ax) + (az * az) <= worstSquared && SameStorey(v.Y, around.Y))
                    return false;
            }
            return true;
        }

        // Triangles the shortcut pass may walk over one route. It is a ceiling on pathological input, not
        // a working budget: the case this exists for — open ground, where the destination is visible from
        // the start — spends one walk and stops. A corridor that admits no shortcut at all is what burns
        // it, and there the funnel's own route is already the answer, so stopping early costs nothing.
        private const int ShortcutBudget = 2048;

        // The funnel returns the shortest route INSIDE the corridor A* handed it, and that corridor is
        // chosen on centre-to-centre cost. Over a tessellated floor that cost is essentially Manhattan:
        // "north, then east" scores the same as the diagonal, so the tie is arbitrary and the route
        // wanders with nothing in its way — measured at 1.174x the direct distance over 14 waypoints on
        // an empty field, leaving the start almost due north before cutting back across.
        //
        // Fixing the metric means an any-angle search over the whole graph. Shortcutting the RESULT
        // reaches the same route far more cheaply: replace a run of waypoints with the straight segment
        // between its ends whenever that segment stays on enabled mesh and keeps the body's width off
        // every wall it passes. Each replacement is a triangle inequality, so the route can only get
        // shorter, and it can only use mesh the walk itself verified — it cannot invent a shortcut
        // through a wall the corridor was avoiding.
        private void Shortcut(List<Vector3> output, int begin, int startTriangle,
            List<Vector3> scratch, float radius)
        {
            if (output.Count - begin < 3)
                return;

            int budget = ShortcutBudget;
            scratch.Clear();
            scratch.Add(output[begin]);
            int anchor = begin;
            int anchorTriangle = startTriangle;
            // One reach for the whole route: every walk resets it, so the cost is a generation bump
            // per segment rather than an allocation.
            ClearanceReach clearance = TakeClearanceReach();

            // A body ground-snaps, so what it actually covers is the SURFACE under the line, not the
            // line. Over a rise that can be longer than the detour it replaces — A* costs its corridor
            // in 3D and will happily go round a ridge, and swapping that for a straight line over the
            // top is a shortcut only in plan. So the walk measures the ground it crosses and a shortcut
            // has to beat the waypoints it removes.
            //
            // The comparison is deliberately one-sided: the run being replaced is measured as straight
            // segments between its waypoints, which do not follow the ground either, so it is
            // undercounted. That biases against shortcutting over broken ground, which is the safe way
            // to be wrong here.
            //
            // A reach that FILLED is not an ordinary refusal, and the pass must not probe around it.
            // The walk spends ONE unit of the route's budget however far the flood ran, so a mesh
            // tessellated finer than the body is wide — where a flood fills rather than finishes —
            // buys 2049 faces of spreading for one unit, and the pass then tries the next waypoint,
            // and the next anchor, and pays it again. Measured on the 5 x 2 m floor at 0.05 m faces,
            // one public TryPath from (0.1, 1.9) to (4.1, 0.4): 11 584 faces spread over two filled
            // reaches, against 6 860 over one when the first of them ends the pass.
            //
            // So it ends the pass, exactly as an exhausted WALK budget does and for the same reason:
            // where the reach refuses, the funnel's own route is already the answer, and giving up
            // is what the ceiling exists to do. It gives up for the whole route rather than the one
            // segment, which is the same trade the walk budget makes, and it costs waypoints on that
            // mesh — the query above keeps 10 where it kept 8. Nothing changes on a mesh whose faces
            // are the size of a body or larger, because no reach there ever fills.
            bool exhausted = false;
            bool Reaches(int probe, out int end)
            {
                end = -1;
                if (exhausted)
                    return false;
                if (!TryLine(anchorTriangle, output[anchor], output[probe], radius, ref budget,
                    clearance, out int reached, out float surface))
                {
                    exhausted = clearance.Exhausted;
                    return false;
                }
                float replaced = 0f;
                for (int i = anchor + 1; i <= probe; i++)
                    replaced += output[i - 1].DistanceTo(output[i]);
                if (surface > replaced)
                    return false;
                end = reached;
                return true;
            }

            try
            {
                while (anchor < output.Count - 1)
                {
                    int last = output.Count - 1;
                    int furthest = anchor + 1;
                    int reached = -1;

                    // Double the reach while it keeps working, then bisect what is left. Trying the
                    // destination first and counting back would find the same answer, but it costs a
                    // walk per waypoint on the routes that have no shortcut at all — and those are
                    // exactly the routes where the funnel is already the answer, because a corner it
                    // emitted is by definition something the corridor forced. Here that case costs ONE
                    // failed walk per waypoint, which stops at the first wall.
                    int reach = 2;
                    int limit = last;
                    while (anchor + reach <= last)
                    {
                        if (!Reaches(anchor + reach, out int end))
                        {
                            limit = anchor + reach - 1;
                            break;
                        }
                        furthest = anchor + reach;
                        reached = end;
                        reach *= 2;
                    }
                    for (int low = furthest + 1, high = limit; low <= high;)
                    {
                        int middle = low + ((high - low) / 2);
                        if (Reaches(middle, out int end))
                        {
                            furthest = middle;
                            reached = end;
                            low = middle + 1;
                        }
                        else
                        {
                            high = middle - 1;
                        }
                    }

                    scratch.Add(output[furthest]);
                    anchor = furthest;
                    // A kept waypoint that no walk reached is a funnel corner, which sits on the mesh;
                    // the grid lookup is the same one the endpoints use and only runs when a shortcut
                    // failed.
                    anchorTriangle = reached >= 0 ? reached : ClosestTriangle(output[anchor], out _);
                    if (anchorTriangle < 0 || budget <= 0 || exhausted)
                    {
                        for (int i = anchor + 1; i < output.Count; i++)
                            scratch.Add(output[i]);
                        break;
                    }
                }
            }
            finally
            {
                _clearance.Add(clearance);
            }

            output.RemoveRange(begin, output.Count - begin);
            output.AddRange(scratch);
        }

        // Walks the mesh along an XZ segment, face to face through the edge it leaves each one by — the
        // navmesh raycast, with a clearance test on top. It refuses the moment the segment leaves the
        // walkable region, comes within the body's radius of a wall, or exhausts the budget.
        //
        // It asks for the same width the portals inset by, so a shortcut can never be tighter than the
        // route it replaces. It is in fact a stronger test: the portal inset runs ALONG the portal and so
        // yields only radius * sin(angle) away from the wall the portal meets, while this measures the
        // real distance from the segment to the wall.
        //
        // TWICE, and this is what the storey filter costs. The clearance test skips an edge more than a
        // storey off the GROUND under the segment where that edge comes nearest to it — and the reach it
        // reads that ground from is filled BY the walk, so a single pass answers about the far end of the
        // segment from the height at the near end. On level ground the two are the same number and it
        // does not matter; on anything that climbs, the profile is metres too low exactly where the
        // clearance test is deciding whether a wall is real, and it decides in the UNSAFE direction —
        // every edge above the start looks like another storey and is skipped, walls included. Nor does
        // the walk arriving there later repair it: the reach has already stamped those faces, and a
        // stamped face is never tested again.
        //
        // Measured on a two-storey mesh whose ramp is one span rather than three 1 m ones, a leg
        // climbing that ramp and drifting onto its own side boundary — the wall of the very face the
        // body is walking on, 2.8 m above where the leg started:
        //
        //     ends 0.45 m off it     accepted    correct, that is radius + Clearance
        //     ends 0.10 m off it     accepted    <- the ramp's own wall, missed by 0.35 m
        //
        // and it stays accepted with the upper floor deleted, so it is not the two-storey case at all:
        // any climb does it. So the profile is built FIRST, by a walk that only measures — the same
        // traversal with the clearance test off — and the clearance walk then reads a profile that
        // covers the whole segment, endpoint included. The measuring walk spends a COPY of the budget:
        // it visits exactly the faces the real one does, so charging for it twice would halve the
        // shortcut budget for the same work.
        //
        // The alternative was to leave one walk and not stamp a face whose edge the storey test skipped,
        // so it is re-tested once the profile reaches that far. It re-tests a face per step per storey
        // disagreement instead of walking the segment twice, but it can only ever be as good as the
        // profile the walk has SO FAR: at the last face there is no later step to repair, and the last
        // face is precisely where the fixture above hides its wall. Measuring first has no such tail.
        private bool TryLine(int startTriangle, Vector3 from, Vector3 to, float radius, ref int budget,
            ClearanceReach reach, out int endTriangle, out float surface)
        {
            endTriangle = startTriangle;
            surface = 0f;
            if (startTriangle < 0)
                return false;
            reach.Begin();
            int measuring = budget;
            Walk(startTriangle, from, to, radius, ref measuring, reach, measure: true, out _, out _);
            return Walk(startTriangle, from, to, radius, ref budget, reach, measure: false,
                out endTriangle, out surface);
        }

        // One traversal. `measure` records the ground profile into the reach and tests nothing;
        // otherwise the profile is already there and every face is cleared against it.
        private bool Walk(int startTriangle, Vector3 from, Vector3 to, float radius, ref int budget,
            ClearanceReach reach, bool measure, out int endTriangle, out float surface)
        {
            endTriangle = startTriangle;
            surface = 0f;

            float ax = from.X, az = from.Z;
            float dx = to.X - ax, dz = to.Z - az;
            int current = startTriangle;
            // Where the walk entered the face it is on, and how high the ground was there. A body
            // ground-snaps, so what it actually covers is the surface under the line, not the line.
            float markX = ax, markZ = az;
            float markY = TryHeightOn(startTriangle, ax, az, out float seed) ? seed : from.Y;
            if (measure)
                reach.Mark(0f, markY);

            while (true)
            {
                if (--budget < 0 || !_enabled[current])
                    return false;

                int i0 = Source.Triangles[current * 3];
                int i1 = Source.Triangles[(current * 3) + 1];
                int i2 = Source.Triangles[(current * 3) + 2];
                if (!measure && !ClearOfWalls(current, from, to, radius, reach))
                    return false;

                // The edge to leave by is one the direction points OUT through — decided against the
                // opposite vertex, so it needs no winding convention. Testing "furthest along so far"
                // instead is what a first draft did, and it is wrong at t = 0: a segment starting exactly
                // on a face boundary, which is where funnel corners sit, has its only exit at t = 0. That
                // exit was skipped, no crossing was found, and the walk concluded the segment ended
                // inside the face and reported it clear — straight through a wall.
                //
                // The outward test also excludes the edge just entered by, since the direction points
                // inward there, so nothing has to remember where the walk came from.
                int exitA = -1, exitB = -1, exitR = -1;
                float exit = float.MaxValue;
                for (int e = 0; e < 3; e++)
                {
                    int va = e == 0 ? i0 : e == 1 ? i1 : i2;
                    int vb = e == 0 ? i1 : e == 1 ? i2 : i0;
                    int vr = e == 0 ? i2 : e == 1 ? i0 : i1;
                    Vector3 p = Source.Vertices[va], q = Source.Vertices[vb];
                    float ex = q.X - p.X, ez = q.Z - p.Z;
                    float outward = (ex * dz) - (ez * dx);
                    float inward = (ex * (Source.Vertices[vr].Z - p.Z))
                        - (ez * (Source.Vertices[vr].X - p.X));
                    float denominator = (dx * ez) - (dz * ex);
                    if (MathF.Abs(denominator) < 1e-9f)
                        continue; // parallel to this edge: it cannot be the one crossed
                    float px = p.X - ax, pz = p.Z - az;
                    float along = ((px * ez) - (pz * ex)) / denominator;
                    float across = ((px * dz) - (pz * dx)) / denominator;
                    if (across < -1e-4f || across > 1f + 1e-4f)
                        continue; // crosses the edge's line, but past its ends
                    // The direction has to point away from the opposite vertex for this to be the way out.
                    if (outward * inward >= 0f || along < -1e-4f || along >= exit)
                        continue;
                    exit = along;
                    exitA = va;
                    exitB = vb;
                    exitR = vr;
                }

                if (exit > 1f)
                {
                    // The segment ends inside this face in XZ — which says nothing about WHICH storey
                    // the face is. The walk follows adjacency, so inside a building whose upper floor
                    // overlaps the ground floor, a walk that never left the ground floor still arrives
                    // under an upstairs waypoint and would call the segment clear. Accepting that turns
                    // a route up the stairs into a straight line through the ceiling, and the movement
                    // code cannot notice: CalculateVelocity discards Y and the body ground-snaps, so the
                    // zombie simply stays downstairs and counts the target as reached below it.
                    if (!CarriesPoint(current, to))
                        return false;
                    float end = TryHeightOn(current, to.X, to.Z, out float last) ? last : markY;
                    // The far end of the profile is the segment's own end, not the last edge it crossed.
                    // A face the segment stops INSIDE keeps rising under it, and the wall it stops next
                    // to is the one the near miss is measured against.
                    if (measure)
                        reach.Mark(1f, end);
                    surface += Rise(markX, markZ, markY, to.X, to.Z, end);
                    endTriangle = current;
                    return true;
                }

                float crossX = ax + (dx * exit), crossZ = az + (dz * exit);
                float crossY = TryHeightOn(current, crossX, crossZ, out float height) ? height : markY;
                surface += Rise(markX, markZ, markY, crossX, crossZ, crossY);
                markX = crossX;
                markZ = crossZ;
                markY = crossY;
                if (measure)
                    reach.Mark(exit, crossY);

                // `exit` is only ever set together with the pair, so past here a crossing was found.
                //
                // An edge can carry more than two faces, and taking the first match would happily pick
                // one on the side the walk is already on — a coplanar duplicate, say. The next iteration
                // then finds the same exit at the same parameter and steps back, and the two faces
                // trade the walk between them until the whole budget is gone, so an otherwise clear
                // route that crosses one non-manifold edge loses all of its shortcutting.
                //
                // The face to cross to is the one whose opposite vertex lies on the far side of the
                // edge from this face's.
                int next = -1;
                float here = SideOfEdge(exitA, exitB, exitR);
                for (int at = _edgeStart[current]; at < _edgeStart[current + 1] && next < 0; at++)
                {
                    Connection c = _edges[at];
                    if (!_enabled[c.To])
                        continue;
                    if ((c.VertexA != exitA || c.VertexB != exitB)
                        && (c.VertexA != exitB || c.VertexB != exitA))
                        continue;
                    int opposite = OppositeVertex(c.To, exitA, exitB);
                    if (opposite >= 0 && here * SideOfEdge(exitA, exitB, opposite) < 0f)
                        next = c.To;
                }
                // No face across it: either a wall, or nothing on the far side that the walk can tell
                // apart from where it already is. The first is unreachable while the clearance test
                // above runs first — an edge with no enabled face across it IS a wall, and a segment
                // leaving through it is zero away from it. The second is a fold this cannot resolve, and
                // refusing costs a shortcut where guessing would cost the budget.
                if (next < 0)
                    return false;

                current = next;
            }
        }

        public bool HasClearLine(int start, Vector3 from, Vector3 to, float radius, ref int budget)
        {
            ClearanceReach reach = TakeClearanceReach();
            try
            {
                return TryLine(start, from, to, radius, ref budget, reach, out _, out _);
            }
            finally
            {
                _clearance.Add(reach);
            }
        }

        private ClearanceReach TakeClearanceReach() =>
            _clearance.TryTake(out ClearanceReach? reach) ? reach : new ClearanceReach(_centres.Length);

        private static float Rise(float ax, float az, float ay, float bx, float bz, float by)
        {
            float dx = bx - ax, dy = by - ay, dz = bz - az;
            return MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
        }

        // Which side of the directed edge a vertex falls on, by sign. Used to tell the face across an
        // edge from another face sharing it on this side.
        private float SideOfEdge(int edgeA, int edgeB, int vertex)
        {
            Vector3 p = Source.Vertices[edgeA], q = Source.Vertices[edgeB], v = Source.Vertices[vertex];
            return ((q.X - p.X) * (v.Z - p.Z)) - ((q.Z - p.Z) * (v.X - p.X));
        }

        // The vertex of `triangle` that is not on the given edge, or -1 if the edge is not one of its.
        private int OppositeVertex(int triangle, int edgeA, int edgeB)
        {
            for (int e = 0; e < 3; e++)
            {
                int v = Source.Triangles[(triangle * 3) + e];
                if (v != edgeA && v != edgeB)
                    return v;
            }
            return -1;
        }

        // Storeys are metres apart; a ramp or a kerb is not. Anything under this is the same surface
        // seen from slightly the wrong height — a target standing a little off the mesh, or a waypoint
        // on the far side of a slope — and anything over it is a different floor.
        private const float StoreyTolerance = 1f;

        // Is the point ON this face, rather than a floor above or below it? Barycentric in XZ, which is
        // the same interpolation LevelNavmesh.SnapXZ uses to decide the very same question.
        private bool CarriesPoint(int triangle, Vector3 point) =>
            TryHeightOn(triangle, point.X, point.Z, out float height)
            && MathF.Abs(height - point.Y) <= StoreyTolerance;

        private bool TryHeightOn(int triangle, float x, float z, out float height)
        {
            height = 0f;
            Vector3 a = Source.Vertices[Source.Triangles[triangle * 3]];
            Vector3 b = Source.Vertices[Source.Triangles[(triangle * 3) + 1]];
            Vector3 c = Source.Vertices[Source.Triangles[(triangle * 3) + 2]];
            float area2 = ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));
            if (MathF.Abs(area2) < 1e-6f)
                return false; // a zero-area face carries nothing; it would divide by its own degeneracy
            float w1 = (((b.Z - c.Z) * (x - c.X)) + ((c.X - b.X) * (z - c.Z))) / area2;
            float w2 = (((c.Z - a.Z) * (x - c.X)) + ((a.X - c.X) * (z - c.Z))) / area2;
            height = (w1 * a.Y) + (w2 * b.Y) + ((1f - w1 - w2) * c.Y);
            return true;
        }

        // Is a straight segment far enough from every wall in reach of it?
        //
        // A wall does not have to belong to a face the line enters: a sliver welded along a wall —
        // which baked tiles produce — puts the boundary a fraction of a metre beyond a shared edge
        // that is NOT one, and neither that edge nor the vertices on it are borders. Measured on a
        // 0.1 m sliver, a line 0.2 m from its outer wall was accepted for a body needing 0.45.
        //
        // Reaching one face further out covered that, and nothing beyond it. Measured on a CHAIN of
        // 0.1 m slivers welded along a 20 m floor, sweeping the line towards the outermost wall:
        //
        //     1 sliver     tightest accepted 0.450 m   correct
        //     2 slivers    accepted at       0.201 m   <- the outer wall is two rings away
        //     3 slivers    accepted at       0.301 m
        //     4 slivers    accepted at       0.401 m
        //
        // So the reach is bounded by DISTANCE instead of by a ring count. Spread outward from the
        // face over the walkable mesh, crossing a shared edge only where that edge itself comes
        // within the body's width of the segment.
        //
        // That cut is exactly right, not merely deeper. Take any wall point W within the width of the
        // segment, and P the nearest point of the segment to it. Every edge the straight line P -> W
        // crosses is met at a point no further from the segment than W is, so it is inside the width
        // too and this spread crosses it — unless that line leaves the walkable region first, and
        // then it has crossed a wall at least as close, which refuses just the same. An edge FURTHER
        // than the width away cannot be on the way to a wall that matters, so skipping it loses
        // nothing. The reach therefore has no depth limit and is still confined to the band of mesh
        // within one body width of the segment.
        //
        // The visited set spans the whole segment rather than one step of the walk, so a face on the
        // fringe is cleared once however many steps pass it — where the one-deep ring retested every
        // neighbour up to four times.
        //
        // The width is measured in XZ, and XZ cannot tell one storey from another. Adjacency alone
        // was supposed to: a spread over shared edges can only leave the floor it starts on by a way
        // a body could walk. It can — up a ramp — and then a floor folded back OVER that one is
        // ordinary shared mesh, every edge of it directly above the segment and so nought metres
        // from it in plan. Its outer boundary is then a wall "in reach", several metres up. Measured
        // on a two-storey mesh with a landing at the head of a ramp (so ramp -> landing -> upper
        // floor are all genuine shared edges rather than a fold): the flat run and the ramp, a
        // segment a body walks in one go, came back CLEAR before the distance bound and REFUSED
        // after, on the strength of the upper floor's far edge 3 m above its start.
        //
        // So an edge is only in reach when it is within the body's width in plan AND on the same
        // storey as the ground under the segment there — the walk's own heights, not the straight
        // line's, since the body ground-snaps and the line over a rise does not follow it. On level
        // ground every height is the same height and this changes nothing at all.
        private bool ClearOfWalls(int triangle, Vector3 from, Vector3 to, float radius,
            ClearanceReach reach)
        {
            float squared = (radius + Clearance) * (radius + Clearance);
            reach.Take(triangle);

            while (reach.TryTakeNext(out int at))
            {
                if (reach.Exhausted)
                    return false;
                int i0 = Source.Triangles[at * 3];
                int i1 = Source.Triangles[(at * 3) + 1];
                int i2 = Source.Triangles[(at * 3) + 2];
                for (int e = 0; e < 3; e++)
                {
                    int va = e == 0 ? i0 : e == 1 ? i1 : i2;
                    int vb = e == 0 ? i1 : e == 1 ? i2 : i0;
                    if (SegmentsDistanceSquaredXZ(from, to, Source.Vertices[va], Source.Vertices[vb],
                        out float along, out float height) >= squared)
                        continue; // out of the body's width: not a wall in reach, nor the way to one
                    if (MathF.Abs(height - reach.HeightAt(along)) > StoreyTolerance)
                        continue; // another storey: neither a wall the body can meet, nor a way to one

                    // One pass over the adjacency answers both questions, the way WallsAt does it:
                    // whether an enabled face still sits ACROSS this edge — a duplicate alongside is
                    // not floor across it — and where to spread next.
                    bool shared = false;
                    int mine = OppositeVertex(at, va, vb);
                    float here = mine >= 0 ? SideOfEdge(va, vb, mine) : 0f;
                    for (int i = _edgeStart[at]; i < _edgeStart[at + 1]; i++)
                    {
                        Connection c = _edges[i];
                        if (!_enabled[c.To])
                            continue;
                        if ((c.VertexA != va || c.VertexB != vb)
                            && (c.VertexA != vb || c.VertexB != va))
                            continue;
                        int theirs = OppositeVertex(c.To, va, vb);
                        if (theirs < 0 || here * SideOfEdge(va, vb, theirs) >= 0f)
                            continue;
                        shared = true;
                        reach.Take(c.To);
                    }
                    if (!shared)
                        return false; // a wall inside the body's width
                }

                // Border VERTICES as well as border edges. The spread follows shared edges, so a fan
                // that meets this one at a single vertex and nowhere else — which the builder's
                // non-manifold welds allow — is not walked into, and its wall would be missed.
                if (TooClose(i0) || TooClose(i1) || TooClose(i2))
                    return false;
            }
            return true;

            bool TooClose(int vertex)
            {
                if (!_borderVertex[vertex])
                    return false;
                Vector3 v = Source.Vertices[vertex];
                return PointSegmentDistanceSquaredXZ(v.X, v.Z, from.X, from.Z, to.X, to.Z,
                        out float along) < squared
                    && MathF.Abs(v.Y - reach.HeightAt(along)) <= StoreyTolerance;
            }
        }

        private static float PointSegmentDistanceSquaredXZ(float px, float pz,
            float ax, float az, float bx, float bz) =>
            PointSegmentDistanceSquaredXZ(px, pz, ax, az, bx, bz, out _);

        private static float PointSegmentDistanceSquaredXZ(float px, float pz,
            float ax, float az, float bx, float bz, out float along)
        {
            float dx = bx - ax, dz = bz - az;
            float lengthSquared = (dx * dx) + (dz * dz);
            along = lengthSquared <= 1e-12f
                ? 0f
                : Math.Clamp((((px - ax) * dx) + ((pz - az) * dz)) / lengthSquared, 0f, 1f);
            float cx = px - (ax + (dx * along)), cz = pz - (az + (dz * along));
            return (cx * cx) + (cz * cz);
        }

        // Crossing segments are zero apart; otherwise the minimum is attained at one of the four ends.
        //
        // `along` is how far down ab the two came closest, and `height` how high cd was there — what
        // the storey test needs, and it falls out of the same four candidates rather than costing a
        // second pass.
        private static float SegmentsDistanceSquaredXZ(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            out float along, out float height)
        {
            if (Area2(a, b, c) > 0f != Area2(a, b, d) > 0f
                && Area2(c, d, a) > 0f != Area2(c, d, b) > 0f)
            {
                // They cross: the meeting point is on both, so it is both answers at once.
                float denominator = ((b.X - a.X) * (d.Z - c.Z)) - ((b.Z - a.Z) * (d.X - c.X));
                if (MathF.Abs(denominator) > 1e-12f)
                {
                    along = Math.Clamp(
                        (((c.X - a.X) * (d.Z - c.Z)) - ((c.Z - a.Z) * (d.X - c.X))) / denominator,
                        0f, 1f);
                    float across = Math.Clamp(
                        (((c.X - a.X) * (b.Z - a.Z)) - ((c.Z - a.Z) * (b.X - a.X))) / denominator,
                        0f, 1f);
                    height = c.Y + ((d.Y - c.Y) * across);
                    return 0f;
                }
                along = 0f;
                height = (c.Y + d.Y) * 0.5f;
                return 0f;
            }

            float best = PointSegmentDistanceSquaredXZ(c.X, c.Z, a.X, a.Z, b.X, b.Z, out float bestAlong);
            float bestHeight = c.Y;
            Consider(PointSegmentDistanceSquaredXZ(d.X, d.Z, a.X, a.Z, b.X, b.Z, out float toD),
                toD, d.Y, ref best, ref bestAlong, ref bestHeight);
            Consider(PointSegmentDistanceSquaredXZ(a.X, a.Z, c.X, c.Z, d.X, d.Z, out float onA),
                0f, c.Y + ((d.Y - c.Y) * onA), ref best, ref bestAlong, ref bestHeight);
            Consider(PointSegmentDistanceSquaredXZ(b.X, b.Z, c.X, c.Z, d.X, d.Z, out float onB),
                1f, c.Y + ((d.Y - c.Y) * onB), ref best, ref bestAlong, ref bestHeight);
            along = bestAlong;
            height = bestHeight;
            return best;
        }

        private static void Consider(float candidate, float candidateAlong, float candidateHeight,
            ref float best, ref float bestAlong, ref float bestHeight)
        {
            if (candidate >= best)
                return;
            best = candidate;
            bestAlong = candidateAlong;
            bestHeight = candidateHeight;
        }

        private static void AppendFunnel(List<Vector3> output, List<Portal> portals, Vector3 destination)
        {
            Vector3 apex = portals[0].Left;
            Vector3 left = apex, right = apex;
            int apexIndex = 0, leftIndex = 0, rightIndex = 0;

            for (int i = 1; i < portals.Count; i++)
            {
                Vector3 nextLeft = portals[i].Left;
                Vector3 nextRight = portals[i].Right;

                if (Area2(apex, right, nextRight) <= 0f)
                {
                    if (SameXZ(apex, right) || Area2(apex, left, nextRight) > 0f)
                    {
                        right = nextRight;
                        rightIndex = i;
                    }
                    else
                    {
                        AddCorner(output, left);
                        apex = left;
                        apexIndex = leftIndex;
                        left = apex;
                        right = apex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                        continue;
                    }
                }

                if (Area2(apex, left, nextLeft) >= 0f)
                {
                    if (SameXZ(apex, left) || Area2(apex, right, nextLeft) < 0f)
                    {
                        left = nextLeft;
                        leftIndex = i;
                    }
                    else
                    {
                        AddCorner(output, right);
                        apex = right;
                        apexIndex = rightIndex;
                        left = apex;
                        right = apex;
                        leftIndex = apexIndex;
                        rightIndex = apexIndex;
                        i = apexIndex;
                    }
                }
            }
            AddCorner(output, destination);
        }

        private static float Area2(Vector3 a, Vector3 b, Vector3 c) =>
            ((b.X - a.X) * (c.Z - a.Z)) - ((b.Z - a.Z) * (c.X - a.X));

        private static bool SameXZ(Vector3 a, Vector3 b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return (dx * dx) + (dz * dz) < 1e-8f;
        }

        private static void AddCorner(List<Vector3> output, Vector3 point)
        {
            if (output.Count == 0 || !SameXZ(output[^1], point))
                output.Add(point);
        }

        private static bool ContainsXZ(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
        {
            float d1 = Sign(point, a, b), d2 = Sign(point, b, c), d3 = Sign(point, c, a);
            bool negative = d1 < 0f || d2 < 0f || d3 < 0f;
            bool positive = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(negative && positive);
        }

        private static float Sign(Vector3 p, Vector3 a, Vector3 b) =>
            ((p.X - b.X) * (a.Z - b.Z)) - ((a.X - b.X) * (p.Z - b.Z));
    }
}
