using System;
using Godot;

namespace UnturnedGodot.Damage;

// Ray casts against the analytic shapes a character is made of, for the queries the physics world cannot
// answer. Zombies are not bodies in the server's physics space — the brain moves them by sweeping a
// capsule and never registers one — so the punch cannot ask Godot what it hit and has to intersect the
// same capsule the mover uses. Pure maths, one place, so the shape the swing tests is provably the shape
// the zombie occupies.
public static class Hitscan
{
    // Ray against a vertical capsule standing on `feet`: `height` tall overall, `radius` thick, so its
    // spherical caps are centred at feet + radius and feet + height - radius. Returns the distance along
    // `direction` at which it is first entered, and the point there.
    //
    // A ray whose ORIGIN is already inside the capsule hits at distance zero rather than missing. That is
    // not a corner case here: a punch starts at the eyes and reaches 1.75 m, so a player standing chest
    // to chest with a zombie is inside it, and refusing that hit would make the game unpunchable at
    // exactly the range punching happens at.
    public static bool RayCapsule(Vector3 origin, Vector3 direction, float maxDistance,
        Vector3 feet, float height, float radius, out float distance, out Vector3 point)
    {
        distance = 0f;
        point = origin;
        if (radius <= 0f || maxDistance <= 0f || direction == Vector3.Zero)
            return false;

        direction = direction.Normalized();
        float capsuleHeight = MathF.Max(height, radius * 2f); // shorter than a sphere: it IS the sphere
        Vector3 a = feet + (Vector3.Up * radius);
        Vector3 b = feet + (Vector3.Up * (capsuleHeight - radius));

        if (SquaredDistanceToSegment(origin, a, b) <= radius * radius)
            return true; // already inside: the fist connects where it stands

        if (!RayCapsuleSurface(origin, direction, a, b, radius, out float t) || t > maxDistance)
            return false;

        distance = t;
        point = origin + (direction * t);
        return true;
    }

    // The nearest positive intersection of a normalized ray with the surface of the capsule spanning
    // a..b. Body first, then whichever cap the body solution ran past — the standard decomposition of a
    // capsule into a finite cylinder plus two spheres.
    private static bool RayCapsuleSurface(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b,
        float radius, out float t)
    {
        t = 0f;
        Vector3 ba = b - a;
        Vector3 oa = origin - a;
        float baba = ba.Dot(ba);
        float bard = ba.Dot(direction);
        float baoa = ba.Dot(oa);
        float rdoa = direction.Dot(oa);
        float oaoa = oa.Dot(oa);

        float quadA = baba - (bard * bard);
        float quadB = (baba * rdoa) - (baoa * bard);
        float quadC = (baba * oaoa) - (baoa * baoa) - (radius * radius * baba);
        float discriminant = (quadB * quadB) - (quadA * quadC);

        // quadA is zero exactly when the ray runs parallel to the axis, where the cylinder has no
        // solution at all and only the caps can be hit.
        if (discriminant >= 0f && quadA > 0f)
        {
            float body = (-quadB - MathF.Sqrt(discriminant)) / quadA;
            float along = baoa + (body * bard);
            if (along > 0f && along < baba && body >= 0f)
            {
                t = body;
                return true;
            }
        }

        bool hitA = RaySphere(origin, direction, a, radius, out float capA);
        bool hitB = RaySphere(origin, direction, b, radius, out float capB);
        if (hitA && hitB)
            t = MathF.Min(capA, capB);
        else if (hitA)
            t = capA;
        else if (hitB)
            t = capB;
        return hitA || hitB;
    }

    private static bool RaySphere(Vector3 origin, Vector3 direction, Vector3 centre, float radius,
        out float t)
    {
        t = float.NaN;
        Vector3 oc = origin - centre;
        float b = direction.Dot(oc);
        float c = oc.Dot(oc) - (radius * radius);
        float discriminant = (b * b) - c;
        if (discriminant < 0f)
            return false;
        float hit = -b - MathF.Sqrt(discriminant);
        if (hit < 0f)
            return false;
        t = hit;
        return true;
    }

    // The squared distance from a point to the segment a..b — the capsule's own distance field, used
    // above to answer "is the ray origin already inside".
    public static float SquaredDistanceToSegment(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared <= 0f)
            return (point - a).LengthSquared();
        float t = Math.Clamp((point - a).Dot(ab) / lengthSquared, 0f, 1f);
        return (point - a - (ab * t)).LengthSquared();
    }
}
