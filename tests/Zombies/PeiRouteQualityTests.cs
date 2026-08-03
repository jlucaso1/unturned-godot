using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests.Zombies;

// Route quality measured against the map the game actually ships, rather than against a fixture built
// to make a point. The synthetic suites pin the funnel's rules one at a time; this pins what those
// rules add up to over 42k real triangles, where walls meet portals at every angle instead of the few
// a hand-built grid can express.
[Trait("Category", "RealData")]
public class PeiRouteQualityTests
{
    // Where the body's footprint may sit. A recast mesh is baked already inset from the colliders, so
    // a point ON the mesh is a point the body can stand — and the funnel's whole contract is that the
    // route stays far enough inside that the 0.40 m capsule never has to leave it.
    private sealed class Coverage
    {
        private const float Cell = 4f;
        private readonly List<(Vector3 A, Vector3 B, Vector3 C)> _tris = new();
        private readonly Dictionary<(int, int), List<int>> _grid = new();

        public Coverage(IReadOnlyList<NavFlag> flags)
        {
            foreach (NavFlag f in flags)
                for (int i = 0; i + 2 < f.Triangles.Length; i += 3)
                {
                    Vector3 a = f.Vertices[f.Triangles[i]];
                    Vector3 b = f.Vertices[f.Triangles[i + 1]];
                    Vector3 c = f.Vertices[f.Triangles[i + 2]];
                    if (MathF.Abs(((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z))) < 1e-6f)
                        continue; // degenerate: "contains" everything under the sign rule
                    int index = _tris.Count;
                    _tris.Add((a, b, c));
                    int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / Cell);
                    int x1 = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / Cell);
                    int z0 = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / Cell);
                    int z1 = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / Cell);
                    for (int x = x0; x <= x1; x++)
                        for (int z = z0; z <= z1; z++)
                        {
                            if (!_grid.TryGetValue((x, z), out List<int>? bucket))
                                _grid[(x, z)] = bucket = new List<int>();
                            bucket.Add(index);
                        }
                }
        }

        public int TriangleCount => _tris.Count;

        public bool Covers(Vector3 p, float tolerance = 2.5f)
        {
            var key = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Z / Cell));
            if (!_grid.TryGetValue(key, out List<int>? bucket))
                return false;
            foreach (int i in bucket)
            {
                (Vector3 a, Vector3 b, Vector3 c) = _tris[i];
                float d1 = ((p.X - b.X) * (a.Z - b.Z)) - ((a.X - b.X) * (p.Z - b.Z));
                float d2 = ((p.X - c.X) * (b.Z - c.Z)) - ((b.X - c.X) * (p.Z - c.Z));
                float d3 = ((p.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (p.Z - a.Z));
                if ((d1 < 0f || d2 < 0f || d3 < 0f) && (d1 > 0f || d2 > 0f || d3 > 0f))
                    continue;
                float total = d1 + d2 + d3;
                if (MathF.Abs(total) < 1e-9f)
                    continue;
                // Stacked floors: only the surface at this height counts as cover for this point.
                if (MathF.Abs((((d2 * a.Y) + (d3 * b.Y) + (d1 * c.Y)) / total) - p.Y) <= tolerance)
                    return true;
            }
            return false;
        }

        public bool Fits(Vector3 p, float radius)
        {
            if (!Covers(p))
                return false;
            for (int i = 0; i < 8; i++)
            {
                float angle = i * MathF.Tau / 8f;
                if (!Covers(new Vector3(p.X + (MathF.Cos(angle) * radius), p.Y,
                        p.Z + (MathF.Sin(angle) * radius))))
                    return false;
            }
            return true;
        }
    }

    private const int Samples = 60;

    // Both ends of a sampled pair are mesh VERTICES, which sit on the boundary by construction, so the
    // footprint legitimately overhangs there. Only the route's interior is a claim the funnel makes.
    private const float EndpointSkirt = 1.5f;

    // What the funnel currently achieves on PEI. It is a CEILING, not a target: this asserts the
    // number never climbs back, and it is meant to come down as the funnel improves. Before portal
    // budget was shared without starving either wall end it was 26 — the proportional split handed one
    // real wall as little as 0.04 m of the inset it asked for, leaving route points 0.01 m from a jamb.
    //
    // Re-measured both ways when a review argued for restoring the proportional split, on the grounds
    // that it equalises PERPENDICULAR clearance while an equal along-edge share does not. The algebra
    // is right — InsetFor asks for wanted/sin, so scaling both requests by budget/total lands every end
    // on wanted*budget/total — but it optimises the wrong thing. Equalising drags the end that CAN be
    // cleared down to the level of one that cannot: a near-parallel wall asks for metres of a portal
    // that is one metre long, and no allocation ever satisfies it. Fully clearing the achievable end is
    // worth more than raising an unachievable one, and the map agrees: 22 routes off the mesh here
    // against 26 proportional, with the tightest fit 0.05 m under BOTH — so the clearance the algebra
    // promises the shallow end does not survive contact with the portals that actually bind.
    //
    // Down to 21 with T-junctions joined. It briefly read 26 when the clearance work landed alongside
    // this test, and that number was measured against a graph carrying 1784 edges it called walls where
    // the mesh is continuous — phantom walls pull portal insets in, so anything measuring distance to
    // the nearest wall was partly measuring against them. Joining those faces did not merely undo the
    // 26: it is a route better than the 22 this ceiling was set at before any of the clearance work.
    private const int WorstRoutesLeavingTheMesh = 21;

    private static (List<NavFlag> Flags, Coverage Mesh, BakedNavGraph Graph) Load()
    {
        string dir = System.IO.Path.Combine(GameData.Map("PEI")!, "Environment");
        List<NavFlag> flags = LevelNavmesh.Load(dir);
        return (flags, new Coverage(flags), BakedNavGraph.Build(flags));
    }

    // Deterministic: fixed seed, fixed flag, fixed acceptance rule. The same 60 pairs every run.
    private static List<(Vector3 From, Vector3 To)> SamplePairs(List<NavFlag> flags, Coverage mesh)
    {
        NavFlag biggest = flags[0];
        foreach (NavFlag f in flags)
            if (f.Triangles.Length > biggest.Triangles.Length)
                biggest = f;
        var rng = new Random(20260803);
        var pairs = new List<(Vector3, Vector3)>();
        int attempts = 0;
        while (pairs.Count < Samples && attempts++ < 200_000)
        {
            Vector3 a = biggest.Vertices[biggest.Triangles[rng.Next(biggest.Triangles.Length / 3) * 3]];
            if (!mesh.Fits(a, BakedNavGraph.AgentRadius))
                continue;
            Vector3 b = biggest.Vertices[biggest.Triangles[rng.Next(biggest.Triangles.Length / 3) * 3]];
            float d = new Vector2(b.X - a.X, b.Z - a.Z).Length();
            if (d < 8f || d > 20f || !mesh.Fits(b, BakedNavGraph.AgentRadius))
                continue;
            pairs.Add((a, b));
        }
        return pairs;
    }

    // Reported from play: a gap between two houses on ground that is solid and walkable, which zombies
    // would not cross — they went round. It was not clearance. The mesh is continuous across it (every
    // sample lands on the mesh with no snap displacement) and a body one CENTIMETRE wide took the same
    // detour as a full-size one, which is what says adjacency rather than width.
    //
    // The cause was a T-junction: the faces meet along a line, but one side spans it with a single edge
    // while the other has a vertex partway along, so no pair of vertex indices matched and both sides
    // were classified as walls. Measured at 1784 of 37396 border edges across PEI, in every flag.
    //
    // Numbers before the join was stitched, at this exact point:
    //
    //     1 m of plan south-west  ->  163.05 m of route      (x163)
    //    14 m of plan west        ->  150.15 m of route      (x10.7)
    //
    // The ceilings here are deliberately loose. This pins that the site is CONNECTED, not the exact
    // geometry of the way through, which insets and corner handling may legitimately move.
    [RealDataFact(Map = "PEI")]
    public void GroundBetweenTwoHouses_IsNotSeveredByASeam()
    {
        (List<NavFlag> flags, Coverage _, BakedNavGraph graph) = Load();
        var reported = new Vector3(-612.59f, 35.05f, -148.55f); // y is standing height; mesh is at 33.35
        Assert.True(LevelNavmesh.SnapXZ(flags, reported, out Vector3 centre),
            "the reported point is not on the navmesh");

        foreach (float degrees in new[] { 200f, 220f, 240f })
        {
            float radians = degrees * MathF.PI / 180f;
            var direction = new Vector3(MathF.Cos(radians), 0f, MathF.Sin(radians));
            Assert.True(LevelNavmesh.SnapXZ(flags, centre + (direction * 1f), out Vector3 near));

            var route = new List<Vector3>();
            Assert.True(graph.TryPath(centre, near, route, BakedNavGraph.AgentRadius) && route.Count >= 2,
                $"no route one metre out at {degrees:F0} deg");
            float length = 0f;
            for (int i = 0; i + 1 < route.Count; i++)
                length += new Vector2(route[i + 1].X - route[i].X, route[i + 1].Z - route[i].Z).Length();
            // Rounding a corner at the body's width costs a few metres; going round the block cost 163.
            Assert.True(length < 15f,
                $"one metre out at {degrees:F0} deg took {length:F2} m of route — the seam is back");
        }

        // And the open run west of it, which was 150 m for 14 m of plan, is direct.
        Assert.True(LevelNavmesh.SnapXZ(flags,
            centre + new Vector3(MathF.Cos(200f * MathF.PI / 180f) * 14f, 0f,
                MathF.Sin(200f * MathF.PI / 180f) * 14f), out Vector3 far));
        var open = new List<Vector3>();
        Assert.True(graph.TryPath(centre, far, open, BakedNavGraph.AgentRadius) && open.Count >= 2);
        float openLength = 0f;
        for (int i = 0; i + 1 < open.Count; i++)
            openLength += new Vector2(open[i + 1].X - open[i].X, open[i + 1].Z - open[i].Z).Length();
        float plan = new Vector2(far.X - centre.X, far.Z - centre.Z).Length();
        Assert.True(openLength < plan * 1.5f,
            $"14 m of open ground took {openLength:F2} m of route against {plan:F2} m of plan");
    }

    [RealDataFact(Map = "PEI")]
    public void RealRoutes_KeepTheBodyOnTheMesh()
    {
        (List<NavFlag> flags, Coverage mesh, BakedNavGraph graph) = Load();
        Assert.True(mesh.TriangleCount > 10_000, $"PEI navmesh looks wrong: {mesh.TriangleCount} triangles");
        List<(Vector3 From, Vector3 To)> pairs = SamplePairs(flags, mesh);
        Assert.Equal(Samples, pairs.Count); // the sampler itself must not drift

        int routed = 0, leaving = 0;
        float tightest = BakedNavGraph.AgentRadius;
        var worst = new List<string>();
        foreach ((Vector3 from, Vector3 to) in pairs)
        {
            var route = new List<Vector3>();
            if (!graph.TryPath(from, to, route, BakedNavGraph.AgentRadius) || route.Count < 2)
                continue;
            routed++;
            bool leaves = false;
            for (int i = 0; i + 1 < route.Count; i++)
            {
                float length = new Vector2(route[i + 1].X - route[i].X, route[i + 1].Z - route[i].Z).Length();
                int steps = Math.Max(1, (int)MathF.Ceiling(length / 0.25f));
                for (int s = 0; s <= steps; s++)
                {
                    Vector3 p = route[i].Lerp(route[i + 1], s / (float)steps);
                    if (new Vector2(p.X - from.X, p.Z - from.Z).Length() < EndpointSkirt
                        || new Vector2(p.X - to.X, p.Z - to.Z).Length() < EndpointSkirt)
                        continue;
                    if (mesh.Fits(p, BakedNavGraph.AgentRadius))
                        continue;
                    leaves = true;
                    for (float r = BakedNavGraph.AgentRadius; r > 0f; r -= 0.05f)
                        if (mesh.Fits(p, r))
                        {
                            tightest = MathF.Min(tightest, r);
                            break;
                        }
                }
            }
            if (leaves)
            {
                leaving++;
                if (worst.Count < 5)
                    worst.Add($"{from} -> {to}");
            }
        }

        Assert.Equal(Samples, routed); // every sampled pair is reachable; a drop here is its own bug
        Assert.True(leaving <= WorstRoutesLeavingTheMesh,
            $"{leaving} of {routed} PEI routes put the 0.40 m body outside the navmesh "
            + $"(ceiling {WorstRoutesLeavingTheMesh}, tightest fit {tightest:F2} m):\n  "
            + string.Join("\n  ", worst));
    }
}
