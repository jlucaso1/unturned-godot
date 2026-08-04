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
// The rule here is the agent's own step height. At every sample, collision is measured relative to the
// authored navmesh plane at that exact point. A face whose real surface rises more than one step above
// that plane is not a route; kerbs, stair treads and continuous authored slopes are left alone.
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
    // edge midpoints catch that; the HIGHEST clearance above the authored plane is the one that matters,
    // because that is what a body crossing the face would have to climb onto.
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
        var clearance = new float[count];
        var known = new bool[count];

        for (int t = 0; t < count; t++)
        {
            Vector3 a = flag.Vertices[flag.Triangles[t * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(t * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(t * 3) + 2]];

            float highestClearance = float.MinValue;
            foreach (Vector3 sample in SamplePoints(a, b, c))
                if (probe(sample, out float y))
                    highestClearance = MathF.Max(highestClearance, y - sample.Y);

            if (highestClearance == float.MinValue)
                continue; // nothing under the whole face: leave the baked data alone
            clearance[t] = highestClearance;
            known[t] = true;
        }

        return Unreachable(flag, stepOffset, clearance, known);
    }

    // The decision itself, over already-sampled clearances above the authored face. Split out so a caller
    // that samples in bulk (or a test with a known clearance) can reuse it.
    public static HashSet<int> Unreachable(NavFlag flag, float stepOffset, float[] clearance, bool[] known)
    {
        int count = flag.Triangles.Length / 3;
        var drop = new HashSet<int>();
        if (count == 0)
            return drop;

        Context context = Analyse(flag, count);
        for (int t = 0; t < count; t++)
            if (known[t] && Drops(in context, t, clearance, stepOffset))
                drop.Add(t);

        return drop;
    }

    // The rule itself. NeedsConfirmation asks the same question of its cheaper surfaces, so it lives in
    // one place: a confirmation set derived from a drifted copy of this would confirm the wrong faces.
    private static bool Drops(in Context context, int t, float[] clearance, float stepOffset) =>
        // An isolated face supplies no route into itself, so there is no reachable side from which to
        // infer a climb. Every connected face is judged directly against the authored plane under each
        // sample. Comparing absolute heights between faces is invalid: broad adjacent triangles can rise
        // by more than one step while forming one continuous, shallow slope.
        context.HasNeighbour[t] && clearance[t] > stepOffset;

    // The faces whose verdict a cheap surface sampler is not entitled to settle on its own.
    //
    // CollisionField answers the same downward probe the physics server does, off the physics thread and
    // orders of magnitude faster, but it is a second implementation of the same geometry and the two agree
    // only to within a margin. That is harmless almost everywhere: a face sitting a metre clear of the step
    // threshold reaches the same verdict either way. It is not harmless in three places, and this names
    // all three.
    //
    // The first is a sample the field flagged as uncertain — it is saying outright that it cannot answer.
    //
    // The second is a face whose clearance is close: within `margin` (the fixed allowance for the gap
    // between two collision implementations) plus `slack` (the sampler's own measured uncertainty for this
    // face).
    //
    // The third is every face the sampled surfaces would DROP. The two directions of this decision are not
    // symmetric: keeping a face leaves the baked navmesh as its authors shipped it, while dropping one
    // removes route from the graph, and a route removed in error is the failure this whole pass exists to
    // prevent — a zombie with nowhere to walk is worse than one taking a route it has to shove through.
    // The destructive verdict is therefore never reached on the cheap measurement alone, however far it
    // sits from the threshold.
    public static HashSet<int> NeedsConfirmation(NavFlag flag, float stepOffset, float[] clearance,
        bool[] known, float[] slack, bool[] uncertain, float margin)
    {
        int count = flag.Triangles.Length / 3;
        var confirm = new HashSet<int>();
        if (count == 0)
            return confirm;

        Context context = Analyse(flag, count);
        for (int t = 0; t < count; t++)
        {
            if (uncertain[t])
            {
                confirm.Add(t);
                continue;
            }
            if (!known[t])
                continue;

            if (Drops(in context, t, clearance, stepOffset))
            {
                confirm.Add(t);
                continue;
            }

            float allowance = margin + slack[t];
            if (context.HasNeighbour[t]
                && MathF.Abs(clearance[t] - stepOffset) <= allowance)
                confirm.Add(t);
        }
        return confirm;
    }

    // The faces that would be dropped by clearances the physics server has not measured. Confirmation no
    // longer changes another face's arithmetic: every verdict is relative to that face's authored plane.
    // The method remains separate because the audit and fallback paths share the invariant that no
    // destructive verdict is published from an unverified CPU measurement.
    public static HashSet<int> UnverifiedDrops(NavFlag flag, float stepOffset, float[] clearance,
        bool[] known, IReadOnlySet<int> verified)
    {
        int count = flag.Triangles.Length / 3;
        var unverified = new HashSet<int>();
        if (count == 0)
            return unverified;

        Context context = Analyse(flag, count);
        for (int t = 0; t < count; t++)
        {
            if (!known[t] || !Drops(in context, t, clearance, stepOffset))
                continue;
            if (!verified.Contains(t))
                unverified.Add(t);
        }
        return unverified;
    }

    private readonly struct Context
    {
        public required bool[] HasNeighbour { get; init; }
    }

    // Adjacency by shared edge. The baked mesh indexes shared vertices, so an undirected index pair
    // identifies an edge exactly — no proximity guessing.
    private static Context Analyse(NavFlag flag, int count)
    {
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

        var hasNeighbour = new bool[count];
        foreach (List<int> sharing in byEdge.Values)
            foreach (int t in sharing)
                foreach (int other in sharing)
                    if (other != t)
                        hasNeighbour[t] = true;

        return new Context
        {
            HasNeighbour = hasNeighbour,
        };
    }
}
