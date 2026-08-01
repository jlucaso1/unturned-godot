using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Data;

// Decides which triangles of a pre-baked navmesh a body cannot actually stand on, given the collision
// world it has to walk through.
//
// Unturned baked its navmeshes with a climb tolerance larger than the CharacterController's
// m_StepOffset, so the graph lays walkable surface straight over obstacles the body cannot climb — a 1 m
// window sill gets bridged. The planner then hands out a route that is passable to it and impassable to
// the physics: a zombie walks into the sill and stands there for good, because it is faithfully following
// a route that lies.
//
// The rule here is the agent's own step height. A face whose ground sits more than one step above the
// lowest neighbour it shares an edge with cannot be reached from that neighbour, so it is not a route.
// Kerbs and stair treads, well under the step, are left alone.
//
// The surface sampling is injected so this stays pure logic: the game passes a physics raycast, tests
// pass an analytic surface.
public static class NavmeshReachability
{
    // The real walking surface under a point. False when there is nothing there at all, in which case
    // the baked data is trusted rather than second-guessed.
    public delegate bool SurfaceProbe(Vector3 point, out float y);

    // Where a face is sampled. The baked triangles are large enough that one can span an opening AND the
    // solid beside it, so sampling only the centre reports the whole face walkable and the funnel then
    // cuts the shortest line straight across the solid half. Centre, three interior points and the three
    // edge midpoints catch that; the HIGHEST surface found is the one that matters, because that is what
    // a body crossing the face would have to climb onto.
    public static IEnumerable<Vector3> SamplePoints(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 centre = (a + b + c) / 3f;
        yield return centre;
        yield return centre.Lerp(a, 0.6f);
        yield return centre.Lerp(b, 0.6f);
        yield return centre.Lerp(c, 0.6f);
        yield return (a + b) * 0.5f;
        yield return (b + c) * 0.5f;
        yield return (c + a) * 0.5f;
    }

    // The indices (into the flag's triangle list, one per 3 indices) of the faces to drop.
    public static HashSet<int> Unreachable(NavFlag flag, float stepOffset, SurfaceProbe probe)
    {
        int count = flag.Triangles.Length / 3;
        var surface = new float[count];
        var known = new bool[count];

        for (int t = 0; t < count; t++)
        {
            Vector3 a = flag.Vertices[flag.Triangles[t * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(t * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(t * 3) + 2]];

            float highest = float.MinValue;
            foreach (Vector3 sample in SamplePoints(a, b, c))
                if (probe(sample, out float y) && y > highest)
                    highest = y;

            if (highest == float.MinValue)
                continue; // nothing under the whole face: leave the baked data alone
            surface[t] = highest;
            known[t] = true;
        }

        return Unreachable(flag, stepOffset, surface, known);
    }

    // The decision itself, over already-sampled surfaces. Split out so a caller that samples in bulk
    // (or a test with a known surface) can reuse it.
    public static HashSet<int> Unreachable(NavFlag flag, float stepOffset, float[] surface, bool[] known)
    {
        int count = flag.Triangles.Length / 3;
        var drop = new HashSet<int>();
        if (count == 0)
            return drop;

        // Adjacency by shared edge. The baked mesh indexes shared vertices, so an undirected index pair
        // identifies an edge exactly — no proximity guessing.
        var byEdge = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < count; t++)
            for (int e = 0; e < 3; e++)
            {
                int v0 = flag.Triangles[(t * 3) + e];
                int v1 = flag.Triangles[(t * 3) + ((e + 1) % 3)];
                (int, int) key = v0 < v1 ? (v0, v1) : (v1, v0);
                if (!byEdge.TryGetValue(key, out List<int>? sharing))
                    byEdge[key] = sharing = new List<int>();
                sharing.Add(t);
            }

        var lowestNeighbour = new float[count];
        var authoredHighest = new float[count];
        var hasNeighbour = new bool[count];
        for (int t = 0; t < count; t++)
        {
            lowestNeighbour[t] = float.MaxValue;
            authoredHighest[t] = MathF.Max(
                flag.Vertices[flag.Triangles[t * 3]].Y,
                MathF.Max(flag.Vertices[flag.Triangles[(t * 3) + 1]].Y,
                    flag.Vertices[flag.Triangles[(t * 3) + 2]].Y));
        }
        foreach (List<int> sharing in byEdge.Values)
            foreach (int t in sharing)
                foreach (int other in sharing)
                    if (other != t)
                    {
                        hasNeighbour[t] = true;
                        if (known[other] && surface[other] < lowestNeighbour[t])
                            lowestNeighbour[t] = surface[other];
                    }

        for (int t = 0; t < count; t++)
            if (known[t] && (
                // A face can be part of a whole cluster bridging the same obstacle, leaving no lower
                // neighbour to expose it. Compare against its authored upper envelope too: a collision
                // surface above every vertex by more than the body can step is not the walkable face.
                (hasNeighbour[t] && surface[t] - authoredHighest[t] > stepOffset)
                || (lowestNeighbour[t] < float.MaxValue
                    && surface[t] - lowestNeighbour[t] > stepOffset)))
                drop.Add(t);

        return drop;
    }
}
