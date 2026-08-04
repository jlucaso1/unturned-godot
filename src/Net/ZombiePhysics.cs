using System;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Net;

// One authoritative set of zombie/world queries for hosted and standalone servers. Keeping the closures
// here prevents the dedicated path from silently degrading to heightmap-only movement when the windowed
// host gains a new collision rule.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
internal static class ZombiePhysics
{
    public static void Attach(ZombieSystem zombies, Func<World3D?> resolveWorld, GroundSampler ground)
    {
        ArgumentNullException.ThrowIfNull(zombies);
        ArgumentNullException.ThrowIfNull(resolveWorld);
        ArgumentNullException.ThrowIfNull(ground);

        World3D? world = null;
        World3D? World() => world ??= resolveWorld();

        // AlertTool's BLOCK_VISION query intentionally stops before the target and sees only authored
        // LARGE/MEDIUM occluders. It is perception, not permission to damage or abandon a detour.
        var visionRay = new PhysicsRayQueryParameters3D
        {
            CollisionMask = CollisionLayers.VisionBlocker,
        };
        zombies.VisionBlocked = (from, to) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return false;
            visionRay.From = from;
            visionRay.To = from + ((to - from) * ZombieBody.VisionRayFraction);
            using Godot.Collections.Dictionary seen = space.IntersectRay(visionRay);
            return seen.Count > 0;
        };

        // Authority checks inspect the complete eye-to-eye segment. World adds terrain, resources and
        // fences promoted by ObjectCollisionPolicy; VisionBlocker retains ordinary LARGE/MEDIUM walls.
        // Players live on a different bit, so reaching the target cannot hit its own capsule.
        var physicalRay = new PhysicsRayQueryParameters3D
        {
            CollisionMask = CollisionLayers.World | CollisionLayers.VisionBlocker,
        };
        zombies.PhysicalLineBlocked = (from, to) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return false;
            physicalRay.From = from;
            physicalRay.To = to;
            using Godot.Collections.Dictionary blocked = space.IntersectRay(physicalRay);
            return blocked.Count > 0;
        };

        var sweep = new CapsuleShape3D { Height = ZombieBody.CapsuleHeight };
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = sweep,
            CollisionMask = CollisionLayers.World,
        };
        var stepDown = new PhysicsRayQueryParameters3D
        {
            CollisionMask = CollisionLayers.World | CollisionLayers.MediumFurniture,
        };
        float lastRadius = -1f;
        zombies.MoveResolver = (from, to, radius) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return to;
            if (radius != lastRadius)
            {
                sweep.Radius = radius;
                lastRadius = radius;
            }

            var chest = new Vector3(0f, ZombieBody.CapsuleCenter, 0f);
            Vector3 at = from;
            Vector3 motion = to - from;
            float initialHorizontalSquared = (motion.X * motion.X) + (motion.Z * motion.Z);
            bool longClearanceProbe = initialHorizontalSquared
                > ZombieBody.MaxStepMotion * ZombieBody.MaxStepMotion;

            // CastMotion ignores an overlap at the starting transform. Endpoint occupancy protects a
            // real sub-metre movement because it cannot cross a fence plus the capsule diameter in one
            // tick, but a multi-metre navigation probe can begin embedded and end clean on the other
            // side. Recover that overlap before allowing its clear-cast fast path.
            if (longClearanceProbe)
            {
                query.Transform = new Transform3D(Basis.Identity, at + chest);
                query.Motion = Vector3.Zero;
                if (TryOverlapCorrection(space, query, motion, out Vector3 initialCorrection))
                {
                    if (initialCorrection.LengthSquared() <= 1e-10f
                        || initialCorrection.LengthSquared() > radius * radius * 4f)
                    {
                        return at;
                    }
                    at += initialCorrection + (initialCorrection.Normalized() * 0.001f);
                    motion = to - at;
                }
            }
            for (int pass = 0; pass < ZombieBody.MaxSlides; pass++)
            {
                if (motion.LengthSquared() < 1e-8f)
                    break;

                query.Transform = new Transform3D(Basis.Identity, at + chest);
                query.Motion = motion;
                float[] cast = space.CastMotion(query);
                bool destinationOccupied = false;
                if (cast[0] >= 1f)
                {
                    // CastMotion does not report a collider the capsule already touches at the start.
                    // That matters for thin barriers: the first tick can finish exactly tangent to a
                    // fence, then a diagonal second tick can be reported clear even though its endpoint
                    // is inside the fence. At zombie speed one tick cannot cross the fence plus the
                    // capsule diameter, so an occupied endpoint proves the supposedly clear cast is not
                    // executable. This is the same sweep-then-destination rule used by ladder clearance.
                    query.Transform = new Transform3D(Basis.Identity, at + motion + chest);
                    query.Motion = Vector3.Zero;
                    using Godot.Collections.Dictionary endpointRest = space.GetRestInfo(query);
                    destinationOccupied = endpointRest.Count > 0;
                    if (!destinationOccupied)
                    {
                        at += motion;
                        break;
                    }
                }

                // CastMotion intentionally ignores a collider already intersecting the capsule at
                // the start. Endpoint occupancy catches the symptom but, without recovering the
                // measured penetration, every possible move is then rejected forever. CollideShape
                // returns contact pairs whose difference is the minimum correction. Bias an ambiguous
                // horizontal correction against the requested motion so recovery cannot carry a body
                // through the same thin barrier it was trying to cross.
                if (pass == 0 && destinationOccupied)
                {
                    query.Transform = new Transform3D(Basis.Identity, at + chest);
                    query.Motion = Vector3.Zero;
                    if (TryOverlapCorrection(space, query, motion, out Vector3 correction)
                        && correction.LengthSquared() > 1e-10f
                        && correction.LengthSquared() <= radius * radius * 4f)
                    {
                        at += correction + (correction.Normalized() * 0.001f);
                        continue;
                    }
                }

                // Navigation validates complete route legs with this same authoritative capsule seam.
                // Such a multi-metre call asks only whether the endpoint is directly reachable. The
                // slide/step logic below models delivery over one <=0.52 m simulation tick; running it
                // over a long leg can land beyond a counter or wall and falsely certify that crossing.
                if (longClearanceProbe)
                {
                    float probeFraction = destinationOccupied ? 0f : cast[0];
                    return at + (motion * probeFraction);
                }

                float horizontalMotionSquared = (motion.X * motion.X) + (motion.Z * motion.Z);
                if (pass == 0
                    && horizontalMotionSquared <= ZombieBody.MaxStepMotion * ZombieBody.MaxStepMotion)
                {
                    query.Transform = new Transform3D(Basis.Identity,
                        at + chest + new Vector3(0f, Player.PlayerConfig.StepOffset, 0f));
                    query.Motion = motion;
                    float[] stepCast = space.CastMotion(query);
                    bool stepDestinationOccupied = false;
                    if (stepCast[0] >= 1f)
                    {
                        query.Transform = new Transform3D(Basis.Identity,
                            at + motion + chest
                                + new Vector3(0f, Player.PlayerConfig.StepOffset, 0f));
                        query.Motion = Vector3.Zero;
                        using Godot.Collections.Dictionary stepRest = space.GetRestInfo(query);
                        stepDestinationOccupied = stepRest.Count > 0;
                    }
                    if (stepCast[0] >= 1f && !stepDestinationOccupied)
                    {
                        stepDown.From = new Vector3(to.X, at.Y + 1.5f, to.Z);
                        stepDown.To = new Vector3(to.X, at.Y + 0.05f, to.Z);
                        using Godot.Collections.Dictionary support = space.IntersectRay(stepDown);
                        if (support.Count > 0)
                            return new Vector3(to.X, ((Vector3)support["position"]).Y, to.Z);
                    }
                }

                float safeFraction = destinationOccupied ? 0f : cast[0];
                Vector3 safe = at + (motion * safeFraction);
                // cast[1] is the first unsafe fraction. Querying rest at cast[0] asks at the last
                // non-overlapping point and can return no normal, leaving the body unable to slide.
                float contactFraction = destinationOccupied
                    ? 1f
                    : cast.Length > 1 ? cast[1] : cast[0];
                query.Transform = new Transform3D(Basis.Identity,
                    at + (motion * contactFraction) + chest);
                query.Motion = Vector3.Zero;
                using Godot.Collections.Dictionary rest = space.GetRestInfo(query);
                Vector3 normal = rest.Count > 0
                    ? (Vector3)rest["normal"]
                    : Vector3.Zero;
                if (normal == Vector3.Zero)
                    return safe;

                Vector3 remaining = motion * (1f - cast[0]);
                motion = remaining - (normal * remaining.Dot(normal));
                at = safe;
            }
            return at;
        };

        var snapRay = new PhysicsRayQueryParameters3D
        {
            CollisionMask = CollisionLayers.World | CollisionLayers.MediumFurniture,
        };
        zombies.GroundSnap = (Vector3 position, out float y) =>
        {
            PhysicsDirectSpaceState3D? space = World()?.DirectSpaceState;
            if (space == null)
                return ground(position.X, position.Z, out y);
            snapRay.From = position + new Vector3(0f, ZombieBody.GroundProbeUp, 0f);
            snapRay.To = position + new Vector3(0f, -ZombieBody.GroundProbeDown, 0f);
            using Godot.Collections.Dictionary hit = space.IntersectRay(snapRay);
            if (hit.Count > 0)
            {
                y = ((Vector3)hit["position"]).Y;
                return true;
            }
            return ground(position.X, position.Z, out y);
        };
    }

    private static bool TryOverlapCorrection(PhysicsDirectSpaceState3D space,
        PhysicsShapeQueryParameters3D query, Vector3 requestedMotion, out Vector3 correction)
    {
        Godot.Collections.Array<Vector3> contacts = space.CollideShape(query, 8);
        correction = Vector3.Zero;
        if (contacts.Count == 0)
            return false;
        for (int i = 0; i + 1 < contacts.Count; i += 2)
        {
            Vector3 candidate = contacts[i + 1] - contacts[i];
            Vector3 flatCandidate = candidate with { Y = 0f };
            Vector3 flatRequested = requestedMotion with { Y = 0f };
            if (flatCandidate.Dot(flatRequested) > 0f)
                candidate = -candidate;
            if (candidate.LengthSquared() > correction.LengthSquared())
                correction = candidate;
        }
        return true;
    }
}
