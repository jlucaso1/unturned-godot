using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Zombies;

namespace UnturnedGodot.Repro;

// The dump's collision slice, wired up as the three world delegates the zombie brain asks for. This is
// the half of a replay that survives someone CHANGING the brain: recorded answers only cover the
// queries the old code made, and the first thing a fix does is make different ones.
//
// It models the host's resolver rather than approximating it — the same capsule (ZombieBody), the same
// four-pass collide-and-slide, the same step-up attempt on first contact, the same ground and vision
// probes and the same layer masks. What it cannot model is the engine's depenetration of a body that
// starts inside geometry; there it stops at the surface, which is also what a single CastMotion does.
public sealed class ReproCollisionWorld
{
    // How the sweep finds first contact: walk the motion in coarse samples until one overlaps, then
    // bisect that interval. 16 samples over one tick's 0.44 m step is 2.8 cm of coarse resolution, and
    // 10 bisections take the answer under a tenth of a millimetre.
    private const int SweepSamples = 16;
    private const int SweepRefinements = 10;

    private readonly ReproTriangles _triangles;
    private readonly List<int> _candidates = new();

    // The heightfield patch the dump carries, used when no triangle is under a ground probe — the same
    // fallback order the host has (physics first, terrain sampler second).
    private readonly ReproHeightSampler? _ground;

    public ReproCollisionWorld(ReproTriangles triangles, ReproHeightSampler? ground = null)
    {
        ArgumentNullException.ThrowIfNull(triangles);
        _triangles = triangles;
        _ground = ground;
    }

    public static ReproCollisionWorld? From(ReproWorldData? world)
    {
        ReproTriangles? triangles = ReproTriangles.From(world?.Geometry);
        return triangles == null
            ? null
            : new ReproCollisionWorld(triangles, ReproHeightSampler.From(world?.Ground));
    }

    public int TriangleCount => _triangles.TriangleCount;

    // Which collider the last Resolve stopped against, by name. The reason a dump records owner names
    // at all: "stuck against Street_Light" is a bug report, "stuck at (-613, 35, -65)" is a coordinate.
    public string LastBlocker { get; private set; } = "";

    // NetworkManager's move resolver, without an engine. Layer mask is World only: MEDIUM furniture
    // lives on its own bit and the original's zombies shove through it.
    public Vector3 Resolve(Vector3 from, Vector3 to, float radius)
    {
        LastBlocker = "";
        Vector3 at = from;
        Vector3 motion = to - from;
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
            if (pass == 0)
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
        float blocked = -1f;
        for (int sample = 1; sample <= SweepSamples; sample++)
        {
            float time = sample / (float)SweepSamples;
            if (!Overlaps(at + (motion * time) + center, half, radiusSquared, mask, out _, out _))
                continue;
            blocked = time;
            break;
        }
        if (blocked < 0f)
            return 1f;

        float free = blocked - (1f / SweepSamples);
        if (free < 0f)
            free = 0f;
        for (int i = 0; i < SweepRefinements; i++)
        {
            float middle = (free + blocked) * 0.5f;
            if (Overlaps(at + (motion * middle) + center, half, radiusSquared, mask, out _, out _))
                blocked = middle;
            else
                free = middle;
        }
        // The normal is read at the blocked position, where the contact actually is.
        Overlaps(at + (motion * blocked) + center, half, radiusSquared, mask, out normal,
            out hitTriangle);
        if (normal.LengthSquared() < 1e-10f)
            normal = hitTriangle >= 0 ? _triangles.NormalOf(hitTriangle) : Vector3.Up;
        else
            normal = normal.Normalized();
        return free;
    }

    private bool Overlaps(Vector3 center, float half, float radiusSquared, uint mask,
        out Vector3 normal, out int hitTriangle)
    {
        normal = Vector3.Zero;
        hitTriangle = -1;
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
            normal = direction;
            hitTriangle = triangle;
        }
        return hitTriangle >= 0;
    }

}
