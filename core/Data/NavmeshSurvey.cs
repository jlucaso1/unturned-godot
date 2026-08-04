using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// One connected region of a flag's walkable surface: the faces a body can reach from any one of them
// without leaving the mesh. `Anchor` is a point ON the surface (the face centre nearest the region's
// centroid), not the centroid itself — an L-shaped region's centroid falls in the notch, which is the
// one place a marker must not go if the point of the marker is "fly here and look".
public readonly record struct NavIsland(int TriangleCount, double Area, Vector3 Anchor,
    Vector3 Min, Vector3 Max);

// An edge with no floor across it: the outer boundary of the mesh, a wall, or the rim of a hole. The
// three are the same thing to the router — a line it will not step over — and telling them apart is
// what the eye is for, which is why this carries geometry rather than a classification.
public readonly record struct NavRimEdge(Vector3 A, Vector3 B);

// What the router sees on one navigation flag.
public sealed class NavFlagSurvey
{
    public required NavFlag Flag { get; init; }

    // Which island each face belongs to, indexed the same way `Flag.Triangles` is grouped; -1 for a face
    // the graph dropped (collision reconciliation removes faces that turned out to sit inside geometry).
    public required int[] IslandOfTriangle { get; init; }

    // Largest first, so island 0 is "the map" on a healthy flag and everything after it is a candidate
    // pocket. IslandOfTriangle indexes into this list, so a colour picked from the index is stable.
    public required IReadOnlyList<NavIsland> Islands { get; init; }

    public required IReadOnlyList<NavRimEdge> Rim { get; init; }

    public required int DroppedTriangles { get; init; }

    public int TriangleCount => IslandOfTriangle.Length;

    public int WalkableTriangles => TriangleCount - DroppedTriangles;

    // The share of walkable surface reachable from the biggest island. Anything short of 1 means some of
    // the mesh cannot be walked to from the rest of it.
    public double LargestIslandShare => Islands.Count == 0 || WalkableTriangles == 0
        ? 0.0
        : (double)Islands[0].TriangleCount / WalkableTriangles;

    // Islands small enough to be a defect rather than a place: a ledge Recast floored, a rooftop with no
    // way up, a pocket sealed off by a stitch that did not take. A zombie that spawns on one is stuck
    // there for the session, so these are what a diagnostic pass is looking for.
    public IEnumerable<(int Index, NavIsland Island)> Pockets(int maxTriangles)
    {
        for (int i = 0; i < Islands.Count; i++)
            if (Islands[i].TriangleCount <= maxTriangles)
                yield return (i, Islands[i]);
    }
}

public sealed partial class BakedNavGraph
{
    // Surveys every flag, in the order they were built. Pure reads of the graph that is already
    // assembled — no navmesh is re-derived here — so it is safe to run on a worker while nothing is
    // disabling faces, and it costs one pass over the faces plus one over their edges.
    public IReadOnlyList<NavFlagSurvey> Survey()
    {
        var surveys = new List<NavFlagSurvey>(_flags.Count);
        foreach (FlagGraph flag in _flags)
            surveys.Add(flag.Survey());
        return surveys;
    }

    private sealed partial class FlagGraph
    {
        public NavFlagSurvey Survey()
        {
            int count = _centres.Length;
            var islandOf = new int[count];
            Array.Fill(islandOf, -1);

            var triangles = new List<int>();
            var areas = new List<double>();
            var centroidSums = new List<Vector3>();
            var mins = new List<Vector3>();
            var maxs = new List<Vector3>();
            var pending = new Stack<int>();
            int dropped = 0;

            // Flood fill over the CSR adjacency, seeded in face order so the island ids — and therefore
            // every colour and every report line derived from them — come out the same on every run.
            for (int seed = 0; seed < count; seed++)
            {
                if (!_enabled[seed])
                {
                    dropped++;
                    continue;
                }
                if (islandOf[seed] >= 0)
                    continue;

                int island = triangles.Count;
                triangles.Add(0);
                areas.Add(0.0);
                centroidSums.Add(Vector3.Zero);
                mins.Add(new Vector3(float.MaxValue, float.MaxValue, float.MaxValue));
                maxs.Add(new Vector3(float.MinValue, float.MinValue, float.MinValue));

                islandOf[seed] = island;
                pending.Push(seed);
                while (pending.Count > 0)
                {
                    int triangle = pending.Pop();
                    triangles[island]++;
                    centroidSums[island] += _centres[triangle];

                    Vector3 a = Source.Vertices[Source.Triangles[triangle * 3]];
                    Vector3 b = Source.Vertices[Source.Triangles[(triangle * 3) + 1]];
                    Vector3 c = Source.Vertices[Source.Triangles[(triangle * 3) + 2]];
                    areas[island] += 0.5 * (b - a).Cross(c - a).Length();
                    mins[island] = Least(Least(Least(mins[island], a), b), c);
                    maxs[island] = Most(Most(Most(maxs[island], a), b), c);

                    for (int at = _edgeStart[triangle]; at < _edgeStart[triangle + 1]; at++)
                    {
                        int to = _edges[at].To;
                        if (!_enabled[to] || islandOf[to] >= 0)
                            continue;
                        islandOf[to] = island;
                        pending.Push(to);
                    }
                }
            }

            NavIsland[] islands = Rank(islandOf, triangles, areas, centroidSums, mins, maxs);
            return new NavFlagSurvey
            {
                Flag = Source,
                IslandOfTriangle = islandOf,
                Islands = islands,
                Rim = CollectRim(),
                DroppedTriangles = dropped,
            };
        }

        // Sorts the islands biggest first and rewrites `islandOf` to match, so the caller can treat
        // index 0 as the main surface without looking anything up.
        private NavIsland[] Rank(int[] islandOf, List<int> triangles, List<double> areas,
            List<Vector3> centroidSums, List<Vector3> mins, List<Vector3> maxs)
        {
            int islandCount = triangles.Count;
            var anchors = new Vector3[islandCount];
            var anchorScores = new float[islandCount];
            Array.Fill(anchorScores, float.MaxValue);
            for (int triangle = 0; triangle < islandOf.Length; triangle++)
            {
                int island = islandOf[triangle];
                if (island < 0)
                    continue;
                Vector3 centroid = centroidSums[island] / triangles[island];
                float score = _centres[triangle].DistanceSquaredTo(centroid);
                if (score >= anchorScores[island])
                    continue;
                anchorScores[island] = score;
                anchors[island] = _centres[triangle];
            }

            var order = new int[islandCount];
            for (int i = 0; i < islandCount; i++)
                order[i] = i;
            // Face count first because that is what "the main surface" means to the router; area breaks
            // the tie between two equally tessellated regions, and the id itself breaks the rest, so
            // equal islands never swap places between runs.
            Array.Sort(order, (x, y) =>
            {
                int byCount = triangles[y].CompareTo(triangles[x]);
                if (byCount != 0)
                    return byCount;
                int byArea = areas[y].CompareTo(areas[x]);
                return byArea != 0 ? byArea : x.CompareTo(y);
            });

            var rank = new int[islandCount];
            for (int i = 0; i < islandCount; i++)
                rank[order[i]] = i;
            for (int triangle = 0; triangle < islandOf.Length; triangle++)
                if (islandOf[triangle] >= 0)
                    islandOf[triangle] = rank[islandOf[triangle]];

            var islands = new NavIsland[islandCount];
            for (int i = 0; i < islandCount; i++)
            {
                int source = order[i];
                islands[i] = new NavIsland(triangles[source], areas[source], anchors[source],
                    mins[source], maxs[source]);
            }
            return islands;
        }

        // The parts of each face's boundary with no enabled floor across them.
        //
        // Not "edges no other face named", which is what the builder's own border pass answers. That
        // pass compares vertex PAIRS, so a stitched T-junction — one side spanning the join with a
        // single edge, the other split by a vertex partway along it — fails it on all three edges even
        // though the router walks straight across. Drawing those would put a red line through 1784 of
        // PEI's seams and send you looking for holes that are not there.
        //
        // So coverage is measured along the edge instead: every connection whose portal lies on this
        // edge's line covers the stretch between its endpoints, and what is left over is wall. That also
        // reports a HALF-stitched seam honestly — the covered part disappears and the rest is drawn,
        // which is the shape of the defect rather than all-or-nothing.
        private List<NavRimEdge> CollectRim()
        {
            var rim = new List<NavRimEdge>();
            var seen = new HashSet<(int Low, int High)>();
            var covered = new List<(float From, float To)>();
            for (int triangle = 0; triangle < _centres.Length; triangle++)
            {
                if (!_enabled[triangle])
                    continue;
                for (int edge = 0; edge < 3; edge++)
                {
                    int v0 = Source.Triangles[(triangle * 3) + edge];
                    int v1 = Source.Triangles[(triangle * 3) + ((edge + 1) % 3)];
                    // Overlapping or duplicated faces name the same edge from the same side, so the
                    // same wall can be reached twice; drawing it twice doubles the line count on
                    // exactly the maps that already have the most of them.
                    if (seen.Contains((Math.Min(v0, v1), Math.Max(v0, v1))))
                        continue;
                    // The pair is only CLAIMED by a face that could actually answer for it. A face
                    // with no side to be on cannot: "is there floor across this?" is decided by a
                    // sign, and a zero sign rejects every neighbour, so a sliver sharing an edge with
                    // real geometry would report the whole edge as wall AND stop the faces that know
                    // better from being asked. Leaving the key unclaimed hands the question to them.
                    if (AppendUncovered(triangle, v0, v1, covered, rim))
                        seen.Add((Math.Min(v0, v1), Math.Max(v0, v1)));
                }
            }
            return rim;
        }

        // True when this face answered for the edge — false when it is not equipped to, and the pair
        // should be left for another face naming it.
        private bool AppendUncovered(int triangle, int v0, int v1, List<(float From, float To)> covered,
            List<NavRimEdge> rim)
        {
            Vector3 a = Source.Vertices[v0], b = Source.Vertices[v1];
            float ex = b.X - a.X, ez = b.Z - a.Z;
            float lengthSquared = (ex * ex) + (ez * ez);
            if (lengthSquared <= 1e-8f)
                return false; // a vertical or zero-length edge in plan: nothing to draw a line along

            covered.Clear();
            // A face can name an edge and still have no third corner to be on a side OF: welding two
            // of a triangle's corners onto the same quantised position leaves an index repeated, and
            // {5, 5, 7} has nothing off the edge (5, 7). OppositeVertex says so with -1, which reads
            // straight through to Vertices[-1] if it is handed on — so the same guard the neighbour
            // test already carries has to come first here.
            int mine = OppositeVertex(triangle, v0, v1);
            if (mine < 0)
                return false;
            // And a zero-area face lies ALONG the edge rather than to one side of it, so it has no
            // opinion to offer on what is across. The same rule the snap and the stitch already apply.
            float here = SideOfEdge(v0, v1, mine);
            if (MathF.Abs(here) <= 1e-6f)
                return false;

            for (int at = _edgeStart[triangle]; at < _edgeStart[triangle + 1]; at++)
            {
                Connection c = _edges[at];
                if (!_enabled[c.To]
                    || !OnEdge(a, ex, ez, lengthSquared, c.VertexA, out float from)
                    || !OnEdge(a, ex, ez, lengthSquared, c.VertexB, out float to))
                    continue;

                // A neighbour covering no ground is not floor across anything, whatever its indices
                // say. This has to be tested on its own because the across test below cannot reach it:
                // a welded face has no vertex off the edge, so it takes the stitched-portal exemption
                // and would be counted as the floor that closes the edge.
                if (IsFlatInPlan(c.To))
                    continue;

                // Across, not merely alongside — the same question the router's own wall test asks, so
                // a duplicated coplanar face cannot erase a real boundary. A stitched portal is exempt
                // because its vertices need not belong to the neighbour at all (that is what makes it a
                // T-junction), and the stitch already required the two faces to be on opposite sides.
                int theirs = OppositeVertex(c.To, c.VertexA, c.VertexB);
                if (theirs >= 0 && here * SideOfEdge(v0, v1, theirs) >= 0f)
                    continue;

                covered.Add((MathF.Min(from, to), MathF.Max(from, to)));
            }

            covered.Sort(static (x, y) => x.From.CompareTo(y.From));
            float open = 0f;
            foreach ((float from, float to) in covered)
            {
                if (from > open)
                    Emit(a, b, open, MathF.Min(from, 1f), rim);
                open = MathF.Max(open, to);
                if (open >= 1f)
                    return true;
            }
            Emit(a, b, open, 1f, rim);
            return true;
        }

        // Whether a face covers no ground at all: collinear in plan, or welded onto a repeated corner.
        // Twice the signed XZ area, which is the same quantity the snap rejects a face on.
        private bool IsFlatInPlan(int triangle)
        {
            Vector3 a = Source.Vertices[Source.Triangles[triangle * 3]];
            Vector3 b = Source.Vertices[Source.Triangles[(triangle * 3) + 1]];
            Vector3 c = Source.Vertices[Source.Triangles[(triangle * 3) + 2]];
            return MathF.Abs(((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z))) <= 1e-6f;
        }

        // Below this the leftover is the float noise between two portals that meet, not a gap a body
        // could be held back by — well under the 0.05 m the stitch itself refuses to call a way through.
        private const float ShortestRimSegment = 0.01f;

        private static void Emit(Vector3 a, Vector3 b, float from, float to, List<NavRimEdge> rim)
        {
            if ((to - from) * a.DistanceTo(b) < ShortestRimSegment)
                return;
            rim.Add(new NavRimEdge(a.Lerp(b, from), a.Lerp(b, to)));
        }

        // Where `vertex` falls along the edge, if it sits on the edge's line at all. Recast writes its
        // vertices on a quantised grid and the stitch matched them with the same slack, so this is float
        // tolerance rather than a licence for "nearly collinear".
        private bool OnEdge(Vector3 a, float ex, float ez, float lengthSquared, int vertex, out float along)
        {
            Vector3 v = Source.Vertices[vertex];
            float length = MathF.Sqrt(lengthSquared);
            along = Math.Clamp((((v.X - a.X) * ex) + ((v.Z - a.Z) * ez)) / lengthSquared, 0f, 1f);
            return MathF.Abs(((v.X - a.X) * ez) - ((v.Z - a.Z) * ex)) / length <= JoinPlanarSlack;
        }

        private static Vector3 Least(Vector3 a, Vector3 b) =>
            new(MathF.Min(a.X, b.X), MathF.Min(a.Y, b.Y), MathF.Min(a.Z, b.Z));

        private static Vector3 Most(Vector3 a, Vector3 b) =>
            new(MathF.Max(a.X, b.X), MathF.Max(a.Y, b.Y), MathF.Max(a.Z, b.Z));
    }
}
