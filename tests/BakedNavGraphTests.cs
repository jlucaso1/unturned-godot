using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class BakedNavGraphTests
{
    private static NavFlag Strip(int quads)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        for (int x = 0; x <= quads; x++)
        {
            vertices.Add(new Vector3(x, 0, 0));
            vertices.Add(new Vector3(x, 0, 1));
        }
        for (int x = 0; x < quads; x++)
        {
            int a = x * 2, b = a + 1, c = a + 2, d = a + 3;
            triangles.AddRange(new[] { a, b, c, b, d, c });
        }
        return new NavFlag
        {
            Center = new Vector3(quads / 2f, 0, 0.5f),
            Size = new Vector3(quads + 1f, 10, 2f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    [Fact]
    public void ConnectedTrianglesProducePortalPath()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { Strip(4) });
        Assert.Equal((14, 204L), graph.AdjacencyStorage);
        // 912 before the T-junction broad phase was counted into this. The rise is the grid, its
        // buckets, the border list and the pair set — transient, freed when Build returns, and
        // deliberately included so the figure the perf harness prints is not an understatement of what
        // a border-heavy map costs to build.
        Assert.Equal(2096, graph.BuildScratchBytes);
        var path = new List<Vector3>();
        var from = new Vector3(0.1f, 0, 0.5f);
        var to = new Vector3(3.9f, 0, 0.5f);

        Assert.True(graph.TryPath(from, to, path));

        // The route starts where it was asked from, and funnel/string-pulling removes every internal
        // portal because the complete corridor is open and straight.
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        Assert.Equal(2, path.Count);
        Assert.All(path, point => Assert.InRange(point.X, 0f, 4f));
    }

    [Fact]
    public void OpenStraightCorridor_IsStringPulledWithoutPortalZigZags()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { Strip(12) });
        Vector3 from = new(0.1f, 0, 0.5f), to = new(11.9f, 0, 0.5f);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, to, path));
        Assert.Equal(new[] { from, to }, path);
    }

    [Fact]
    public void RemovedBandReturnsAClosestReachablePartialPath()
    {
        NavFlag flag = Strip(3);
        var removed = new Dictionary<NavFlag, HashSet<int>>
        {
            [flag] = new HashSet<int> { 2, 3 }, // both faces of the middle quad
        };
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag }, removed);

        var from = new Vector3(0.1f, 0, 0.5f);
        var target = new Vector3(2.9f, 0, 0.5f);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, target, path));
        Assert.Equal(from, path[0]);
        Assert.NotEqual(target, path[^1]);
        Assert.InRange(path[^1].X, 0f, 1f);
    }

    [Fact]
    public void ProgressiveDisableImmediatelyRemovesACompletedCollisionBand()
    {
        NavFlag flag = Strip(3);
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });
        Vector3 from = new(0.1f, 0, 0.5f), to = new(2.9f, 0, 0.5f);
        Assert.True(graph.TryPath(from, to, new List<Vector3>()));

        Assert.Equal(2, graph.Disable(flag, new HashSet<int> { 2, 3 }));
        var partial = new List<Vector3>();
        Assert.True(graph.TryPath(from, to, partial));
        Assert.NotEqual(to, partial[^1]);
        Assert.InRange(partial[^1].X, 0f, 1f);
        Assert.Equal(0, graph.Disable(flag, new HashSet<int> { 2, 3, -1, 99 }));
    }

    [Fact]
    public void EndpointsMustShareAnAuthoredFlag()
    {
        NavFlag left = Strip(1);
        NavFlag right = Strip(1);
        right.Center += new Vector3(10, 0, 0);
        for (int i = 0; i < right.Vertices.Length; i++)
            right.Vertices[i] += new Vector3(10, 0, 0);
        BakedNavGraph graph = BakedNavGraph.Build(new[] { left, right });

        Assert.False(graph.TryPath(
            new Vector3(0.2f, 0, 0.5f), new Vector3(10.8f, 0, 0.5f), new List<Vector3>()));
    }

    [Fact]
    public void TargetInExpandedBoundsSnapsToTheNearestAuthoredFace()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { Strip(4) });
        var from = new Vector3(0.2f, 0, 0.5f);
        var target = new Vector3(36f, 0, 0.5f); // outside forcedBounds, inside its 64 m Bounds.dat expansion
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, target, path));
        Assert.Equal(from, path[0]);
        Assert.NotEqual(target, path[^1]);
        Assert.InRange(path[^1].X, 3.9f, 4f);

        Assert.False(graph.TryPath(from, new Vector3(70f, 0, 0.5f), new List<Vector3>()));
    }

    [Fact]
    public void SameTriangleStillStartsWhereItWasAsked()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { Strip(1) });
        var from = new Vector3(0.1f, 0, 0.1f);
        var target = new Vector3(0.3f, 0, 0.2f);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, target, path));
        Assert.Equal(new[] { from, target }, path);
    }

    [Fact]
    public void RepeatedSearchesReuseWorkspace_AndConcurrentSearchesStayIndependent()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { Strip(32) });
        var from = new Vector3(0.1f, 0, 0.25f);
        var to = new Vector3(31.9f, 0, 0.75f);
        for (int i = 0; i < 100; i++)
        {
            var path = new List<Vector3>();
            Assert.True(graph.TryPath(from, to, path));
            Assert.Equal(from, path[0]);
            Assert.Equal(to, path[^1]);
        }
        Assert.Equal(1, graph.SearchWorkspaceCount);

        const int Searches = 16;
        System.Threading.Tasks.Parallel.For(0, Searches, i =>
        {
            var path = new List<Vector3>();
            Vector3 a = i % 2 == 0 ? from : to;
            Vector3 b = i % 2 == 0 ? to : from;
            Assert.True(graph.TryPath(a, b, path));
            Assert.Equal(a, path[0]);
            Assert.Equal(b, path[^1]);
        });

        // One workspace per search is the ceiling, because a search only allocates one when it finds the
        // pool empty. How many of the sixteen actually do is a property of the runtime, not of the graph:
        // Parallel.For runs on the thread pool, which injects threads past the core count on its own
        // schedule, and ConcurrentBag.TryTake can also come back empty-handed under contention. Bounding
        // this by Environment.ProcessorCount asserted neither — it failed on a four-core runner that ran
        // six searches at once, and would have passed a graph that allocated per call on a wider machine.
        int pooled = graph.SearchWorkspaceCount;
        Assert.InRange(pooled, 1, Searches);

        // What the pool is actually for, asserted where it is deterministic: with every workspace handed
        // back, further searches take from the pool instead of allocating. Same claim the cold run above
        // makes, against a warm pool of unknown size.
        for (int i = 0; i < 100; i++)
        {
            var path = new List<Vector3>();
            Assert.True(graph.TryPath(from, to, path));
        }
        Assert.Equal(pooled, graph.SearchWorkspaceCount);
    }

    [Fact]
    public void CsrCache_RoundTripsRoutes_AndRejectsWrongFingerprintOrCorruption()
    {
        NavFlag flag = Strip(12);
        BakedNavGraph built = BakedNavGraph.Build(new[] { flag });
        using var cache = new MemoryStream();
        built.Write(cache, "topology-v1");
        byte[] bytes = cache.ToArray();

        Assert.True(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v1", new[] { flag }, out var loaded));
        var expected = new List<Vector3>();
        var actual = new List<Vector3>();
        Vector3 from = new(0.1f, 0, 0.2f), to = new(11.9f, 0, 0.8f);
        Assert.True(built.TryPath(from, to, expected));
        Assert.True(loaded!.TryPath(from, to, actual));
        Assert.Equal(expected, actual);
        Assert.Equal(built.AdjacencyStorage, loaded.AdjacencyStorage);
        Assert.Equal(0, loaded.BuildScratchBytes);

        Assert.False(BakedNavGraph.TryRead(new MemoryStream(bytes), "other", new[] { flag }, out _));
        bytes[0] ^= 0x7f;
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v1", new[] { flag }, out _));
    }

    [Fact]
    public void CsrCache_RejectsNegativeOrDecreasingEdgeOffsets()
    {
        NavFlag flag = Strip(3);
        using var cache = new MemoryStream();
        BakedNavGraph built = BakedNavGraph.Build(new[] { flag });
        built.Write(cache, "topology-v1");
        byte[] bytes = cache.ToArray();
        int offsets = EdgeOffsetsStart(bytes, flag);

        byte[] negative = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(offsets), -1);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(negative), "topology-v1", new[] { flag }, out _));

        byte[] decreasing = (byte[])bytes.Clone();
        int second = BinaryPrimitives.ReadInt32LittleEndian(decreasing.AsSpan(offsets + sizeof(int)));
        int thirdOffset = offsets + (2 * sizeof(int));
        Assert.True(second > 0);
        BinaryPrimitives.WriteInt32LittleEndian(decreasing.AsSpan(thirdOffset), second - 1);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(decreasing), "topology-v1", new[] { flag }, out _));

        byte[] outOfRange = (byte[])bytes.Clone();
        int finalOffset = offsets + ((flag.Triangles.Length / 3) * sizeof(int));
        BinaryPrimitives.WriteInt32LittleEndian(outOfRange.AsSpan(finalOffset),
            checked((int)built.AdjacencyStorage.Connections) + 1);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(outOfRange), "topology-v1", new[] { flag }, out _));
    }

    // The grid offsets are consumed exactly like the edge offsets — ClosestTriangle walks _cellItems from
    // _cellStart[cell] to _cellStart[cell + 1] with no bounds test of its own — but only the TERMINAL
    // offset was validated on read. A cache whose last offset is right and whose earlier ones are
    // anything at all loaded happily, and then indexed past the item array during a repath: on the
    // server tick, outside the try/catch TryRead has for exactly this.
    [Fact]
    public void CsrCache_RejectsNegativeOrDecreasingGridOffsets()
    {
        NavFlag flag = Field(8, step: 2f);
        using var cache = new MemoryStream();
        BakedNavGraph.Build(new[] { flag }).Write(cache, "grid-v1");
        byte[] bytes = cache.ToArray();
        int offsets = GridOffsetsStart(bytes, flag);

        // Sanity: untouched, it loads.
        Assert.True(BakedNavGraph.TryRead(new MemoryStream((byte[])bytes.Clone()), "grid-v1",
            new[] { flag }, out _));

        byte[] negative = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(negative.AsSpan(offsets + sizeof(int)), -1);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(negative), "grid-v1", new[] { flag }, out _));

        // Find a cell whose offset has actually advanced, so "one less than the previous" is a genuine
        // decrease rather than a no-op on a run of empty cells.
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offsets));
        int advanced = -1;
        for (int i = 1; i < count && advanced < 0; i++)
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offsets + ((i + 1) * sizeof(int)))) > 0)
                advanced = i;
        Assert.True(advanced > 0, "the fixture must produce a grid with at least one non-empty cell");

        byte[] decreasing = (byte[])bytes.Clone();
        int at = offsets + ((advanced + 1) * sizeof(int));
        int previous = BinaryPrimitives.ReadInt32LittleEndian(decreasing.AsSpan(at - sizeof(int)));
        BinaryPrimitives.WriteInt32LittleEndian(decreasing.AsSpan(at), previous - 1);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(decreasing), "grid-v1", new[] { flag }, out _));

        byte[] beyondItems = (byte[])bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(beyondItems.AsSpan(offsets + sizeof(int)), int.MaxValue);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(beyondItems), "grid-v1", new[] { flag }, out _));
    }

    // Walks the header and the CSR block to the first grid offset, mirroring FlagGraph.Read's order.
    private static int GridOffsetsStart(byte[] bytes, NavFlag flag)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8, leaveOpen: false);
        reader.ReadUInt32();
        reader.ReadInt32();
        reader.ReadString();
        Assert.Equal(1, reader.ReadInt32());
        int triangles = flag.Triangles.Length / 3;
        Assert.Equal(triangles, reader.ReadInt32());
        reader.BaseStream.Seek(triangles * 3L * sizeof(float), SeekOrigin.Current);
        Assert.Equal(triangles, reader.ReadInt32());
        reader.BaseStream.Seek(triangles, SeekOrigin.Current); // bool values
        Assert.Equal(triangles + 1, reader.ReadInt32());
        reader.BaseStream.Seek((triangles + 1L) * sizeof(int), SeekOrigin.Current);
        int edges = reader.ReadInt32();
        reader.BaseStream.Seek(edges * 3L * sizeof(int), SeekOrigin.Current);
        reader.ReadInt32(); // columns
        reader.ReadInt32(); // rows
        reader.ReadSingle(); // cellSize
        reader.ReadSingle(); // minX
        reader.ReadSingle(); // minZ
        return checked((int)reader.BaseStream.Position);
    }

    private static int EdgeOffsetsStart(byte[] bytes, NavFlag flag)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes), Encoding.UTF8, leaveOpen: false);
        reader.ReadUInt32();
        reader.ReadInt32();
        reader.ReadString();
        Assert.Equal(1, reader.ReadInt32());
        int triangles = flag.Triangles.Length / 3;
        Assert.Equal(triangles, reader.ReadInt32());
        reader.BaseStream.Seek(triangles * 3L * sizeof(float), SeekOrigin.Current);
        Assert.Equal(triangles, reader.ReadInt32());
        reader.BaseStream.Seek(triangles, SeekOrigin.Current); // bool values
        Assert.Equal(triangles + 1, reader.ReadInt32());
        return checked((int)reader.BaseStream.Position);
    }

    // A mesh big enough that the endpoint lookup has to use its grid rather than scanning: several
    // hundred triangles over a wide area, with a step in height so "the triangle under the point" and
    // "the nearest triangle" are different answers.
    private static NavFlag Field(int quadsPerSide, float step)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int side = quadsPerSide + 1;
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                vertices.Add(new Vector3(x, x < side / 2 ? 0f : step, z));

        for (int x = 0; x < quadsPerSide; x++)
            for (int z = 0; z < quadsPerSide; z++)
            {
                int a = (x * side) + z, b = a + 1, c = a + side, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }

        return new NavFlag
        {
            Center = new Vector3(quadsPerSide / 2f, 0, quadsPerSide / 2f),
            Size = new Vector3(quadsPerSide + 2f, 100f, quadsPerSide + 2f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    [Fact]
    public void GridLookupPicksTheSameTrianglesAsScanningThemAll()
    {
        // The index only earns its place if it cannot change the answer: for every point, on the mesh or
        // off it, the triangle it snaps to has to be the one an exhaustive scan would have chosen.
        NavFlag flag = Field(20, 4f);
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });

        int contained = 0;
        for (float x = -4f; x <= 24f; x += 1.3f)
            for (float z = -4f; z <= 24f; z += 1.3f)
                foreach (float y in new[] { -6f, 0.3f, 9f })
                {
                    var point = new Vector3(x, y, z);
                    (int expected, bool contains) = ScanForClosest(flag, point);
                    Assert.Equal(expected, graph.ClosestTriangleOf(0, point));
                    if (contains)
                        contained++;
                }

        Assert.True(contained > 100, "the sweep has to cover points over the mesh, not only beside it");
    }

    [Fact]
    public void GridLookupSkipsTrianglesTheGraphRemoved()
    {
        // Disabled triangles are not in the buckets at all, so the walk has to keep finding the nearest
        // one that is still there.
        NavFlag flag = Field(8, 0f);
        var removed = new HashSet<int>();
        for (int triangle = 0; triangle < 20; triangle++)
            removed.Add(triangle);

        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = removed });

        for (float x = 0.5f; x < 8f; x += 1.5f)
            for (float z = 0.5f; z < 8f; z += 1.5f)
            {
                int chosen = graph.ClosestTriangleOf(0, new Vector3(x, 0, z));
                Assert.True(chosen >= 0);
                Assert.DoesNotContain(chosen, removed);
            }
    }

    // What ClosestTriangle used to do: look at every triangle, prefer one whose footprint covers the
    // point (scored by height alone), otherwise take the nearest centre.
    private static (int Triangle, bool Contains) ScanForClosest(NavFlag flag, Vector3 point)
    {
        int closest = -1;
        float score = float.MaxValue;
        bool foundContaining = false;
        int count = flag.Triangles.Length / 3;
        for (int triangle = 0; triangle < count; triangle++)
        {
            Vector3 a = flag.Vertices[flag.Triangles[triangle * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(triangle * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(triangle * 3) + 2]];
            bool contains = ContainsXZ(a, b, c, point);
            if (foundContaining && !contains)
                continue;

            Vector3 centre = (a + b + c) / 3f;
            float dx = centre.X - point.X, dz = centre.Z - point.Z, dy = centre.Y - point.Y;
            float candidate = contains ? dy * dy : (dx * dx) + (dz * dz) + (0.25f * dy * dy);
            if ((contains && !foundContaining) || candidate < score)
            {
                foundContaining = contains;
                closest = triangle;
                score = candidate;
            }
        }
        return (closest, foundContaining);
    }

    private static bool ContainsXZ(Vector3 a, Vector3 b, Vector3 c, Vector3 point)
    {
        static float Sign(Vector3 p, Vector3 q, Vector3 r) =>
            ((p.X - r.X) * (q.Z - r.Z)) - ((q.X - r.X) * (p.Z - r.Z));

        float d1 = Sign(point, a, b), d2 = Sign(point, b, c), d3 = Sign(point, c, a);
        return !((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f));
    }

    // California2 is a Steam Workshop subscription, so it is never present in CI and the guards below stay
    // opportunistic by design. The attribute still turns "no install at all" into a visible skip.
    [RealDataFact]
    public void RealCalifornia2_LargeGraphBuildsAndRoutesAdjacentFaces()
    {
        string map = MapCatalog.ResolvePath(GameData.Install!, "California2");
        if (!Directory.Exists(map))
            return;
        List<NavFlag> flags = LevelNavmesh.Load(Path.Combine(map, "Environment"));
        int total = 0;
        foreach (NavFlag flag in flags)
            total += flag.Triangles.Length / 3;
        if (total <= 100_000) // workshop item absent or replaced with a different map
            return;

        BakedNavGraph graph = BakedNavGraph.Build(flags);
        foreach (NavFlag flag in flags)
        {
            var ownerByEdge = new Dictionary<(int, int), int>();
            int count = flag.Triangles.Length / 3;
            for (int triangle = 0; triangle < count; triangle++)
                for (int edge = 0; edge < 3; edge++)
                {
                    int a = flag.Triangles[(triangle * 3) + edge];
                    int b = flag.Triangles[(triangle * 3) + ((edge + 1) % 3)];
                    (int, int) key = a < b ? (a, b) : (b, a);
                    if (ownerByEdge.TryGetValue(key, out int neighbour))
                    {
                        Vector3 from = Centre(flag, neighbour);
                        Vector3 to = Centre(flag, triangle);
                        var path = new List<Vector3>();
                        Assert.True(graph.TryPath(from, to, path));
                        Assert.Equal(from, path[0]);
                        Assert.Equal(to, path[^1]);
                        return;
                    }
                    ownerByEdge[key] = triangle;
                }
        }
        Assert.Fail("California2 contained no adjacent navmesh faces");
    }

    // Progressive reconciliation disables faces one batch at a time, and the graph must end up in the
    // state a graph BUILT with those faces excluded would have been in. That is the whole licence for
    // keeping the original CSR instead of rebuilding.
    //
    // Non-manifold edges are where a cheap incremental update stops agreeing with a rebuild. The builder
    // connects EVERY pair of faces sharing an edge, so an edge can carry three of them; disabling one
    // leaves the other two joined across it and it did NOT become a wall. Treating it as one narrows
    // portals in the middle of open mesh, which is the wandering-route symptom.
    //
    // The face disabled here is a duplicate of an interior one, so all three of its edges survive it.
    // Nothing about the walkable region changes and the route must not move by so much as a waypoint.
    [Fact]
    public void DisablingOneFaceOfANonManifoldEdge_MatchesABuildWithoutIt()
    {
        NavFlag field = Field(32, 0f);
        const int Side = 33;
        // Quad (16, 16)'s first triangle, square in the middle of a straight run along z = 16.
        int a = (16 * Side) + 16, b = a + 1, c = a + Side;
        var triangles = new List<int>(field.Triangles) { a, b, c };
        var flag = new NavFlag
        {
            Center = field.Center,
            Size = field.Size,
            Vertices = field.Vertices,
            Triangles = triangles.ToArray(),
        };
        var duplicate = new HashSet<int> { (16 * 32 * 2) + (16 * 2) };

        BakedNavGraph rebuilt = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = duplicate });
        BakedNavGraph reconciled = BakedNavGraph.Build(new[] { flag });
        Assert.Equal(1, reconciled.Disable(flag, duplicate));

        var expected = new List<Vector3>();
        var actual = new List<Vector3>();
        var from = new Vector3(2, 0, 16);
        var to = new Vector3(30, 0, 16);
        Assert.True(rebuilt.TryPath(from, to, expected));
        Assert.True(reconciled.TryPath(from, to, actual));
        Assert.Equal(new[] { from, to }, expected);
        Assert.Equal(expected, actual);
    }

    // Two halves of one continuous floor, meeting along x = 2. The left half spans the join with a
    // SINGLE edge (2,0)-(2,2); the right half has a vertex at (2,1) on that line, so its side of the
    // join is TWO edges. No pair of vertex indices matches across the seam, which is the only thing
    // adjacency was keyed on, so all three edges were classified as walls and the halves were separate
    // islands. Recast output does this wherever tiles meet at different subdivision.
    private static NavFlag TJunction()
    {
        var vertices = new[]
        {
            new Vector3(0, 0, 0), // 0
            new Vector3(0, 0, 2), // 1
            new Vector3(2, 0, 0), // 2
            new Vector3(2, 0, 2), // 3
            new Vector3(2, 0, 1), // 4  the vertex that splits the left half's edge
            new Vector3(4, 0, 0), // 5
            new Vector3(4, 0, 1), // 6
            new Vector3(4, 0, 2), // 7
        };
        var triangles = new[]
        {
            0, 1, 2, 1, 3, 2, // left half: its right side is the single edge 2-3
            2, 4, 5, 4, 6, 5, // right half, lower: left side is edge 2-4
            4, 3, 6, 3, 7, 6, // right half, upper: left side is edge 4-3
        };
        return new NavFlag
        {
            Center = new Vector3(2, 0, 1),
            Size = new Vector3(6, 10, 4),
            Vertices = vertices,
            Triangles = triangles,
        };
    }

    // The broad phase indexes each border edge by the cells it spans, widened by the slack the join
    // test allows. Those widened bounds reach PAST both ends of the edge, so converting them back to a
    // position along it extrapolates — and dividing that overshoot by a dx of 2e-6 turned one column
    // into a million metres of Z, half a million indexed rows.
    //
    // What rules the blowup out is structural: clamping the interpolation parameter to [0, 1] means the
    // rows indexed for a column can never exceed the segment's own extent, whatever the slack does to
    // the column bounds. The scratch bound below is what makes that measurable — the defect showed up
    // as cost rather than as a wrong answer, so routing assertions alone passed either way and could
    // not have caught a regression. The ceiling is generous against the 52 KB this actually uses and
    // still orders of magnitude under the blowup.
    [Fact]
    public void ALongNearlyVerticalSliver_DoesNotExplodeTheBroadPhase()
    {
        var flag = new NavFlag
        {
            Center = new Vector3(0.5f, 0, 1000f),
            Size = new Vector3(4f, 10f, 2100f),
            Vertices = new[]
            {
                new Vector3(0f, 0, 0f),
                new Vector3(0.000002f, 0, 2000f),
                new Vector3(1f, 0, 0f),
                new Vector3(1.000002f, 0, 2000f),
            },
            Triangles = new[] { 0, 1, 2, 1, 3, 2 },
        };

        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });

        var path = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(0.5f, 0, 1f), new Vector3(0.5f, 0, 1999f), path));
        Assert.Equal(2, path.Count); // one open sliver, string-pulled straight
        Assert.True(graph.BuildScratchBytes < 200_000,
            $"broad phase used {graph.BuildScratchBytes} bytes of scratch on a two-triangle sliver");
    }

    [Fact]
    public void AFloorSplitByATJunction_IsStillOneWalkableSurface()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { TJunction() });
        var path = new List<Vector3>();
        var from = new Vector3(0.5f, 0, 1);
        var to = new Vector3(3.5f, 0, 1);

        Assert.True(graph.TryPath(from, to, path), "the two halves of one floor were not connected");
        Assert.False(graph.HasClearLine(0, from, to));
        Assert.True(path.Count >= 2);
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        // Straight across: the seam is not an obstacle, so nothing should be routed around.
        Assert.Equal(3f, Plan(path), 2);

        // And back. The stitch adds a connection in each direction, and the FUNNEL may use that portal
        // inside the corridor selected by A*. Border classification used to require both portal vertex
        // ids in the spanning triangle; the middle T vertex cannot be there by definition, so it inset
        // the opening almost to its midpoint and added a material detour. The any-angle line walker
        // remains deliberately conservative: collision-only walls can sit on a stitched seam, so it may
        // not invent a new corridor through one merely because the XZ navmesh overlaps there. That can
        // retain the 5 cm clearance inset visible below, but no longer sends the route to the portal's
        // far midpoint.
        var back = new List<Vector3>();
        Assert.True(graph.TryPath(to, from, back), "the seam was only crossable in one direction");
        Assert.False(graph.HasClearLine(0, to, from));
        Assert.Equal(to, back[0]);
        Assert.Equal(from, back[^1]);
        Assert.True(Plan(back) < 3.2f, $"the return route materially detoured: {Plan(back):F2} m");
    }

    // A 6 m tent-shaped ridge — 2 m of run either side of the crest — laid along +Z from z = -2 to the
    // far edge of the field, so the only way past it is round its -Z end. Everything else is flat.
    private static float RidgeHeight(float x, float z)
    {
        float crest = MathF.Abs(x) >= 2f ? 0f : 6f * (1f - (MathF.Abs(x) / 2f));
        return z >= -2f ? crest : z <= -3f ? 0f : crest * (z + 3f);
    }

    private static NavFlag RidgeField()
    {
        const int Columns = 25; // x = -12 .. 12
        const int Rows = 11;    // z =  -8 .. 2
        var vertices = new List<Vector3>();
        for (int x = 0; x < Columns; x++)
            for (int z = 0; z < Rows; z++)
                vertices.Add(new Vector3(x - 12, RidgeHeight(x - 12, z - 8), z - 8));

        var triangles = new List<int>();
        for (int x = 0; x < Columns - 1; x++)
            for (int z = 0; z < Rows - 1; z++)
            {
                int a = (x * Rows) + z, b = a + 1, c = a + Rows, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }

        return new NavFlag
        {
            Center = new Vector3(0, 0, -3),
            Size = new Vector3(26, 40, 12),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    // What the body actually covers, which is the ground under the route rather than the route: sampled
    // off the same height function the field was built from.
    private static float GroundLength(IReadOnlyList<Vector3> path)
    {
        const int Samples = 4000;
        float total = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            Vector3 from = path[i - 1], to = path[i];
            var previous = new Vector3(from.X, RidgeHeight(from.X, from.Z), from.Z);
            for (int s = 1; s <= Samples; s++)
            {
                float t = s / (float)Samples;
                float x = from.X + ((to.X - from.X) * t), z = from.Z + ((to.Z - from.Z) * t);
                var here = new Vector3(x, RidgeHeight(x, z), z);
                total += previous.DistanceTo(here);
                previous = here;
            }
        }
        return total;
    }

    // A rise A* deliberately goes AROUND, which the shortcut pass would put straight back OVER.
    //
    // Every other replacement the shortcut makes is licensed by the triangle inequality, and that holds
    // in the XZ plan. It does NOT hold on the surface, and the surface is what gets walked: zombies
    // ground-snap, so a segment that is shorter in plan can be longer to cover. A* already prices this
    // correctly — its edge cost is centre-to-centre in 3D — and here it prefers the flat way round the
    // end of the ridge to climbing six metres over it.
    //
    // Which makes the straight line from end to end genuinely tempting to the shortcut pass: it stays on
    // enabled mesh the whole way and clears every wall, and it is 16 m in plan against the detour's 17.6.
    // Only the surface-length comparison in Reaches refuses it, and refusing is right: over the crest is
    // 24.6 m of ground against the detour's 17.8.
    [Fact]
    public void ShortcutRefusesAStraightLineOverARiseThatIsLongerToWalkThanTheDetour()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { RidgeField() });
        Vector3 from = new(-8, 0, 0), to = new(8, 0, 0);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, to, path));

        // Four waypoints is both halves of the claim at once. The funnel's own corridor is six, so the
        // shortcut pass DID run and DID tighten the detour; collapsing it to the two-point straight line
        // over the crest is what the surface check prevents.
        Assert.Equal(4, path.Count);
        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        Assert.Equal(-3f, path[1].Z, 3);
        Assert.Equal(-3f, path[2].Z, 3);

        // Shorter in plan, longer on the ground — the whole reason the plan-only triangle inequality is
        // not enough here.
        Assert.InRange(Plan(path), 17.5f, 17.8f);
        Assert.InRange(Plan(new[] { from, to }), 15.9f, 16.1f);
        Assert.InRange(GroundLength(path), 17.7f, 18f);
        Assert.InRange(GroundLength(new[] { from, to }), 24.5f, 24.8f);
    }

    private static float Plan(IReadOnlyList<Vector3> path)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++) total += path[i - 1].DistanceTo(path[i]);
        return total;
    }

    private static Vector3 Centre(NavFlag flag, int triangle) =>
        (flag.Vertices[flag.Triangles[triangle * 3]]
            + flag.Vertices[flag.Triangles[(triangle * 3) + 1]]
            + flag.Vertices[flag.Triangles[(triangle * 3) + 2]]) / 3f;
}
