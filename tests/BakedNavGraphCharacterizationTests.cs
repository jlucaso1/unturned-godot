using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// BakedNavGraph is the CPU pathfinder every zombie repaths through, and it is a candidate for being
// rewritten for speed. This file exists so that rewrite can be proved to change nothing: it pins what
// comes OUT of the public surface for a given input — the waypoints, the counts, the measured
// clearances, the true/false — and never how any of it is arrived at.
//
// BakedNavGraphTests and Zombies/PathQualityTests already pin the string-pulling, the doorway
// clearance, the grid index, the shortcut's surface-length guard and the non-manifold cases. Nothing
// here repeats those; this is the rest of the surface — the endpoints, the refusals, the radius, the
// cache, and the shapes of input that are malformed rather than merely awkward.
public class BakedNavGraphCharacterizationTests
{
    // Waypoints are compared to 0.1 mm. Every number in a route comes out of add/multiply/divide/sqrt
    // on floats, which IEEE-754 fixes bit for bit on every platform CI runs, so no tolerance is
    // strictly needed — but a rewrite is entitled to reassociate that arithmetic, which moves the last
    // ulp (~1e-7 at these magnitudes). 1e-4 m is a thousand times that and still three orders of
    // magnitude below anything a 0.4 m body could act on, so it cannot hide a real change of route.
    private const int Precision = 4;

    // A perfectly flat, unobstructed square of `quadsPerSide` squared quads, two triangles each,
    // sharing vertices so the faces are genuinely edge-adjacent. It exists to be the null case: any
    // route across it that is not a straight line is the graph's doing and not the mesh's.
    private static NavFlag FlatField(int quadsPerSide)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int side = quadsPerSide + 1;
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                vertices.Add(new Vector3(x, 0f, z));
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

    private const int WallColumn = 10;   // the wall occupies x in [10, 11]
    private const float OpeningMinZ = 7f;

    // A 20 m field walled across x in [10, 11], with `openQuads` metres of opening from z = 7. The
    // property it holds: the ONLY way from one side to the other is through that opening, so anything
    // a route does at the wall is forced rather than incidental.
    private static (NavFlag Flag, BakedNavGraph Graph) Opening(int openQuads)
    {
        const int Quads = 20;
        NavFlag flag = FlatField(Quads);
        var blocked = new HashSet<int>();
        for (int z = 0; z < Quads; z++)
        {
            if (z >= (int)OpeningMinZ && z < (int)OpeningMinZ + openQuads)
                continue;
            int quad = (WallColumn * Quads) + z;
            blocked.Add(quad * 2);
            blocked.Add((quad * 2) + 1);
        }
        return (flag, BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = blocked }));
    }

    // The same 20 m field with the wall closed all the way across: two walkable islands inside ONE
    // authored flag, with no route between them at all. This is the disconnected case, and it has to
    // be inside a single flag — two flags is a different code path, already pinned elsewhere.
    private static (NavFlag Flag, BakedNavGraph Graph) TwoIslands()
    {
        // An opening of no quads is a solid wall: Opening's skip condition is z >= 7 && z < 7, which
        // nothing satisfies. Delegating keeps the two fixtures walling the same column if it ever moves.
        return Opening(0);
    }

    private static void AssertRoute(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X, Precision);
            Assert.Equal(expected[i].Y, actual[i].Y, Precision);
            Assert.Equal(expected[i].Z, actual[i].Z, Precision);
        }
    }

    private static float DistanceToBox(Vector3 point, float minZ, float maxZ)
    {
        float dx = MathF.Max(MathF.Max(WallColumn - point.X, point.X - (WallColumn + 1)), 0f);
        float dz = MathF.Max(MathF.Max(minZ - point.Z, point.Z - maxZ), 0f);
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    // The closest the ROUTE — not just its corners — comes to either half of the wall. 2048 samples a
    // segment puts the sampling error under a millimetre on the longest leg here, well inside the
    // margins the assertions below leave.
    private static (float Worst, Vector3 At) WorstClearance(List<Vector3> path, float openQuads)
    {
        float worst = float.MaxValue;
        Vector3 at = Vector3.Zero;
        for (int i = 1; i < path.Count; i++)
            for (int s = 0; s <= 2048; s++)
            {
                Vector3 point = path[i - 1].Lerp(path[i], s / 2048f);
                float clearance = MathF.Min(DistanceToBox(point, -100f, OpeningMinZ),
                    DistanceToBox(point, OpeningMinZ + openQuads, 100f));
                if (clearance < worst)
                {
                    worst = clearance;
                    at = point;
                }
            }
        return (worst, at);
    }

    // Where the route crosses the middle of the wall band, interpolated rather than looked for among
    // the waypoints: the band is 1 m wide and the funnel has no reason to put a vertex inside it.
    private static List<float> CrossingsOfTheWall(List<Vector3> path)
    {
        var crossings = new List<float>();
        const float Mid = WallColumn + 0.5f;
        for (int i = 1; i < path.Count; i++)
        {
            float a = path[i - 1].X - Mid, b = path[i].X - Mid;
            if (a * b > 0f)
                continue;
            float t = MathF.Abs(b - a) < 1e-9f ? 0f : -a / (b - a);
            crossings.Add(path[i - 1].Z + ((path[i].Z - path[i - 1].Z) * t));
        }
        return crossings;
    }

    // ---------------------------------------------------------------- happy paths

    // The movement code reads index 0 as "where I am" and steers at index 1, so a route that does not
    // begin at the caller's own position makes it skip the first portal and cut the corner through
    // whatever stood there. And it has to END at the destination, or the zombie stops short of it.
    // Both ends are handed back verbatim — not snapped, not rounded — which is what lets the caller
    // compare its own target against path[^1] to tell a complete route from a partial one.
    [Theory]
    [InlineData(2f, 2f, 18f, 18f)]      // clean diagonal across open ground
    [InlineData(4f, 2f, 16f, 15f)]      // through the doorway, at an angle
    [InlineData(9f, 7.5f, 12f, 7.5f)]   // down the middle of the doorway
    [InlineData(16f, 15f, 4f, 2f)]      // and the same trip backwards
    public void ARouteBeginsWhereItWasAskedAndEndsAtTheDestination(
        float fromX, float fromZ, float toX, float toZ)
    {
        (NavFlag _, BakedNavGraph graph) = Opening(1);
        var from = new Vector3(fromX, 0, fromZ);
        var to = new Vector3(toX, 0, toZ);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, to, path));

        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        Assert.True(path.Count >= 2);
    }

    // TryPath APPENDS. Callers reuse one buffer across repaths, so whether the graph clears it is part
    // of the contract: a rewrite that started clearing would silently drop whatever the caller had put
    // there, and a rewrite that started at index 0 of a non-empty list would corrupt it.
    [Fact]
    public void TryPath_AppendsToTheCallersListInsteadOfClearingIt()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(8) });
        var marker = new Vector3(99, 99, 99);
        var path = new List<Vector3> { marker };

        Assert.True(graph.TryPath(new Vector3(1, 0, 1), new Vector3(7, 0, 7), path));

        AssertRoute(new[] { marker, new Vector3(1, 0, 1), new Vector3(7, 0, 7) }, path);
    }

    // A doorway route has to go THROUGH the doorway: cross the wall band exactly once, and cross it
    // inside the opening. Crossing twice means the route doubled back through it; crossing outside it
    // means the route went through the wall.
    //
    // All three of these cross at z = 7.500, the exact middle of the 1 m opening — the two portal ends
    // are both jambs, both get inset, and with equal demands the share leaves the midpoint.
    [Theory]
    [InlineData(4f, 2f, 16f, 15f)]
    [InlineData(2f, 18f, 18f, 2f)]
    [InlineData(9f, 7.5f, 12f, 7.5f)]
    public void ADoorwayRoute_CrossesTheWallOnceAndDownTheMiddleOfTheOpening(
        float fromX, float fromZ, float toX, float toZ)
    {
        (NavFlag _, BakedNavGraph graph) = Opening(1);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(new Vector3(fromX, 0, fromZ), new Vector3(toX, 0, toZ), path));

        List<float> crossings = CrossingsOfTheWall(path);
        Assert.Single(crossings);
        Assert.Equal(7.5f, crossings[0], 3);
    }

    // A mega is nearly twice a normal zombie's width, and a route built for the narrow body walks the
    // wide one into the jamb. PathQualityTests pins that each body clears its OWN width; this pins the
    // thing that makes that possible, which is that the radius actually MOVES the route.
    //
    // Measured on a 3 m opening with an angled approach, minimum distance from the route to the wall:
    //
    //     radius 0.00   ->  0.283   (the straight line, which passes through the opening unaided)
    //     radius 0.40   ->  0.542
    //     radius 0.75   ->  0.792
    //
    // Note this is not a monotone law and must not be asserted as one: radius 1.00 measures 1.000,
    // because past some width the funnel stops collapsing and the raw corridor is what comes back.
    // What holds — and what a rewrite must keep — is that each body is routed at least its own width
    // clear, and that the mega is given materially more room than the normal zombie on the same query.
    //
    // RE-PINNED at 1ee6002 ("Round the corner a leg cuts"), which lowered the two moving bodies:
    //
    //     bf9391e    0.40 -> 0.7064    0.75 -> 1.0837    margin 0.3773
    //     1ee6002    0.40 -> 0.5423    0.75 -> 0.7921    margin 0.2498
    //
    // The zero-radius case is untouched at 0.2826, which is the tell: this is not the clearance TEST
    // weakening — that would move all three — it is the ROUTES getting shorter. The old figures were
    // paid for by the over-approximating collinear branch in InsetFor, which demanded the whole portal
    // for any wall parallel to it, including walls running BACKWARDS from the direction of travel whose
    // nearest point is the vertex the plain inset already clears. Over-insetting those portals pushed
    // the corridor wide of the far jamb and the surplus showed up here as clearance nobody had asked
    // for. It was never a guarantee: the same branch gave a 0.6 m portal with one wall end an inset of
    // 0.299 and left the capsule sitting on the vertex. RoundCorners is not involved — suppress the
    // call and both numbers are identical.
    //
    // Both bodies still clear their own radius, which is the assertion that matters and is unchanged
    // below. The mega now eats 0.008 into its 0.05 steering skin (0.7921 against the 0.80 its inset
    // asks for) because RoundCorners repairs a leg only when it passes closer than the RADIUS, 0.75 —
    // 1ee6002 states that threshold and its reason, that a chord from a corner on the 0.80 circle can
    // never itself reach 0.80 and asking would subdivide forever.
    //
    // The "materially more room" margin is therefore re-pinned too, 0.3 -> 0.2. It has to stay well
    // clear of zero to keep this test two-sided — a rewrite that ignored the radius entirely would give
    // both bodies the 0.2826 straight line, margin 0.000 — and 0.2498 is still 71% of the 0.35 m the
    // mega is wider, where the old 0.3773 was 108% and the extra came out of the over-approximation.
    [Fact]
    public void AWiderBodyIsGivenAWiderBerth()
    {
        (NavFlag _, BakedNavGraph graph) = Opening(3);
        var from = new Vector3(4, 0, 2);
        var to = new Vector3(16, 0, 15);

        var narrow = new List<Vector3>();
        var wide = new List<Vector3>();
        var point = new List<Vector3>();
        Assert.True(graph.TryPath(from, to, narrow, BakedNavGraph.AgentRadius));
        Assert.True(graph.TryPath(from, to, wide, 0.75f));
        Assert.True(graph.TryPath(from, to, point, 0f));

        (float narrowWorst, Vector3 narrowAt) = WorstClearance(narrow, 3f);
        (float wideWorst, Vector3 wideAt) = WorstClearance(wide, 3f);
        (float pointWorst, Vector3 _) = WorstClearance(point, 3f);

        Assert.Equal(0.5423f, narrowWorst, 3);
        Assert.Equal(0.7921f, wideWorst, 3);
        Assert.Equal(0.2826f, pointWorst, 3);

        Assert.True(narrowWorst >= BakedNavGraph.AgentRadius,
            $"a 0.40 m body is routed {narrowWorst:0.000} m from the wall at "
            + $"({narrowAt.X:0.00}, {narrowAt.Z:0.00})");
        Assert.True(wideWorst >= 0.75f,
            $"a 0.75 m body is routed {wideWorst:0.000} m from the wall at "
            + $"({wideAt.X:0.00}, {wideAt.Z:0.00})");
        Assert.True(wideWorst > narrowWorst + 0.2f,
            $"the mega was given {wideWorst:0.000} m against the normal zombie's {narrowWorst:0.000} m");
    }

    // The default is the ordinary zombie, so a caller with no particular body in mind still gets a
    // usable route. It is a real default and not a coincidence: the same query at 0.20 comes back with
    // a different route, so the parameter is doing work at 0.40.
    //
    // RE-PINNED at 1ee6002 ("Round the corner a leg cuts"), from 4 waypoints to 8. Both halves of that
    // commit are visible here and neither is a route going anywhere new — the trip still leaves (4, 2),
    // still crosses the wall band exactly once at z = 7.500, still arrives at (16, 15):
    //
    //     bf9391e   4 wp   from, (9.550, 7.450), (11.450, 7.550), to        0.4730 m from the jamb
    //     1ee6002   8 wp   as below                                         0.4157 m from the jamb
    //
    // The waypoints are the InsetFor change, not RoundCorners: suppress the RoundCorners call and this
    // route is unchanged at 8. The old collinear branch demanded the WHOLE portal whenever a wall ran
    // parallel to it, including walls running backwards from the direction of travel, and a portal
    // that hands over all of itself lets the shortcut pass swallow the doorway in one segment. Correct
    // treatment leaves the doorway portals at their proper 0.45 inset — (10, 7.45) and (11, 7.55) are
    // exactly that — and the shortcut can no longer span them, so the corridor's own corners survive.
    //
    // The clearance fell with it, 0.4730 -> 0.4157, and that number is not a coincidence either: it is
    // 0.45 * cos(22.5 deg), one RoundCorners pass halving a right-angle turn. The old 0.4730 was bought
    // by the over-approximation, which was not a guarantee — the same branch gave a 0.6 m portal with
    // one wall end an inset of 0.299 and left the capsule on the vertex. 0.4157 is still clear of
    // AgentRadius; the 0.05 steering skin is the budget 1ee6002 documents spending, and
    // PathQualityTests.NoPartOfARoute_EntersOrGrazesAWall pins the same figure on the same fixture.
    [Fact]
    public void TheDefaultRadiusIsTheOrdinaryZombiesRadius()
    {
        (NavFlag _, BakedNavGraph graph) = Opening(1);
        var from = new Vector3(4, 0, 2);
        var to = new Vector3(16, 0, 15);

        var byDefault = new List<Vector3>();
        var explicitly = new List<Vector3>();
        var narrower = new List<Vector3>();
        Assert.True(graph.TryPath(from, to, byDefault));
        Assert.True(graph.TryPath(from, to, explicitly, BakedNavGraph.AgentRadius));
        Assert.True(graph.TryPath(from, to, narrower, 0.2f));

        Assert.Equal(0.4f, BakedNavGraph.AgentRadius);
        AssertRoute(explicitly, byDefault);
        AssertRoute(new[]
        {
            from,
            new Vector3(8f, 0, 7f),
            new Vector3(10f, 0, 7.45f),
            new Vector3(11f, 0, 7.55f),
            new Vector3(11.318198f, 0, 7.681802f),
            new Vector3(11.45f, 0, 8f),
            new Vector3(12f, 0, 10f),
            to,
        }, byDefault);
        Assert.NotEqual(narrower.Count, byDefault.Count);

        // The properties the route literal above is a witness for, asserted in their own right so a
        // rewrite that moves a waypoint is told WHICH of them it broke. Both ends verbatim, one
        // crossing, down the middle of the 1 m opening, and never nearer the wall than the body is
        // wide.
        Assert.Equal(from, byDefault[0]);
        Assert.Equal(to, byDefault[^1]);
        List<float> crossings = CrossingsOfTheWall(byDefault);
        Assert.Single(crossings);
        Assert.Equal(7.5f, crossings[0], 3);
        (float worst, Vector3 at) = WorstClearance(byDefault, 1f);
        Assert.Equal(0.4157f, worst, 3);
        Assert.True(worst >= BakedNavGraph.AgentRadius,
            $"the default body is routed {worst:0.0000} m from the wall at ({at.X:0.00}, {at.Z:0.00})");
    }

    // Repathing happens several times a second per zombie, from several threads at once, on a graph
    // that pools its search workspaces. If the same query could answer differently the zombie would
    // jitter between two routes; if a shared workspace leaked between threads the routes would be
    // wrong rather than merely different. Whole-route equality is the assertion — matching endpoints
    // would pass even while the middle changed every time.
    [Fact]
    public void TheSameQueryReturnsTheSameRoute_OnOneGraphOnAFreshOneAndFromManyThreads()
    {
        NavFlag flag = FlatField(24);
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });
        BakedNavGraph fresh = BakedNavGraph.Build(new[] { flag });
        var from = new Vector3(1.5f, 0, 2.5f);
        var to = new Vector3(22.5f, 0, 21.5f);

        var first = new List<Vector3>();
        Assert.True(graph.TryPath(from, to, first));
        AssertRoute(new[] { from, to }, first);

        for (int i = 0; i < 20; i++)
        {
            var again = new List<Vector3>();
            Assert.True(graph.TryPath(from, to, again));
            AssertRoute(first, again);
        }

        var independent = new List<Vector3>();
        Assert.True(fresh.TryPath(from, to, independent));
        AssertRoute(first, independent);

        Parallel.For(0, 32, _ =>
        {
            var concurrent = new List<Vector3>();
            Assert.True(graph.TryPath(from, to, concurrent));
            AssertRoute(first, concurrent);
        });
    }

    // ---------------------------------------------------------------- bad and edge paths

    // "No route exists" does NOT come back as false. TryPath returns TRUE and hands back a partial
    // route: the caller's own position, then the CENTRE of the closest face A* could actually reach.
    // That is deliberate — it matches NavigationServer, and a newly aggroed zombie behind a sealed
    // wall gets a collision-aware approach instead of standing still — but it means "found a route"
    // and "reached the target" are different questions, and the only way to tell them apart from
    // outside is that path[^1] is not the destination.
    //
    // Both endpoints are face centres of the 1 m tessellation, which is why they are thirds.
    [Fact]
    public void ADestinationWithNoRoute_ComesBackAsAPartialRouteToTheClosestReachableFaceCentre()
    {
        (NavFlag _, BakedNavGraph graph) = TwoIslands();
        var west = new Vector3(2, 0, 10);
        var east = new Vector3(18, 0, 10);

        var outward = new List<Vector3>();
        Assert.True(graph.TryPath(west, east, outward));
        AssertRoute(new[] { west, new Vector3(9.66667f, 0, 9.66667f) }, outward);
        Assert.NotEqual(east, outward[^1]);

        var back = new List<Vector3>();
        Assert.True(graph.TryPath(east, west, back));
        AssertRoute(new[] { east, new Vector3(11.33333f, 0, 9.33333f) }, back);

        // A query that stays on one island is unaffected — the partial behaviour is not contagious.
        var local = new List<Vector3>();
        Assert.True(graph.TryPath(west, new Vector3(9, 0, 4), local));
        AssertRoute(new[] { west, new Vector3(9, 0, 4) }, local);
    }

    // Endpoints do not have to be ON the mesh. A zombie standing on a doorstep the baker never covered,
    // or a player one metre past the last triangle, still has to be routed to. Both ends are kept
    // exactly as asked — the graph snaps its CORRIDOR to the nearest faces but never moves the points
    // it was given — so the caller's "am I there yet" test against path[^1] keeps working.
    [Fact]
    public void EndpointsOffTheMeshButInsideTheFlag_AreKeptVerbatim()
    {
        // The mesh covers x, z in [0, 8]; the flag's authored box is [-1, 9], so both of these are
        // inside the flag and outside the triangles.
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(8) });
        var from = new Vector3(-0.5f, 0, -0.5f);
        var to = new Vector3(8.5f, 0, 8.5f);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, to, path));

        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        AssertRoute(new[]
        {
            from,
            new Vector3(0.45f, 0, 0.55f),
            new Vector3(1f, 0, 2f),
            new Vector3(6f, 0, 7f),
            new Vector3(7.45f, 0, 7.55f),
            to,
        }, path);

        // Everything BETWEEN the two off-mesh ends is on the mesh: the corridor is real even though
        // the ends are not.
        for (int i = 1; i < path.Count - 1; i++)
        {
            Assert.InRange(path[i].X, 0f, 8f);
            Assert.InRange(path[i].Z, 0f, 8f);
        }
    }

    // Outside every authored flag there is no graph to search, so the answer is a flat refusal — and
    // the caller's list is left exactly as it was, because a caller that ignores the false and walks
    // its buffer must not find half a route in it.
    //
    // The destination gets 64 m of slack (Bounds.dat expands authored navigation by that much) and the
    // start gets none. The boundary is sharp: this flag's box ends at x = 9, so x = 72.9 is routed and
    // x = 73.1 is not.
    [Fact]
    public void AnEndpointOutsideEveryFlag_IsRefusedAndTheOutputIsLeftAlone()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(8) });
        var marker = new Vector3(7, 7, 7);

        var fromOutside = new List<Vector3> { marker };
        Assert.False(graph.TryPath(new Vector3(500, 0, 500), new Vector3(4, 0, 4), fromOutside));
        Assert.Equal(new[] { marker }, fromOutside);

        var beyondTheSlack = new List<Vector3> { marker };
        Assert.False(graph.TryPath(new Vector3(1, 0, 1), new Vector3(73.1f, 0, 4), beyondTheSlack));
        Assert.Equal(new[] { marker }, beyondTheSlack);

        // Just inside the slack: routed, and the route ends on the mesh edge closest to the target
        // rather than at the target itself.
        var insideTheSlack = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(1, 0, 1), new Vector3(72.9f, 0, 4), insideTheSlack));
        AssertRoute(new[]
        {
            new Vector3(1, 0, 1),
            new Vector3(7, 0, 3),
            new Vector3(8, 0, 4),
        }, insideTheSlack);
    }

    // A flag can be authored with a box and no navmesh at all — a deleted or never-baked flag. Nothing
    // may index into an empty triangle array, so every query has to answer rather than throw, and the
    // empty graph still has to serialize and load back as an empty graph.
    [Fact]
    public void AnEmptyFlag_RoutesNothingIndexesNothingAndStillRoundTrips()
    {
        var empty = new NavFlag { Center = Vector3.Zero, Size = new Vector3(64, 64, 64) };
        BakedNavGraph graph = BakedNavGraph.Build(new[] { empty });
        var marker = new Vector3(5, 5, 5);
        var path = new List<Vector3> { marker };

        Assert.False(graph.TryPath(Vector3.Zero, new Vector3(1, 0, 1), path));
        Assert.Equal(new[] { marker }, path);
        Assert.Equal(-1, graph.ClosestTriangleOf(0, Vector3.Zero));
        // No connections, and the only adjacency allocation is the one-entry CSR offset array.
        Assert.Equal((0, 4L), graph.AdjacencyStorage);
        Assert.Equal(0, graph.BuildScratchBytes);
        Assert.Equal(0, graph.SearchWorkspaceCount);

        using var cache = new MemoryStream();
        graph.Write(cache, "empty-v1");
        Assert.True(BakedNavGraph.TryRead(new MemoryStream(cache.ToArray()), "empty-v1",
            new[] { empty }, out BakedNavGraph? loaded));
        Assert.Equal((0, 4L), loaded!.AdjacencyStorage);
        Assert.False(loaded.TryPath(Vector3.Zero, new Vector3(1, 0, 1), new List<Vector3>()));

        // And a graph with no flags whatsoever, which is what a map with no navigation gives.
        BakedNavGraph nothing = BakedNavGraph.Build(Array.Empty<NavFlag>());
        Assert.False(nothing.TryPath(Vector3.Zero, new Vector3(1, 0, 1), new List<Vector3>()));
        Assert.Equal((0, 0L), nothing.AdjacencyStorage);
    }

    // SUSPECT — pinned as it is today, not endorsed.
    //
    // A zero-area face covers no ground, so welding one onto a flat field changes nothing about where
    // a body may stand. It nevertheless turns the two vertices it touches into WALL CORNERS: the
    // border test asks whether an enabled face still sits ACROSS each edge, decided by the sign of the
    // opposite vertex's side, and a degenerate face's opposite vertex is exactly ON the edge, so the
    // sign is zero, so nothing is ever across it and both ends are marked as borders.
    //
    // The result is a phantom obstacle in open ground. On an 8 m field with one face {v, v, w} added
    // at the interior vertices (4, 0, 4) and (4, 0, 5):
    //
    //     HasClearLine (1, 1) -> (7, 7)      clean: true    with the degenerate face: FALSE
    //     TryPath      (1, 1) -> (7, 7)      clean: 2 waypoints, straight
    //                                        with it: 3, detouring via (2.00, 6.00)
    //
    // That is the "wanders with nothing in the way" symptom, from data rather than from the corridor.
    // It matters because baked navmeshes really do contain degenerate faces — LevelNavmesh.SnapXZ
    // skips them explicitly, and TryHeightOn guards against dividing by their own degeneracy — so this
    // is reachable from real maps and not only from a fixture.
    //
    // Whether it is worth the vertex-to-face index it would take to fix is not this file's call. What
    // this file guarantees is that a rewrite cannot change it without someone noticing.
    //
    // Tracked as issue #57. When it IS fixed, this test gets INVERTED and its comment rewritten — a
    // SUSPECT pin asserts the defect, so a red one here means the fix landed, not that it broke
    // something. Deleting it would lose the only record that the behaviour was ever deliberate.
    [Fact]
    public void ADegenerateFace_TurnsItsOwnVerticesIntoPhantomWallCorners()
    {
        NavFlag clean = FlatField(8);
        const int Side = 9;
        int v = (4 * Side) + 4; // (4, 0, 4), an interior vertex with mesh all around it
        int w = (4 * Side) + 5; // (4, 0, 5), likewise
        var withDegenerate = new NavFlag
        {
            Center = clean.Center,
            Size = clean.Size,
            Vertices = clean.Vertices,
            Triangles = new List<int>(clean.Triangles) { v, v, w }.ToArray(),
        };

        BakedNavGraph good = BakedNavGraph.Build(new[] { clean });
        BakedNavGraph degenerate = BakedNavGraph.Build(new[] { withDegenerate });
        var from = new Vector3(1, 0, 1);
        var to = new Vector3(7, 0, 7);

        Assert.True(good.HasClearLine(0, from, to));
        Assert.False(degenerate.HasClearLine(0, from, to));

        var straight = new List<Vector3>();
        Assert.True(good.TryPath(from, to, straight));
        AssertRoute(new[] { from, to }, straight);

        var detour = new List<Vector3>();
        Assert.True(degenerate.TryPath(from, to, detour));
        AssertRoute(new[] { from, new Vector3(2, 0, 6), to }, detour);

        // The phantom only reaches as far as the body's own width: a line 1.4 m away from (4, 0, 5) is
        // untouched, which is what identifies this as a clearance test firing rather than the mesh
        // having genuinely changed shape.
        Assert.True(degenerate.HasClearLine(0, new Vector3(1, 0, 6.4f), new Vector3(7, 0, 6.4f)));
    }

    // Reconciliation can prune every face of a flag — a flag entirely inside collision, which happens
    // when a map's authored navigation outlives the building it was baked for. Nothing may then be
    // routed on it, nothing may snap to it, and the caller's buffer must be left alone. Both ways of
    // arriving there have to agree: excluded at build time, and disabled afterwards.
    [Fact]
    public void EveryFaceDisabled_RefusesEveryQueryWhicheverWayItGotThere()
    {
        NavFlag flag = FlatField(4);
        var all = new HashSet<int>();
        for (int triangle = 0; triangle < flag.Triangles.Length / 3; triangle++)
            all.Add(triangle);

        BakedNavGraph atBuild = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = all });
        var marker = new Vector3(9, 9, 9);
        var path = new List<Vector3> { marker };

        Assert.False(atBuild.TryPath(new Vector3(1, 0, 1), new Vector3(3, 0, 3), path));
        Assert.Equal(new[] { marker }, path);
        Assert.Equal(-1, atBuild.ClosestTriangleOf(0, new Vector3(1, 0, 1)));
        Assert.False(atBuild.HasClearLine(0, new Vector3(1, 0, 1), new Vector3(3, 0, 3)));
        // Nothing was ever connected, so the CSR carries no edges at all.
        Assert.Equal(0, atBuild.AdjacencyStorage.Connections);

        BakedNavGraph afterwards = BakedNavGraph.Build(new[] { flag });
        Assert.True(afterwards.TryPath(new Vector3(1, 0, 1), new Vector3(3, 0, 3), new List<Vector3>()));
        Assert.Equal(32, afterwards.Disable(flag, all));
        Assert.False(afterwards.TryPath(new Vector3(1, 0, 1), new Vector3(3, 0, 3), new List<Vector3>()));
        Assert.Equal(-1, afterwards.ClosestTriangleOf(0, new Vector3(1, 0, 1)));
    }

    // A radius of zero is a point, and a point is entitled to walk anywhere on the mesh — but not to
    // graze a wall, because the route is aimed at by a steering controller with a look-ahead and a
    // turn rate. The skin on top of the radius is 0.05 m and it survives a radius of zero exactly:
    //
    //     a straight run 0.050 m from the jamb   -> kept, measured clearance 0.05000
    //     a straight run 0.020 m from the jamb   -> refused, and the route is bent out to 0.05000
    //
    // Which is the whole two-sided claim: the skin is real, and it is the ONLY thing a zero radius
    // buys. A rewrite that dropped it would pass the first of these and fail the second.
    //
    // RE-PINNED at 1ee6002 ("Round the corner a leg cuts"). The refusal is unchanged — the 0.020 line
    // is still rejected, the route still comes back bent — but where it is bent TO moved, and moved
    // onto the number this test is named for:
    //
    //     bf9391e   3 wp   from, (11.000, 7.999), to      0.8509 m clear
    //     1ee6002   5 wp   as below                       0.0500 m clear
    //
    // 0.851 was the funnel collapsing the whole 3 m opening to its middle, which is what the old
    // collinear branch in InsetFor forced by demanding the entire portal for any wall parallel to it.
    // A zero-radius body asks for radius + Clearance = 0.050 and it now gets 0.05000019, exactly, at
    // (10, 7.05) and (11, 7.05) — the skin and not one millimetre more, which is the claim in the
    // title. The old number was 17x what was asked for and was an artefact, not a policy. Nothing here
    // dips below the skin: 0.05000019 is the worst point on the whole polyline, sampled 2048 times a
    // leg. RoundCorners is not involved — suppress the call and this route is unchanged.
    [Fact]
    public void ARadiusOfZeroBuysExactlyTheSteeringSkinAndNoMore()
    {
        (NavFlag _, BakedNavGraph graph) = Opening(3);

        var onTheSkin = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 7.05f), new Vector3(16, 0, 7.05f), onTheSkin, 0f));
        AssertRoute(new[] { new Vector3(4, 0, 7.05f), new Vector3(16, 0, 7.05f) }, onTheSkin);
        Assert.Equal(0.05f, WorstClearance(onTheSkin, 3f).Worst, 4);

        var insideTheSkin = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 7.02f), new Vector3(16, 0, 7.02f), insideTheSkin, 0f));
        AssertRoute(new[]
        {
            new Vector3(4, 0, 7.02f),
            new Vector3(4f, 0, 7f),
            new Vector3(10f, 0, 7.05f),
            new Vector3(11f, 0, 7.05f),
            new Vector3(16, 0, 7.02f),
        }, insideTheSkin);
        Assert.Equal(0.05f, WorstClearance(insideTheSkin, 3f).Worst, 4);

        // It is bent, not straight — the 0.020 m line really was refused and not merely re-measured.
        Assert.True(insideTheSkin.Count > 2);
        // And the skin is a floor, not a target it may undershoot anywhere along the polyline.
        Assert.True(WorstClearance(insideTheSkin, 3f).Worst >= 0.05f);
    }

    // A radius far wider than any opening on the map must not make the graph give up or produce
    // nonsense: every portal collapses, the shortcut pass refuses everything, and what comes back is
    // the funnel's raw corridor — longer and lumpier, but still a route between the two points asked
    // for, entirely on the mesh, with no infinities in it.
    [Fact]
    public void AnAbsurdRadius_StillReturnsARouteBetweenTheRightPointsOnTheMesh()
    {
        (NavFlag _, BakedNavGraph graph) = Opening(1);
        var from = new Vector3(4, 0, 2);
        var to = new Vector3(16, 0, 15);

        foreach (float radius in new[] { 1e6f, float.MaxValue })
        {
            var path = new List<Vector3>();
            Assert.True(graph.TryPath(from, to, path, radius));

            Assert.Equal(from, path[0]);
            Assert.Equal(to, path[^1]);
            Assert.True(path.Count > 2, $"radius {radius} collapsed the route to a straight line");
            foreach (Vector3 point in path)
            {
                Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z),
                    $"radius {radius} produced {point}");
                Assert.InRange(point.X, 0f, 20f);
                Assert.InRange(point.Z, 0f, 20f);
            }
            // Still through the opening, not through the wall.
            List<float> crossings = CrossingsOfTheWall(path);
            Assert.Single(crossings);
            Assert.InRange(crossings[0], OpeningMinZ, OpeningMinZ + 1f);
        }
    }

    // Asking for a route to where you already stand is what a zombie does on the tick it arrives. It
    // comes back as the point twice rather than once or not at all, because the movement code needs an
    // index 0 to read as "here" and an index 1 to steer at, and a one-element route would leave it
    // steering at nothing.
    [Fact]
    public void FromEqualToTo_ComesBackAsThatPointTwice()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(8) });
        var at = new Vector3(3.25f, 0, 4.75f);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(at, at, path));

        Assert.Equal(new[] { at, at }, path);
    }

    // Reconciliation calls Disable repeatedly with overlapping batches while zombies are routing on
    // the graph, so the return value is "how many faces this call actually took away" and not "how
    // many were named". Everything that cannot take a face away has to be a no-op returning zero —
    // a repeat, an out-of-range index, an empty batch, a flag belonging to some other graph — and none
    // of them may disturb the routes.
    [Fact]
    public void Disable_IsIdempotent_AndIgnoresUnknownFlagsAndOutOfRangeFaces()
    {
        NavFlag flag = FlatField(20);
        NavFlag stranger = FlatField(20);
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });

        var band = new HashSet<int>();
        for (int z = 0; z < 20; z++)
        {
            int quad = (WallColumn * 20) + z;
            if (z == (int)OpeningMinZ)
                continue;
            band.Add(quad * 2);
            band.Add((quad * 2) + 1);
        }

        Assert.Equal(38, graph.Disable(flag, band));
        var afterFirst = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 2), new Vector3(16, 0, 15), afterFirst));

        Assert.Equal(0, graph.Disable(flag, band));                                  // the same batch again
        Assert.Equal(0, graph.Disable(flag, new HashSet<int>()));                    // nothing named
        Assert.Equal(0, graph.Disable(flag, new HashSet<int> { -1, int.MinValue, int.MaxValue, 100_000 }));
        Assert.Equal(0, graph.Disable(stranger, new HashSet<int> { 0, 1, 2 }));      // a flag this graph never saw

        var afterTheNoOps = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 2), new Vector3(16, 0, 15), afterTheNoOps));
        AssertRoute(afterFirst, afterTheNoOps);

        // A batch that overlaps the first one is credited only with what it newly removes.
        var overlapping = new HashSet<int>(band) { 0, 1 };
        Assert.Equal(2, graph.Disable(flag, overlapping));

        // The same holds of the build-time exclusion list: a key for a flag the graph was not built
        // from is ignored rather than misapplied to the flag it does have.
        BakedNavGraph plain = BakedNavGraph.Build(new[] { flag });
        BakedNavGraph misdirected = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [stranger] = new HashSet<int> { 0, 1, 2, 3 } });
        Assert.Equal(plain.AdjacencyStorage, misdirected.AdjacencyStorage);
    }

    // Cache blobs come off disk and are trusted by nothing. A miss has to be a quiet false with a null
    // graph — never a throw, because TryRead runs on the server tick and the alternative to a rebuild
    // is a crash. Every prefix of a valid blob is checked here, not a few chosen lengths: truncation
    // can land in the middle of any field, and the length checks and the offset checks catch different
    // halves of that.
    [Fact]
    public void ACacheBlob_IsRefusedWhenTruncatedMisMagickedMisVersionedOrMisFingerprinted()
    {
        NavFlag flag = FlatField(4);
        BakedNavGraph built = BakedNavGraph.Build(new[] { flag });
        using var cache = new MemoryStream();
        built.Write(cache, "topology-v1");
        byte[] bytes = cache.ToArray();

        // Sanity: intact, it loads and routes.
        Assert.True(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v1", new[] { flag }, out var whole));
        Assert.True(whole!.TryPath(new Vector3(0.5f, 0, 0.5f), new Vector3(3.5f, 0, 3.5f), new List<Vector3>()));

        for (int length = 0; length < bytes.Length; length++)
        {
            var truncated = new byte[length];
            Array.Copy(bytes, truncated, length);
            Assert.False(BakedNavGraph.TryRead(new MemoryStream(truncated), "topology-v1",
                new[] { flag }, out BakedNavGraph? partial),
                $"a blob cut to {length} bytes was accepted");
            Assert.Null(partial);
        }

        byte[] magic = (byte[])bytes.Clone();
        magic[0] ^= 0xff;
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(magic), "topology-v1", new[] { flag }, out _));

        byte[] version = (byte[])bytes.Clone();
        version[4] = 9; // the version int follows the four magic bytes
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(version), "topology-v1", new[] { flag }, out _));

        Assert.False(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v2", new[] { flag }, out _));

        // Trailing bytes are a miss too: a blob that decodes and then has more after it is not the
        // blob that was written, whatever those bytes say.
        var trailing = new byte[bytes.Length + 4];
        Array.Copy(bytes, trailing, bytes.Length);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(trailing), "topology-v1", new[] { flag }, out _));

        // The sources have to match what was written: neither a different number of flags...
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v1", new[] { flag, flag }, out _));
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v1",
            Array.Empty<NavFlag>(), out _));
        // ...nor the same number of flags with a different topology.
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(bytes), "topology-v1",
            new[] { FlatField(6) }, out _));

        Assert.False(BakedNavGraph.TryRead(new MemoryStream(), "topology-v1", new[] { flag }, out _));
    }

    // The point of caching a reconciled graph is to skip the physics probe on the next boot, so what
    // has to survive the round trip is the DISABLED faces — the expensive part — not merely the mesh.
    // The bar is a route: the loaded graph, the graph it was written from, and a graph freshly built
    // with those faces excluded all have to answer identically.
    //
    // Note that Disable deliberately leaves the original CSR edges in place, so the loaded graph
    // reports the same connection count as before anything was disabled. That is not a leak: the edges
    // between surviving faces are the same either way, and the routes prove it.
    //
    // RE-PINNED at 1ee6002 ("Round the corner a leg cuts"), 7 waypoints to 10.
    //
    // The thing this test exists to catch did NOT happen and was checked before the number was
    // touched. All three graphs still return the IDENTICAL route, bit for bit, at every one of the ten
    // waypoints — reconciled, cached and freshly-excluded agree exactly as they did at bf9391e. The
    // route is longer for the same reason it is longer everywhere else on this branch, not because
    // routing became path-dependent on how the walkable region was arrived at.
    //
    //     bf9391e    7 wp   ... (6, 0.311), (6.550, 0.450), (7, 0.311) ...    0.5500 m from the band
    //     1ee6002   10 wp   as below                                          0.4157 m from the band
    //
    // Both halves of 1ee6002 are here: the InsetFor collinear correction takes it 7 -> 9, RoundCorners
    // adds the tenth. The four corners at 0.450 — (5.55, 1), (6, 0.55), (7, 0.55), (7.45, 1) — are the
    // band's two end jambs properly inset, and (5.682, 0.682) / (7.318, 0.682) are the rounding
    // waypoints, each exactly 0.450 from the jamb vertex its leg was cutting. What is left, 0.41575,
    // is 0.45 * cos(22.5 deg): one pass halves the right-angle turn, and the pass stops there because
    // its threshold is the radius 0.40, not radius + Clearance. It is the same signature and the same
    // number as the doorway fixture in PathQualityTests.NoPartOfARoute_EntersOrGrazesAWall.
    [Fact]
    public void ACacheRoundTrip_PreservesDisabledFaces_AndAgreesWithAFreshBuild()
    {
        NavFlag flag = FlatField(12);
        var band = new HashSet<int>();
        for (int z = 1; z < 12; z++)
        {
            int quad = (6 * 12) + z;
            band.Add(quad * 2);
            band.Add((quad * 2) + 1);
        }

        BakedNavGraph reconciled = BakedNavGraph.Build(new[] { flag });
        Assert.Equal(22, reconciled.Disable(flag, band));

        using var cache = new MemoryStream();
        reconciled.Write(cache, "band-v1");
        Assert.True(BakedNavGraph.TryRead(new MemoryStream(cache.ToArray()), "band-v1",
            new[] { flag }, out BakedNavGraph? loaded));

        BakedNavGraph rebuilt = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = band });

        var from = new Vector3(2, 0, 8);
        var to = new Vector3(10, 0, 8);
        var expected = new List<Vector3>();
        var fromCache = new List<Vector3>();
        var fromScratch = new List<Vector3>();
        Assert.True(reconciled.TryPath(from, to, expected));
        Assert.True(loaded!.TryPath(from, to, fromCache));
        Assert.True(rebuilt.TryPath(from, to, fromScratch));

        // The band is only open at z in [0, 1], so the route is forced round its end — proof that the
        // disabled faces really are disabled in all three and not merely absent from the assertion.
        AssertRoute(new[]
        {
            from,
            new Vector3(4f, 0, 5f),
            new Vector3(5.55f, 0, 1f),
            new Vector3(5.681802f, 0, 0.681802f),
            new Vector3(6f, 0, 0.55f),
            new Vector3(7f, 0, 0.55f),
            new Vector3(7.318198f, 0, 0.681802f),
            new Vector3(7.45f, 0, 1f),
            new Vector3(8f, 0, 7f),
            to,
        }, expected);
        AssertRoute(expected, fromCache);
        AssertRoute(expected, fromScratch);
        // Belt and braces on the point of the test, in a form no re-pin of the literal above can
        // quietly relax: the three routes are the same route, not merely three routes that each match
        // whatever the literal happens to say today.
        Assert.Equal(expected, fromCache);
        Assert.Equal(expected, fromScratch);

        Assert.Equal(reconciled.AdjacencyStorage, loaded.AdjacencyStorage);
        Assert.Equal(0, loaded.BuildScratchBytes); // nothing was built, so nothing was scratched
    }

    // Write is handed a stream the caller owns and keeps writing to, so it must not close it and must
    // leave the position after what it wrote. The other side of that: TryRead insists on consuming the
    // WHOLE stream, so two graphs concatenated into one file is a miss rather than the first of them.
    [Fact]
    public void Write_LeavesTheStreamOpenAndPositioned_AndOnlyOneBlobPerStreamIsAccepted()
    {
        NavFlag flag = FlatField(4);
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });

        using var stream = new MemoryStream();
        graph.Write(stream, "stream-v1");
        long written = stream.Position;
        Assert.True(written > 0);
        Assert.Equal(written, stream.Length);
        Assert.True(stream.CanWrite, "Write closed a stream it does not own");

        stream.Position = 0;
        Assert.True(BakedNavGraph.TryRead(stream, "stream-v1", new[] { flag }, out _));
        Assert.True(stream.CanRead, "TryRead closed a stream it does not own");
        Assert.Equal(written, stream.Position);

        using var twice = new MemoryStream();
        graph.Write(twice, "stream-v1");
        graph.Write(twice, "stream-v1");
        Assert.Equal(written * 2, twice.Length);
        Assert.False(BakedNavGraph.TryRead(new MemoryStream(twice.ToArray()), "stream-v1",
            new[] { flag }, out BakedNavGraph? doubled));
        Assert.Null(doubled);
    }

    // Flags are separate authored graphs and two of them may share an XZ footprint — a street and the
    // tunnel under it. Nothing in the flag box distinguishes them, so the choice is made on how far
    // the endpoints are from each flag's faces IN HEIGHT, and the only way to see that from outside is
    // to give the two flags different obstacles and watch which one the route obeys.
    //
    // The lower flag is walled at x in [4, 5] except at z in [0, 1]; the upper one, 20 m above the same
    // ground, is clear. Same XZ query, twenty metres apart in Y: one detours, one goes straight.
    [Fact]
    public void EndpointHeightPicksBetweenFlagsStackedOnTheSameGround()
    {
        NavFlag lower = FlatField(8);
        lower.Center = new Vector3(4, 0, 4);
        lower.Size = new Vector3(10, 200, 10);

        NavFlag upper = FlatField(8);
        for (int i = 0; i < upper.Vertices.Length; i++)
            upper.Vertices[i] += new Vector3(0, 20, 0);
        upper.Center = new Vector3(4, 20, 4);
        upper.Size = new Vector3(10, 200, 10);

        var walled = new HashSet<int>();
        for (int z = 1; z < 8; z++)
        {
            int quad = (4 * 8) + z;
            walled.Add(quad * 2);
            walled.Add((quad * 2) + 1);
        }
        BakedNavGraph graph = BakedNavGraph.Build(new[] { lower, upper },
            new Dictionary<NavFlag, HashSet<int>> { [lower] = walled });

        var downstairs = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(2, 0, 6), new Vector3(7, 0, 6), downstairs));
        Assert.Equal(new Vector3(2, 0, 6), downstairs[0]);
        Assert.Equal(new Vector3(7, 0, 6), downstairs[^1]);
        Assert.Contains(downstairs, point => point.Z <= 1f); // round the end of the wall
        Assert.True(downstairs.Count > 2);

        var upstairs = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(2, 20, 6), new Vector3(7, 20, 6), upstairs));
        AssertRoute(new[] { new Vector3(2, 20, 6), new Vector3(7, 20, 6) }, upstairs);
    }

    // The budget is a ceiling on pathological input, not a working allowance, so what it counts has to
    // be the FACES the segment crosses — otherwise a long clear run gets refused on a map with a finer
    // tessellation than the one the number was chosen on.
    //
    // Measured: the segment (0.5, 0.3) -> (7.5, 4.1) over 1 m quads crosses 23 faces, and 23 is exactly
    // where the answer flips. Both sides are asserted, because "it succeeded with a big budget" would
    // pass under an implementation that ignored the budget entirely.
    [Fact]
    public void TheLineWalksBudgetIsTheNumberOfFacesTheSegmentCrosses()
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(8) });
        var from = new Vector3(0.5f, 0, 0.3f);
        var to = new Vector3(7.5f, 0, 4.1f);

        Assert.False(graph.HasClearLine(0, from, to, 0.05f, 22));
        Assert.True(graph.HasClearLine(0, from, to, 0.05f, 23));
        Assert.True(graph.HasClearLine(0, from, to, 0.05f));

        // Zero and negative budgets refuse before looking at anything.
        Assert.False(graph.HasClearLine(0, from, to, 0.05f, 0));
        Assert.False(graph.HasClearLine(0, from, to, 0.05f, -5));

        // Two points in one face still cost one face each side of the shared diagonal.
        var inside = new Vector3(1.2f, 0, 1.2f);
        var acrossTheDiagonal = new Vector3(1.8f, 0, 1.8f);
        Assert.False(graph.HasClearLine(0, inside, acrossTheDiagonal, 0.05f, 1));
        Assert.True(graph.HasClearLine(0, inside, acrossTheDiagonal, 0.05f, 2));
    }
}
