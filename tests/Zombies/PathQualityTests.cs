using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// Two symptoms, reported from play: in houses and tight spaces zombies sometimes stop pressed against a
// wall, and over open ground they take a route that wanders when nothing is in the way.
//
// Both are properties of the route the funnel produces, so both are measurable here without an engine.
// Every threshold below was chosen after measuring the broken behaviour, and each comment records what
// that measurement was — the numbers are the specification, not decoration.
public class PathQualityTests
{
    private const float DoorMinZ = 7f;
    private const float DoorMaxZ = 8f;
    private const float WallMinX = 10f;
    private const float WallMaxX = 11f;

    // A perfectly flat, unobstructed field: quadsPerSide squared quads, two triangles each. `cell` is
    // the quad size, so a fixture can make an opening narrower than a whole metre.
    private static NavFlag FlatField(int quadsPerSide, float cell = 1f)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int side = quadsPerSide + 1;
        for (int x = 0; x < side; x++)
            for (int z = 0; z < side; z++)
                vertices.Add(new Vector3(x * cell, 0f, z * cell));
        for (int x = 0; x < quadsPerSide; x++)
            for (int z = 0; z < quadsPerSide; z++)
            {
                int a = (x * side) + z, b = a + 1, c = a + side, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        return new NavFlag
        {
            Center = new Vector3(quadsPerSide * cell / 2f, 0, quadsPerSide * cell / 2f),
            Size = new Vector3((quadsPerSide * cell) + 2f, 100f, (quadsPerSide * cell) + 2f),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
    }

    // A wall across x in [10, 11] with a doorway ONE quad wide at z in [7, 8] — a house door.
    private static (NavFlag Flag, BakedNavGraph Graph) Doorway(int quads = 20)
    {
        NavFlag flag = FlatField(quads);
        var blocked = new HashSet<int>();
        for (int z = 0; z < quads; z++)
        {
            if (z == (int)DoorMinZ)
                continue;
            int quad = (10 * quads) + z;
            blocked.Add(quad * 2);
            blocked.Add((quad * 2) + 1);
        }
        return (flag, BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = blocked }));
    }

    // Sweep and slide against the wall slab, modelling what the host actually installs: a CastMotion
    // that advances to first contact and projects the REMAINING motion onto that surface, iterated a few
    // times. It never depenetrates — the body stops at the surface — and it never refuses outright, it
    // slides. Both of those matter: a refusing resolver overstates the bug and a depenetrating one
    // hides it, and I got a different answer from each before writing this one.
    private static ZombieMoveResolver WallWithDoorway() => (from, to, radius) =>
    {
        (float MinX, float MaxX, float MinZ, float MaxZ)[] boxes =
        {
            (WallMinX, WallMaxX, -100f, DoorMinZ),
            (WallMinX, WallMaxX, DoorMaxZ, 100f),
        };

        static bool Overlaps(Vector3 p, float radius,
            (float MinX, float MaxX, float MinZ, float MaxZ) box)
        {
            float cx = Math.Clamp(p.X, box.MinX, box.MaxX);
            float cz = Math.Clamp(p.Z, box.MinZ, box.MaxZ);
            float dx = p.X - cx, dz = p.Z - cz;
            return (dx * dx) + (dz * dz) < radius * radius;
        }

        bool Blocked(Vector3 p)
        {
            foreach (var box in boxes)
                if (Overlaps(p, radius, box))
                    return true;
            return false;
        }

        // Fine substeps: each one either lands free, or is taken on whichever single axis stays free —
        // which is the projection of the remaining motion onto the contact plane for an axis-aligned
        // wall. Anything that would enter the surface is simply not taken.
        const int Substeps = 16;
        Vector3 at = from;
        Vector3 step = (to - from) / Substeps;
        for (int i = 0; i < Substeps; i++)
        {
            var full = new Vector3(at.X + step.X, to.Y, at.Z + step.Z);
            if (!Blocked(full))
            {
                at = full;
                continue;
            }
            var alongZ = new Vector3(at.X, to.Y, at.Z + step.Z);
            if (!Blocked(alongZ))
            {
                at = alongZ;
                continue;
            }
            var alongX = new Vector3(at.X + step.X, to.Y, at.Z);
            if (!Blocked(alongX))
                at = alongX;
        }
        return at;
    };

    private static float Length(List<Vector3> path)
    {
        float total = 0f;
        for (int i = 1; i < path.Count; i++)
            total += path[i - 1].DistanceTo(path[i]);
        return total;
    }

    // SYMPTOM: "over open ground they trace a path that goes around when there is no obstacle."
    //
    // The funnel returns the shortest route INSIDE the corridor A* handed it, and that corridor is
    // chosen on centre-to-centre cost, which over a tessellated floor is essentially Manhattan: "north
    // then east" ties with the diagonal and the tie is arbitrary. So the wander was never in the string
    // pulling, it was in the corridor, and no amount of funnel work could reach it.
    //
    // Measured on flat, EMPTY ground before the shortcut pass, with nothing whatsoever in the way:
    //
    //     along X    1.001x over  4 waypoints
    //     along Z    1.001x over  4 waypoints
    //     shallow    1.040x over  6 waypoints
    //     diagonal   1.174x over 14 waypoints   <- leaves the start heading nearly due north
    //
    // A straight line between two points on an empty field is two waypoints and 1.000x, and that is
    // now what every one of them is.
    [Theory]
    [InlineData(2f, 16f, 30f, 16f)]   // straight along X
    [InlineData(16f, 2f, 16f, 30f)]   // straight along Z
    [InlineData(2f, 2f, 30f, 5f)]     // a shallow angle
    [InlineData(2f, 2f, 30f, 30f)]    // the diagonal
    public void OpenGround_IsWalkedStraight(float fromX, float fromZ, float toX, float toZ)
    {
        BakedNavGraph graph = BakedNavGraph.Build(new[] { FlatField(32) });
        var from = new Vector3(fromX, 0, fromZ);
        var to = new Vector3(toX, 0, toZ);
        var path = new List<Vector3>();

        Assert.True(graph.TryPath(from, to, path));

        float ratio = Length(path) / from.DistanceTo(to);
        Assert.True(ratio <= 1.001f, $"walked {ratio:0.000}x the direct distance over empty ground");
        Assert.Equal(new[] { from, to }, path);
    }

    // Shortening a route must never be allowed to shorten it THROUGH something. A body walks the
    // segments, not the waypoints, so this samples the whole polyline rather than its corners: nothing
    // anywhere along it may enter the wall, or even come nearer to it than the body's own radius.
    //
    // This is the test that caught the walk's real defect. A first version decided the exit edge by
    // "the first crossing strictly ahead of where we entered", which skips a crossing at t = 0 — and a
    // segment starting exactly on a face boundary, which is where every funnel corner sits, has its
    // only exit at t = 0. The walk found no crossing, concluded the segment ended inside the face and
    // reported it clear, and the doorway route collapsed to a straight line through the wall.
    [Fact]
    public void NoPartOfARoute_EntersOrGrazesAWall()
    {
        (NavFlag _, BakedNavGraph graph) = Doorway();
        (Vector3 From, Vector3 To)[] queries =
        {
            (new Vector3(4, 0, 2), new Vector3(16, 0, 15)),
            (new Vector3(5, 0, 6), new Vector3(13, 0, 9)),
            (new Vector3(2, 0, 18), new Vector3(18, 0, 2)),
            (new Vector3(15, 0, 3), new Vector3(3, 0, 14)),
            (new Vector3(9, 0, 7.5f), new Vector3(12, 0, 7.5f)),
        };

        // That last one lies down the middle of the doorway with nothing in the way, so the whole route
        // is a single shortcut segment. What the pass ITSELF emits is held to the full width.
        var straight = new List<Vector3>();
        Assert.True(graph.TryPath(queries[^1].From, queries[^1].To, straight));
        Assert.Equal(2, straight.Count);
        for (int step = 0; step <= 64; step++)
        {
            Vector3 point = straight[0].Lerp(straight[1], step / 64f);
            Assert.True(MathF.Min(DistanceToWall(point, -100f, DoorMinZ),
                DistanceToWall(point, DoorMaxZ, 100f)) >= BakedNavGraph.AgentRadius,
                "a segment this pass created must keep the body's full width off the wall");
        }

        float worst = float.MaxValue;
        Vector3 worstAt = Vector3.Zero;
        foreach ((Vector3 from, Vector3 to) in queries)
        {
            var path = new List<Vector3>();
            Assert.True(graph.TryPath(from, to, path));
            for (int i = 1; i < path.Count; i++)
                for (int step = 0; step <= 64; step++)
                {
                    Vector3 point = path[i - 1].Lerp(path[i], step / 64f);
                    float clearance = MathF.Min(
                        DistanceToWall(point, -100f, DoorMinZ),
                        DistanceToWall(point, DoorMaxZ, 100f));
                    if (clearance < worst)
                    {
                        worst = clearance;
                        worstAt = point;
                    }
                }
        }

        Assert.True(worst > 0f,
            $"the route enters a wall at ({worstAt.X:0.00}, {worstAt.Z:0.00})");

        // The residual, which this pass neither causes nor cures — measured at 0.318 m either way, at
        // the same point, (9.78, 7.78). It is the known limit of insetting a portal end ALONG its own
        // portal: that buys full clearance AT the corner but not on the segment ARRIVING at it, which
        // can still cut across the jamb behind it. Closing it means offsetting against the wall edges
        // incident to the vertex, which a per-vertex flag cannot express. Pinned rather than described
        // so that the change which does close it has a number to move.
        Assert.InRange(worst, 0.30f, 0.33f);
    }

    // Distance in XZ from a point to one of the two wall slabs; zero when the point is inside it.
    private static float DistanceToWall(Vector3 point, float minZ, float maxZ)
    {
        float dx = MathF.Max(MathF.Max(WallMinX - point.X, point.X - WallMaxX), 0f);
        float dz = MathF.Max(MathF.Max(minZ - point.Z, point.Z - maxZ), 0f);
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    // The walk itself, which the routes above only exercise where a shortcut happened to be tried.
    // A wall stops it, the body's width stops it short of a wall it would otherwise slip past, and an
    // exhausted budget stops it regardless.
    [Fact]
    public void TheLineWalk_RefusesWalls_ClosePasses_AndAnExhaustedBudget()
    {
        (NavFlag _, BakedNavGraph graph) = Doorway();

        Assert.True(graph.HasClearLine(0, new Vector3(2, 0, 2), new Vector3(9, 0, 9)));
        Assert.True(graph.HasClearLine(0, new Vector3(9, 0, 7.5f), new Vector3(12, 0, 7.5f)),
            "straight down the middle of a 1 m doorway must be walkable by a 0.4 m body");

        Assert.False(graph.HasClearLine(0, new Vector3(9, 0, 8.5f), new Vector3(12, 0, 8.5f)),
            "a line through the wall itself must be refused");
        // Geometrically inside the opening, but 0.1 m from the jamb — a 0.4 m body does not fit there.
        Assert.False(graph.HasClearLine(0, new Vector3(9, 0, 7.9f), new Vector3(12, 0, 7.9f)),
            "a line that shaves a jamb must be refused");
        // Same clear line as above, with no budget to walk it.
        Assert.False(graph.HasClearLine(0, new Vector3(2, 0, 2), new Vector3(9, 0, 9), budget: 3));
    }

    // A building whose upper floor sits over its ground floor, joined only by a ramp at the far end.
    // The two levels overlap in XZ from x = 3 to x = 10, which is the case that breaks an XZ-only walk.
    //
    //   Y=3          +--------------------+   upper floor, x in [3, 13]
    //                                    /
    //   Y=0  +--------------------------+     ground floor x in [0, 10], ramp x in [10, 13]
    private static BakedNavGraph TwoStoreys()
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        const int Rows = 5; // z = 0..4

        int Column(float x, float y)
        {
            int first = vertices.Count;
            for (int z = 0; z < Rows; z++)
                vertices.Add(new Vector3(x, y, z));
            return first;
        }

        void Quads(int left, int right)
        {
            for (int z = 0; z < Rows - 1; z++)
            {
                int a = left + z, b = a + 1, c = right + z, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        }

        // Ground floor out to x = 10, then a ramp climbing to Y = 3 at x = 13.
        int previous = Column(0f, 0f);
        for (int x = 1; x <= 13; x++)
        {
            int current = Column(x, MathF.Max(0f, x - 10f));
            Quads(previous, current);
            previous = current;
        }
        // The upper floor folds back over the ground floor, joined ONLY at the ramp's top column, whose
        // vertex indices it reuses — the graph welds on shared indices, so this is the single doorway
        // between the two levels.
        for (int x = 12; x >= 3; x--)
        {
            int current = Column(x, 3f);
            Quads(previous, current);
            previous = current;
        }

        var flag = new NavFlag
        {
            Center = new Vector3(6.5f, 0, 2),
            Size = new Vector3(40, 200, 40),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
        return BakedNavGraph.Build(new[] { flag });
    }

    // The walk is in XZ, so "the segment ends inside this face" says nothing about which STOREY that
    // face is. Downstairs at (1, 0, 2), upstairs at (4, 3, 2): the straight XZ line between them lies
    // entirely over the ground floor, so a walk that never left it arrives under the upstairs waypoint
    // and would report the segment clear. Taking that shortcut replaces the whole trip up the ramp with
    // a 3 m vertical jump through the ceiling — and nothing downstream would catch it, since
    // CalculateVelocity discards Y and the body ground-snaps, so the zombie just stays downstairs.
    [Fact]
    public void AShortcut_DoesNotJumpBetweenStoreysThatOverlapInPlan()
    {
        BakedNavGraph graph = TwoStoreys();
        var downstairs = new Vector3(1, 0, 2);
        var upstairs = new Vector3(4, 3, 2);

        Assert.False(graph.HasClearLine(0, downstairs, upstairs),
            "the ground floor does not carry an upstairs waypoint just because it is under it");

        var path = new List<Vector3>();
        Assert.True(graph.TryPath(downstairs, upstairs, path));

        // The route has to go the long way: out to the ramp and back. A straight line is 3 m.
        Assert.True(Length(path) > 18f,
            $"the route only walked {Length(path):0.0} m, so it took the shortcut through the ceiling");
        // It went out to the ramp. Collapsing the flat run AND the ramp into one leg is fine and does
        // happen — the surface is continuous, and the body ground-snaps along it — so what is asserted
        // is that the route passes through the only place the two levels actually join.
        Assert.Contains(path, point => point.X >= 12f);
    }

    // A wide floor with a THIN strip welded along its far edge, so the strip's outer boundary is a wall
    // 0.1 m beyond a shared edge that is not one. Baked tiles produce slivers like this along walls.
    private static BakedNavGraph FloorWithASliverAlongTheWall(float sliver)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        float[] rows = { 0f, 20f, 20f + sliver };

        for (int x = 0; x <= 20; x++)
            foreach (float z in rows)
                vertices.Add(new Vector3(x, 0f, z));
        for (int x = 0; x < 20; x++)
            for (int r = 0; r < rows.Length - 1; r++)
            {
                int a = (x * rows.Length) + r, b = a + 1, c = a + rows.Length, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }

        var flag = new NavFlag
        {
            Center = new Vector3(10, 0, 10),
            Size = new Vector3(60, 200, 60),
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
        };
        return BakedNavGraph.Build(new[] { flag });
    }

    // A wall does not have to belong to a face the line walks through. Here the line stays inside the
    // wide floor the whole way, so every face it enters has no border edge at all and no border vertex
    // — the shared edge at z = 20 is interior, and the vertices ON it are interior too, because the
    // sliver beyond carries the actual boundary at z = 20.1.
    //
    // Checking only the faces walked through therefore accepts a line 0.2 m from a wall for a body that
    // needs 0.45. The clearance test reaches one face further out for exactly this.
    [Fact]
    public void TheLineWalk_SeesWallsOnFacesItDoesNotEnter()
    {
        BakedNavGraph graph = FloorWithASliverAlongTheWall(0.1f);

        Assert.True(graph.HasClearLine(0, new Vector3(2, 0, 10), new Vector3(18, 0, 10)),
            "a line down the middle of a 20 m floor is clear");
        Assert.False(graph.HasClearLine(0, new Vector3(2, 0, 19.9f), new Vector3(18, 0, 19.9f)),
            "0.2 m from the sliver's outer wall is not clearance for a 0.4 m body");
    }

    // An edge can carry more than two faces — the builder connects every pair that shares one. Taking
    // the first CSR match to cross by can pick a face on the side the walk is already on: the next
    // iteration finds the same exit at the same parameter and steps back, and the two trade the walk
    // between them until the budget is gone. An otherwise clear route that crosses one such edge then
    // loses ALL of its shortcutting, silently, because the pass just gives up.
    //
    // The duplicate is inserted directly AFTER the face it copies and BEFORE that face's true
    // neighbour. Order is what decides this: appended at the end of the mesh it sorts last in the
    // adjacency and first-match happens to pick correctly, which is a coincidence and not a guarantee.
    // Here first-match picks the duplicate from the original and the original from the duplicate.
    //
    // The duplicate changes nothing about the walkable region, which is what makes it a clean probe:
    // the same line over the same floor, and only the budget can tell the difference.
    [Fact]
    public void TheLineWalk_CrossesANonManifoldEdgeInsteadOfBouncingOnIt()
    {
        NavFlag field = FlatField(32);
        const int Side = 33;
        int original = (((16 * 32) + 16) * 2) * 3; // quad (16, 16)'s first face, in index positions
        int a = (16 * Side) + 16;

        var triangles = new List<int>(field.Triangles);
        triangles.InsertRange(original + 3, new[] { a, a + 1, a + Side });
        var flag = new NavFlag
        {
            Center = field.Center,
            Size = field.Size,
            Vertices = field.Vertices,
            Triangles = triangles.ToArray(),
        };
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });

        // 28 m over 1 m quads is a little over 60 faces; 200 is ample unless the walk is going nowhere.
        Assert.True(graph.HasClearLine(0, new Vector3(2, 0, 16.3f), new Vector3(30, 0, 16.3f), budget: 200),
            "the walk spent its budget without crossing the floor");
    }

    // A duplicated face shares its edges from the SAME side. Where one of those edges is a real outer
    // boundary, counting the duplicate as "floor across it" turns the wall into an interior edge: it
    // stops being tested for clearance, and its vertices stop being borders, so nothing at all guards
    // the edge of the navmesh. A route can then run half a metre inside it with the capsule hanging off.
    //
    // The duplicate here changes nothing about where a body may stand — same floor, same boundary.
    [Fact]
    public void TheLineWalk_IsNotBlindedToABoundaryByADuplicateFaceBesideIt()
    {
        NavFlag field = FlatField(20);
        const int Side = 21;
        var triangles = new List<int>(field.Triangles);
        // The far row's second face carries the boundary edge (x, 20) - (x + 1, 20). Duplicate each.
        for (int x = 0; x < 20; x++)
        {
            int a = (x * Side) + 19, b = a + 1, c = a + Side, d = c + 1;
            triangles.AddRange(new[] { b, d, c });
        }
        var flag = new NavFlag
        {
            Center = field.Center,
            Size = field.Size,
            Vertices = field.Vertices,
            Triangles = triangles.ToArray(),
        };
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag });

        Assert.True(graph.HasClearLine(0, new Vector3(2, 0, 10), new Vector3(18, 0, 10)),
            "a line down the middle of the floor is clear");
        Assert.False(graph.HasClearLine(0, new Vector3(2, 0, 19.8f), new Vector3(18, 0, 19.8f)),
            "0.2 m inside the edge of the navmesh is not clearance for a 0.4 m body");
    }

    // A flag with no faces snaps to no triangle, and the walk has to answer that rather than index it.
    [Fact]
    public void AFlagWithNoFaces_HasNoClearLines()
    {
        var empty = new NavFlag { Center = Vector3.Zero, Size = new Vector3(64, 64, 64) };
        BakedNavGraph graph = BakedNavGraph.Build(new[] { empty });
        Assert.False(graph.HasClearLine(0, Vector3.Zero, new Vector3(4, 0, 4)));
    }

    // A route long and hemmed-in enough to exhaust the shortcut pass's walking budget must come back
    // whole. Giving up partway is the designed behaviour — the pass is an improvement on the funnel's
    // answer, never a precondition for it — so what has to hold is that the tail it did not reach is
    // still there, still starts where it was asked from, and still ends at the destination.
    [Fact]
    public void ARouteTooLongToSmooth_IsReturnedIntact()
    {
        const int Quads = 96;
        NavFlag flag = FlatField(Quads);
        // Walls every 6 m with staggered gaps: a switchback corridor with no long sightlines.
        var blocked = new HashSet<int>();
        for (int x = 6; x < Quads; x += 6)
            for (int z = 0; z < Quads; z++)
            {
                if (z == (x / 6 % 2 == 0 ? 1 : Quads - 2))
                    continue;
                blocked.Add((((x * Quads) + z) * 2) + 0);
                blocked.Add((((x * Quads) + z) * 2) + 1);
            }
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = blocked });

        var from = new Vector3(2, 0, 48);
        var to = new Vector3(94, 0, 48);
        var path = new List<Vector3>();
        Assert.True(graph.TryPath(from, to, path));

        Assert.Equal(from, path[0]);
        Assert.Equal(to, path[^1]);
        Assert.True(path.Count > 32, $"expected a long switchback route, got {path.Count} waypoints");
        // Whatever the pass did or did not reach, the route stays a walk: no repeated points, and every
        // leg short enough to belong to this corridor rather than to a jump across it.
        for (int i = 1; i < path.Count; i++)
            Assert.InRange(path[i - 1].DistanceTo(path[i]), 1e-4f, 96f);
    }

    // SYMPTOM: "in houses and tight spaces they sometimes stop pressed against the walls."
    //
    // The geometric half. A portal endpoint is a mesh VERTEX, and at a doorway that vertex is the jamb,
    // so string-pulling to it aims a body with width at a point ON the wall. Measured before the fix,
    // with an angled approach: a waypoint landed exactly on a jamb — clearance 0.000 against a 0.40 m
    // capsule.
    [Fact]
    public void ADoorwayRoute_KeepsTheBodysWidthOffTheJambs()
    {
        (NavFlag _, BakedNavGraph graph) = Doorway();
        var path = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 2), new Vector3(16, 0, 15), path));

        var jambs = new[]
        {
            new Vector3(WallMinX, 0, DoorMinZ), new Vector3(WallMaxX, 0, DoorMinZ),
            new Vector3(WallMinX, 0, DoorMaxZ), new Vector3(WallMaxX, 0, DoorMaxZ),
        };

        foreach (Vector3 point in path)
            foreach (Vector3 jamb in jambs)
            {
                float clearance = new Vector2(point.X - jamb.X, point.Z - jamb.Z).Length();
                Assert.True(clearance > 0.3f,
                    $"waypoint ({point.X:0.00}, {point.Z:0.00}) sits {clearance:0.000} m from a jamb");
            }
    }

    // And the behavioural half, which is the symptom itself: the same doorway, a real wall in the
    // MoveResolver seam, and a zombie hunting a player on the far side.
    //
    // Before the fix it ended at (10.17, 7.59) — inside the wall slab — and never got through in 20 s.
    // A 1 m opening leaves a 0.4 m capsule only a 0.2 m band for its centre, and the route aimed it
    // 0.4 m outside that band, at the jamb itself.
    [Fact]
    public void AZombieHuntsThroughAHouseDoorway_InsteadOfStallingOnTheJamb()
    {
        (NavFlag flag, BakedNavGraph graph) = Doorway();
        var bounds = new List<NavBound>
        {
            new NavBound { Center = new Vector3(10, 50, 10), Size = new Vector3(60, 200, 60) },
        };
        var system = new ZombieSystem(new[] { new ZombieTable { Name = "C", Health = 100, Damage = 10 } },
            bounds, (float x, float z, out float y) => { y = 0f; return true; }, new[] { flag });
        // Spawnpoints carry UNITY coordinates, so Z is pre-flipped to land on (5, 6) in Godot space.
        system.Spawn(new[] { new ZombieSpawnpointData(0, new Vector3(5, 0, -6)) }, new Random(1));
        ZombieInstance zombie = Assert.Single(system.Zombies);
        zombie.Speciality = EZombieSpeciality.Normal;
        zombie.Position = new Vector3(5, 0, 6);

        system.PathQuery = (Vector3 a, Vector3 b, List<Vector3> p) => graph.TryPath(a, b, p);
        system.PathReady = () => true;
        system.MoveResolver = WallWithDoorway();

        var player = new[]
        {
            new ZombiePlayerView(1, new Vector3(13, 0, 9), UnturnedGodot.Player.EPlayerStance.Stand, false),
        };

        bool through = false;
        for (int tick = 0; tick < 250 && !through; tick++) // 20 s at the server tick
        {
            system.Tick(player, 0.08f);
            through = zombie.Position.X > WallMaxX + 0.5f;
        }

        Assert.True(through, "the zombie never cleared the doorway; it ended at "
            + $"({zombie.Position.X:0.00}, {zombie.Position.Z:0.00})");
    }

    // The clamp path, which the previous version of this test did not reach: Inset only clamps when a
    // portal is narrower than 2 * (AgentRadius + Clearance) = 0.9 m, and a 1 m door never gets there. A
    // half-metre grid does, so this is the branch that would invert a portal if it regressed.
    //
    // The query is deliberately OFF the door's midline at both ends, so the assertion depends on where
    // the portal actually put the route rather than on the straight line already lying down the middle.
    [Fact]
    public void AGapNarrowerThanTheBody_CollapsesToItsMiddleInsteadOfInverting()
    {
        const float Cell = 0.5f;
        const int Quads = 40;
        const int WallColumn = 20;   // wall spans x in [10.0, 10.5]
        const int DoorRow = 14;      // opening spans z in [7.0, 7.5] — 0.5 m, under the 0.9 m clamp

        NavFlag flag = FlatField(Quads, Cell);
        var blocked = new HashSet<int>();
        for (int z = 0; z < Quads; z++)
        {
            if (z == DoorRow)
                continue;
            int quad = (WallColumn * Quads) + z;
            blocked.Add(quad * 2);
            blocked.Add((quad * 2) + 1);
        }
        BakedNavGraph graph = BakedNavGraph.Build(new[] { flag },
            new Dictionary<NavFlag, HashSet<int>> { [flag] = blocked });

        float doorMinZ = DoorRow * Cell, doorMaxZ = (DoorRow + 1) * Cell;
        float wallMinX = WallColumn * Cell, wallMaxX = (WallColumn + 1) * Cell;

        var path = new List<Vector3>();
        Assert.True(graph.TryPath(new Vector3(4, 0, 4), new Vector3(16, 0, 11), path),
            "a gap narrower than the body must still be routed through, not refused");

        // Nothing may land outside the opening, and nothing may land outside the portal it came from:
        // an inverted portal shows up as a waypoint beyond a jamb.
        foreach (Vector3 point in path)
            if (point.X >= wallMinX && point.X <= wallMaxX)
                Assert.InRange(point.Z, doorMinZ, doorMaxZ);

        // And the clamp really did engage. Interpolating where the route crosses the middle of the wall
        // band rather than looking for a waypoint inside it: the band is 0.5 m wide and the funnel has no
        // reason to place a vertex inside it.
        float mid = (wallMinX + wallMaxX) * 0.5f;
        float? crossingZ = null;
        for (int i = 1; i < path.Count && crossingZ == null; i++)
        {
            Vector3 a = path[i - 1], b = path[i];
            if ((a.X - mid) * (b.X - mid) > 0f)
                continue;
            float t = MathF.Abs(b.X - a.X) < 1e-6f ? 0f : (mid - a.X) / (b.X - a.X);
            crossingZ = a.Z + ((b.Z - a.Z) * t);
        }

        Assert.True(crossingZ.HasValue, "the route never crossed the wall");
        // With both ends inset by 0.45 the 0.5 m portal would have inverted; clamped, it collapses to the
        // opening's middle, which is the only place a body can aim.
        Assert.Equal((doorMinZ + doorMaxZ) * 0.5f, crossingZ!.Value, 2);
    }
}
