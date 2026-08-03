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
    private const int CacheVersion = 1;
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

    public bool TryPath(Vector3 from, Vector3 to, List<Vector3> path)
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
        return best != null && best.FindPath(bestFrom, bestTo, from, bestDestination, path);
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
    private readonly record struct Portal(Vector3 Left, Vector3 Right);
    private readonly record struct EdgeRecord(int KeyA, int KeyB, int Triangle, int A, int B, int Sequence);
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

        private sealed class SearchWorkspace
        {
            private readonly int[] _generationByTriangle;
            private int _generation;
            public readonly float[] Cost;
            public readonly int[] CameFrom;
            public readonly PriorityQueue<int, float> Frontier = new();
            public readonly List<int> Reverse = new();
            public readonly List<Portal> Portals = new();

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

            long directedCountLong = 0;
            for (int first = 0; first < records.Length;)
            {
                int last = first + 1;
                while (last < records.Length && records[last].KeyA == records[first].KeyA
                    && records[last].KeyB == records[first].KeyB) last++;
                long sharing = last - first;
                directedCountLong += sharing * (sharing - 1);
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
            ScratchBytes = ((long)records.Length * 24) + ((long)directed.Length * 24);

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
                // hitch every time a large flag finishes. Faces are only ever disabled, never
                // re-enabled, so borders only grow and this stays exact rather than merely cheap.
                for (int at = _edgeStart[triangle]; at < _edgeStart[triangle + 1]; at++)
                {
                    Connection c = _edges[at];
                    if (!_enabled[c.To])
                        continue;
                    _borderVertex[c.VertexA] = true;
                    _borderVertex[c.VertexB] = true;
                }
            }
            return changed;
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
                    bool shared = false;
                    for (int at = _edgeStart[t]; at < _edgeStart[t + 1] && !shared; at++)
                    {
                        Connection c = _edges[at];
                        if (!_enabled[c.To])
                            continue;
                        shared = (c.VertexA == v0 && c.VertexB == v1)
                            || (c.VertexA == v1 && c.VertexB == v0);
                    }
                    if (!shared)
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

        public bool FindPath(int start, int goal, Vector3 from, Vector3 to, List<Vector3> output)
        {
            if (start < 0 || goal < 0)
                return false;
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
                portals.Add(new Portal(from, from));
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
                            portals.Add(Inset(left, right,
                                _borderVertex[leftVertex], _borderVertex[rightVertex]));
                            break;
                        }
                    }
                }
                portals.Add(new Portal(destination, destination));
                AppendFunnel(output, portals, destination);
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
        private static Portal Inset(Vector3 left, Vector3 right, bool insetLeft, bool insetRight)
        {
            if (!insetLeft && !insetRight)
                return new Portal(left, right);

            Vector3 along = right - left;
            along.Y = 0f;
            float length = along.Length();
            if (length <= 1e-4f)
                return new Portal(left, right);

            // Half the portal only when BOTH ends move and could cross each other. A single moving end
            // has the whole portal to travel, and halving it there under-insets: a 0.6 m portal with one
            // wall end has room for the full 0.45 and was being given 0.299, leaving the capsule on the
            // vertex it was supposed to clear.
            float budget = (insetLeft && insetRight ? length * 0.5f : length) - 1e-3f;
            float inset = MathF.Min(AgentRadius + Clearance, budget);
            if (inset <= 0f)
                return new Portal(left, right);

            Vector3 step = along / length * inset;
            return new Portal(insetLeft ? left + step : left, insetRight ? right - step : right);
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
