using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

// The dump's collision slice, wired up as the three world delegates the zombie brain asks for. This is
// the half of a replay that survives someone CHANGING the brain: recorded answers only cover the
// queries the old code made, and the first thing a fix does is make different ones.
//
// It models the host's resolver rather than approximating it — the same capsule (ZombieBody), the same
// four-pass collide-and-slide, the same step-up attempt on first contact, the same ground and vision
// probes and the same layer masks. Initial-overlap recovery uses the captured triangle contacts rather
// than Godot's solver, but follows the same conservative rule: escape away from the requested barrier.
public sealed class ReproCollisionWorld
{
    // How the sweep finds first contact: walk the motion in coarse samples until one overlaps, then
    // bisect that interval. 16 samples over one tick's 0.44 m step is 2.8 cm of coarse resolution.
    // Navigation also sends multi-metre clearance probes, so their sample count grows with distance;
    // keeping spacing below half the capsule radius prevents a thin fence from fitting between samples.
    private const int SweepSamples = 16;
    private const int SweepRefinements = 10;

    private readonly ReproTriangles _triangles;
    private readonly List<int> _candidates = new();

    // The heightfield patch the dump carries, used when no triangle is under a ground probe — the same
    // fallback order the host has (physics first, terrain sampler second).
    private readonly ReproHeightSampler? _ground;

    private readonly Vector3 _centre;
    private readonly float _radiusSquared;

    public ReproCollisionWorld(ReproTriangles triangles, ReproHeightSampler? ground = null,
        Vector3 centre = default, float radius = float.PositiveInfinity)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        _triangles = triangles;
        _ground = ground;
        _centre = centre;
        _radiusSquared = float.IsFinite(radius) ? radius * radius : float.PositiveInfinity;
    }

    public static ReproCollisionWorld? From(ReproWorldData? world)
    {
        ReproTriangles? triangles = ReproTriangles.From(world?.Geometry);
        if (triangles == null)
            return null;
        ReproGeometryData geometry = world!.Geometry!;
        return new ReproCollisionWorld(triangles, ReproHeightSampler.From(world.Ground),
            ReproVector.To(geometry.Center),
            geometry.Radius > 0f ? geometry.Radius : float.PositiveInfinity);
    }

    // Is this point somewhere the capture actually looked? Outside the slice there are no triangles,
    // so every query answers "nothing there" — which is indistinguishable from open ground and is the
    // one way this world can lie. Callers ask first and count the difference.
    //
    // `reach` is how far the query extends from the point: a swept capsule reaches its own radius
    // sideways and its full height upward, and a ground probe reaches metres down. A body whose centre
    // sits just inside the slice can still be resting against a wall whose triangles are just outside
    // it, so the centre alone is not the question.
    public bool Covers(Vector3 point, float reach = 0f)
    {
        if (float.IsPositiveInfinity(_radiusSquared))
            return true;
        float distance = (point - _centre).Length() + MathF.Max(0f, reach);
        return distance * distance <= _radiusSquared;
    }

    public int TriangleCount => _triangles.TriangleCount;

    // The same short downward column reconciliation asks of the live physics world. Keeping it here
    // lets a dump verify a changed reconciliation algorithm against its captured geometry instead of
    // silently restoring the old session's disabled-face list.
    public bool ProbeNavSurface(Vector3 point, float stepOffset, out float y)
    {
        float top = NavmeshSurfaceSampling.TopOf(point, stepOffset);
        float bottom = NavmeshSurfaceSampling.BottomOf(point);
        if (!CoversNavSurface(point, stepOffset))
        {
            y = 0f;
            return false;
        }

        Vector3 from = new(point.X, top, point.Z);
        Vector3 to = new(point.X, bottom, point.Z);
        if (Raycast(from, to, CollisionLayers.World, out float fraction, out _))
        {
            y = Mathf.Lerp(top, bottom, fraction);
            return true;
        }
        if (_ground != null && _ground.Sample(point.X, point.Z, out y)
            && y >= bottom && y <= top)
            return true;
        y = 0f;
        return false;
    }

    // Coverage is separate from a probe answer: inside the captured slice, finding no surface is real
    // evidence; outside it, the same empty result merely means the dump did not record that column.
    public bool CoversNavSurface(Vector3 point, float stepOffset)
    {
        float top = NavmeshSurfaceSampling.TopOf(point, stepOffset);
        float bottom = NavmeshSurfaceSampling.BottomOf(point);
        return Covers(point, MathF.Max(top - point.Y, point.Y - bottom));
    }

    // Which collider the last Resolve stopped against, by name. The reason a dump records owner names
    // at all: "stuck against Street_Light" is a bug report, "stuck at (-613, 35, -65)" is a coordinate.
    public string LastBlocker { get; private set; } = "";

    // NetworkManager's move resolver, without an engine. Layer mask is World only: MEDIUM furniture
    // lives on its own bit and the original's zombies shove through it.
    public Vector3 Resolve(Vector3 from, Vector3 to, float radius)
    {
        LastBlocker = "";
        Vector3 motion = to - from;
        Vector3 at = RecoverInitialOverlap(from, radius, motion);
        float initialHorizontalSquared = (motion.X * motion.X) + (motion.Z * motion.Z);
        if (initialHorizontalSquared > ZombieBody.MaxStepMotion * ZombieBody.MaxStepMotion)
        {
            // Long calls are clearance probes from navigation, never one simulation step. Sliding and
            // step-up are delivery behaviours that only make sense over the body's real tick distance;
            // applying them to a whole route leg can climb or teleport over the obstruction being tested.
            float time = Sweep(at, motion, radius, CollisionLayers.World, out _, out int triangle);
            if (time < 1f)
                LastBlocker = _triangles.OwnerOf(triangle);
            return at + (motion * time);
        }
        for (int pass = 0; pass < ZombieBody.MaxSlides; pass++)
        {
            if (motion.LengthSquared() < 1e-8f)
                break;
            float time = Sweep(at, motion, radius, CollisionLayers.World, out Vector3 normal,
                out int triangle);
            if (time >= 1f)
                return at + motion;
            LastBlocker = _triangles.OwnerOf(triangle);

            // The CharacterController's step pass, attempted once on first contact exactly like the
            // host's: retry the sweep raised by the step offset, and only accept it when there is
            // ground under the destination within the climb, or a body would float over gaps.
            float horizontalMotionSquared = (motion.X * motion.X) + (motion.Z * motion.Z);
            if (pass == 0
                && horizontalMotionSquared <= ZombieBody.MaxStepMotion * ZombieBody.MaxStepMotion)
            {
                var lift = new Vector3(0f, Player.PlayerConfig.StepOffset, 0f);
                if (Sweep(at + lift, motion, radius, CollisionLayers.World, out _, out _) >= 1f
                    && GroundBetween(new Vector3(to.X, at.Y + 1.5f, to.Z),
                        new Vector3(to.X, at.Y + 0.05f, to.Z), out float steppedY))
                {
                    return new Vector3(to.X, steppedY, to.Z);
                }
            }

            Vector3 safe = at + (motion * time);
            Vector3 remaining = motion * (1f - time);
            motion = remaining - (normal * remaining.Dot(normal));
            at = safe;
        }
        return at;
    }

    // CastMotion deliberately ignores shapes already intersecting at the start. Without a recovery
    // pass, a capsule pushed a few centimetres into a counter gets an occupied destination on every
    // possible move and remains frozen forever. Move it only by the measured penetration depth and
    // bias an ambiguous triangle normal against the requested motion, so overlap recovery cannot be
    // used as a shortcut through the barrier the zombie was trying to cross.
    private Vector3 RecoverInitialOverlap(Vector3 at, float radius, Vector3 requestedMotion)
    {
        float half = MathF.Max(0f, (ZombieBody.CapsuleHeight / 2f) - radius);
        Vector3 centerOffset = new(0f, ZombieBody.CapsuleCenter, 0f);
        float radiusSquared = radius * radius;
        for (int pass = 0; pass < ZombieBody.MaxSlides; pass++)
        {
            float padding = radius + half + 0.01f;
            _triangles.Gather(at.X - padding, at.Z - padding,
                at.X + padding, at.Z + padding, _candidates);
            if (!Overlaps(at + centerOffset, half, radiusSquared, CollisionLayers.World,
                    out Vector3 normal, out int triangle, out float distanceSquared))
                break;
            LastBlocker = _triangles.OwnerOf(triangle);
            if (normal.LengthSquared() < 1e-10f)
                normal = _triangles.NormalOf(triangle);
            if (normal.LengthSquared() < 1e-10f)
                break;
            normal = normal.Normalized();
            Vector3 flatNormal = normal with { Y = 0f };
            Vector3 flatMotion = requestedMotion with { Y = 0f };
            if (flatNormal.Dot(flatMotion) > 0f)
                normal = -normal;
            float depth = radius - MathF.Sqrt(MathF.Max(0f, distanceSquared));
            if (depth <= 0f)
                break;
            at += normal * (depth + 0.001f);
        }
        return at;
    }

    // The host's ground probe: a short ray from just above the step offset, down three metres, against
    // the solid world plus the furniture a body stands on.
    public bool GroundSnap(Vector3 position, out float y)
    {
        Vector3 from = position + new Vector3(0f, ZombieBody.GroundProbeUp, 0f);
        Vector3 to = position + new Vector3(0f, -ZombieBody.GroundProbeDown, 0f);
        if (GroundBetween(from, to, out y))
            return true;
        return _ground != null && _ground.Sample(position.X, position.Z, out y);
    }

    // AlertTool's BLOCK_VISION ray, stopping short of the target's own collider.
    public bool VisionBlocked(Vector3 from, Vector3 to)
    {
        Vector3 end = from + ((to - from) * ZombieBody.VisionRayFraction);
        return Raycast(from, end, CollisionLayers.VisionBlocker, out _, out _);
    }

    // Attacks and collision recovery are authority checks, not AlertTool perception: include every solid
    // movement layer and inspect the endpoint instead of dropping the last five percent.
    public bool PhysicalLineBlocked(Vector3 from, Vector3 to) =>
        Raycast(from, to, CollisionLayers.World | CollisionLayers.VisionBlocker, out _, out _);

    private bool GroundBetween(Vector3 from, Vector3 to, out float y)
    {
        if (Raycast(from, to, CollisionLayers.World | CollisionLayers.MediumFurniture,
            out float distance, out _))
        {
            y = from.Y + ((to.Y - from.Y) * distance);
            return true;
        }
        y = 0f;
        return false;
    }

    // Nearest hit along the segment, as a fraction of it.
    public bool Raycast(Vector3 from, Vector3 to, uint mask, out float fraction, out int hitTriangle)
    {
        fraction = 1f;
        hitTriangle = -1;
        Vector3 delta = to - from;
        float length = delta.Length();
        if (length < 1e-6f)
            return false;
        Vector3 direction = delta / length;
        _triangles.Gather(MathF.Min(from.X, to.X), MathF.Min(from.Z, to.Z),
            MathF.Max(from.X, to.X), MathF.Max(from.Z, to.Z), _candidates);
        float best = length;
        foreach (int triangle in _candidates)
        {
            if (!_triangles.Matches(triangle, mask))
                continue;
            if (_triangles.RayTriangle(triangle, from, direction, out float distance)
                && distance <= best)
            {
                best = distance;
                hitTriangle = triangle;
            }
        }
        if (hitTriangle < 0)
            return false;
        fraction = best / length;
        return true;
    }

    // First contact of the swept capsule, as a fraction of the motion. `normal` points away from the
    // surface, which is what the slide projects against.
    private float Sweep(Vector3 at, Vector3 motion, float radius, uint mask, out Vector3 normal,
        out int hitTriangle)
    {
        normal = Vector3.Up;
        hitTriangle = -1;
        float half = MathF.Max(0f, (ZombieBody.CapsuleHeight / 2f) - radius);
        Vector3 center = new(0f, ZombieBody.CapsuleCenter, 0f);
        Vector3 destination = at + motion;
        float padding = radius + half + 0.01f;
        _triangles.Gather(
            MathF.Min(at.X, destination.X) - padding, MathF.Min(at.Z, destination.Z) - padding,
            MathF.Max(at.X, destination.X) + padding, MathF.Max(at.Z, destination.Z) + padding,
            _candidates);
        if (_candidates.Count == 0)
            return 1f;

        float radiusSquared = radius * radius;
        int samples = Math.Max(SweepSamples,
            (int)MathF.Ceiling(motion.Length() / MathF.Max(0.05f, radius * 0.5f)));
        float blocked = -1f;
        for (int sample = 1; sample <= samples; sample++)
        {
            float time = sample / (float)samples;
            if (!Overlaps(at + (motion * time) + center, half, radiusSquared, mask,
                    out _, out _, out _))
                continue;
            blocked = time;
            break;
        }
        if (blocked < 0f)
            return 1f;

        float free = blocked - (1f / samples);
        if (free < 0f)
            free = 0f;
        for (int i = 0; i < SweepRefinements; i++)
        {
            float middle = (free + blocked) * 0.5f;
            if (Overlaps(at + (motion * middle) + center, half, radiusSquared, mask,
                    out _, out _, out _))
                blocked = middle;
            else
                free = middle;
        }
        // The normal is read at the blocked position, where the contact actually is.
        Overlaps(at + (motion * blocked) + center, half, radiusSquared, mask, out normal,
            out hitTriangle, out _);
        if (normal.LengthSquared() < 1e-10f)
            normal = hitTriangle >= 0 ? _triangles.NormalOf(hitTriangle) : Vector3.Up;
        else
            normal = normal.Normalized();
        return free;
    }

    private bool Overlaps(Vector3 center, float half, float radiusSquared, uint mask,
        out Vector3 normal, out int hitTriangle, out float distanceSquared)
    {
        normal = Vector3.Zero;
        hitTriangle = -1;
        distanceSquared = float.MaxValue;
        Vector3 p0 = center - new Vector3(0f, half, 0f);
        Vector3 p1 = center + new Vector3(0f, half, 0f);
        float deepest = float.MaxValue;
        foreach (int triangle in _candidates)
        {
            if (!_triangles.Matches(triangle, mask))
                continue;
            float distance = _triangles.DistanceSquared(triangle, p0, p1, out Vector3 direction);
            if (distance >= radiusSquared || distance >= deepest)
                continue;
            deepest = distance;
            distanceSquared = distance;
            normal = direction;
            hitTriangle = triangle;
        }
        return hitTriangle >= 0;
    }

}
