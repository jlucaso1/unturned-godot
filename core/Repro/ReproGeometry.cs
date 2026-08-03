using System;
using Godot;

namespace UnturnedGodot.Repro;

// The two pieces of geometry a physics-free replay needs: how far a capsule's core segment is from a
// triangle, and where a ray meets one. Textbook constructions (Ericson, Möller-Trumbore), by value and
// without allocation, because a replayed second calls them hundreds of thousands of times.
public static class ReproGeometry
{
    public static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        // Voronoi regions of the triangle, in order: vertices, edges, interior.
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = ab.Dot(ap), d2 = ac.Dot(ap);
        if (d1 <= 0f && d2 <= 0f)
            return a;

        Vector3 bp = p - b;
        float d3 = ab.Dot(bp), d4 = ac.Dot(bp);
        if (d3 >= 0f && d4 <= d3)
            return b;

        float vc = (d1 * d4) - (d3 * d2);
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
            return a + (ab * (d1 / (d1 - d3)));

        Vector3 cp = p - c;
        float d5 = ab.Dot(cp), d6 = ac.Dot(cp);
        if (d6 >= 0f && d5 <= d6)
            return c;

        float vb = (d5 * d2) - (d1 * d6);
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
            return a + (ac * (d2 / (d2 - d6)));

        float va = (d3 * d6) - (d5 * d4);
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
            return b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6))));

        float denominator = 1f / (va + vb + vc);
        return a + (ab * (vb * denominator)) + (ac * (vc * denominator));
    }

    // Closest points between two segments, including the degenerate cases (either or both collapsed to
    // a point, and parallel segments) — all three occur here: a mega's capsule is nearly a point, and
    // an axis-aligned wall triangle is parallel to a straight-line step every time.
    public static void ClosestPointsSegments(Vector3 p0, Vector3 p1, Vector3 q0, Vector3 q1,
        out Vector3 closestP, out Vector3 closestQ)
    {
        Vector3 d1 = p1 - p0, d2 = q1 - q0, r = p0 - q0;
        float a = d1.Dot(d1), e = d2.Dot(d2), f = d2.Dot(r);
        float s, t;
        const float Epsilon = 1e-12f;

        if (a <= Epsilon && e <= Epsilon)
        {
            closestP = p0;
            closestQ = q0;
            return;
        }
        if (a <= Epsilon)
        {
            s = 0f;
            t = Math.Clamp(f / e, 0f, 1f);
        }
        else
        {
            float c = d1.Dot(r);
            if (e <= Epsilon)
            {
                t = 0f;
                s = Math.Clamp(-c / a, 0f, 1f);
            }
            else
            {
                float b = d1.Dot(d2);
                float denominator = (a * e) - (b * b);
                s = denominator > Epsilon ? Math.Clamp(((b * f) - (c * e)) / denominator, 0f, 1f) : 0f;
                t = ((b * s) + f) / e;
                if (t < 0f)
                {
                    t = 0f;
                    s = Math.Clamp(-c / a, 0f, 1f);
                }
                else if (t > 1f)
                {
                    t = 1f;
                    s = Math.Clamp((b - c) / a, 0f, 1f);
                }
            }
        }
        closestP = p0 + (d1 * s);
        closestQ = q0 + (d2 * t);
    }

    // Squared distance between a segment and a triangle. `direction` points from the triangle to the
    // segment — the outward direction a slide projects the remaining motion onto. Zero when the segment
    // passes through the triangle, in which case the direction falls back to the face normal on the
    // segment's side, which is the only meaningful answer for a body already inside a wall.
    public static float SegmentTriangleDistanceSquared(Vector3 p0, Vector3 p1,
        Vector3 a, Vector3 b, Vector3 c, out Vector3 direction)
    {
        Vector3 normal = (b - a).Cross(c - a);
        float normalLengthSquared = normal.LengthSquared();
        if (normalLengthSquared > 1e-16f)
        {
            float d0 = normal.Dot(p0 - a), d1 = normal.Dot(p1 - a);
            if ((d0 < 0f && d1 > 0f) || (d0 > 0f && d1 < 0f))
            {
                float t = d0 / (d0 - d1);
                Vector3 hit = p0 + ((p1 - p0) * t);
                if ((ClosestPointOnTriangle(hit, a, b, c) - hit).LengthSquared() <= 1e-8f)
                {
                    direction = normal / MathF.Sqrt(normalLengthSquared);
                    if (d0 < 0f)
                        direction = -direction;
                    return 0f;
                }
            }
        }

        Vector3 bestSegment = p0, bestTriangle = ClosestPointOnTriangle(p0, a, b, c);
        float best = (bestSegment - bestTriangle).LengthSquared();
        Consider(p1, ClosestPointOnTriangle(p1, a, b, c), ref best, ref bestSegment, ref bestTriangle);

        Edge(p0, p1, a, b, ref best, ref bestSegment, ref bestTriangle);
        Edge(p0, p1, b, c, ref best, ref bestSegment, ref bestTriangle);
        Edge(p0, p1, c, a, ref best, ref bestSegment, ref bestTriangle);

        direction = bestSegment - bestTriangle;
        return best;
    }

    private static void Edge(Vector3 p0, Vector3 p1, Vector3 e0, Vector3 e1,
        ref float best, ref Vector3 bestSegment, ref Vector3 bestTriangle)
    {
        ClosestPointsSegments(p0, p1, e0, e1, out Vector3 onSegment, out Vector3 onEdge);
        Consider(onSegment, onEdge, ref best, ref bestSegment, ref bestTriangle);
    }

    private static void Consider(Vector3 onSegment, Vector3 onTriangle,
        ref float best, ref Vector3 bestSegment, ref Vector3 bestTriangle)
    {
        float distance = (onSegment - onTriangle).LengthSquared();
        if (distance >= best)
            return;
        best = distance;
        bestSegment = onSegment;
        bestTriangle = onTriangle;
    }

    // Möller-Trumbore, both faces: a collider's inward face still stops a ray, and the world here is a
    // slice cut out of larger meshes, so backfaces are common at the cut.
    public static bool RayTriangle(Vector3 from, Vector3 direction, Vector3 a, Vector3 b, Vector3 c,
        out float distance)
    {
        distance = 0f;
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 h = direction.Cross(e2);
        float determinant = e1.Dot(h);
        if (MathF.Abs(determinant) < 1e-9f)
            return false; // parallel to the plane
        float inverse = 1f / determinant;
        Vector3 s = from - a;
        float u = inverse * s.Dot(h);
        if (u < 0f || u > 1f)
            return false;
        Vector3 q = s.Cross(e1);
        float v = inverse * direction.Dot(q);
        if (v < 0f || u + v > 1f)
            return false;
        distance = inverse * e2.Dot(q);
        return distance >= 0f;
    }
}
