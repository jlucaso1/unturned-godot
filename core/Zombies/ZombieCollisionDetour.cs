using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Zombies;

internal delegate bool ZombieDetourPointProbe(Vector3 point, out Vector3 grounded);
internal delegate Vector3 ZombieDetourMotionProbe(Vector3 from, Vector3 to, float radius);

// A bounded physical fallback for obstacles that lie wholly inside one baked navmesh face. This is
// deliberately not another general pathfinder: nine polar rings out to 14 m, local neighbour edges,
// one fixed probe budget, and no edge the capsule has not traversed successfully in a single resolve.
internal sealed class ZombieCollisionDetour
{
    internal const int Rings = 9;
    internal const int Spokes = 32;
    internal const float RingStep = 2f;
    internal const int MaxPolarEdgeProbes = 1024;
    internal const float MaxSearchRadius = 14f;

    private const int PointCount = 1 + (Rings * Spokes);
    private const float WallFollowStep = 0.5f;
    // A point remains inside the same 14 m incident disc, but the safe perimeter inside that disc can
    // be much longer than its radius (the PEI shop route is ~35 m). Cap travelled perimeter separately.
    private const float MaxWallFollowDistance = 48f;
    private const int WallFollowSteps = (int)(MaxWallFollowDistance / WallFollowStep);
    // Wall following is a doorway anti-aliasing pre-pass, not a tax on the established polar search.
    // Reserve its worst case separately so a sealed wall cannot consume the probes needed to reach a
    // valid deep-counter exit that the polar fallback already knew how to find.
    internal const int MaxWallFollowProbes = (WallFollowSteps * 2 * 2) + 1;
    internal const int MaxWallFollowPointProbes = WallFollowSteps * 2 * 2;
    internal const int MaxEdgeProbes = MaxPolarEdgeProbes + MaxWallFollowProbes;
    private readonly Vector3[] _points = new Vector3[PointCount];
    private readonly bool[] _valid = new bool[PointCount];
    private readonly bool[] _closed = new bool[PointCount];
    private readonly float[] _costs = new float[PointCount];
    private readonly int[] _cameFrom = new int[PointCount];
    private readonly PriorityQueue<int, float> _frontier = new();
    private readonly List<int> _reverse = new(PointCount);
    private readonly List<Vector3> _wallPath = new(WallFollowSteps + 2);

    internal int LastProbeCount { get; private set; }

    internal bool TryFind(Vector3 from, Vector3 destination, float radius,
        ZombieDetourPointProbe pointProbe, ZombieDetourMotionProbe motionProbe,
        List<Vector3> output)
    {
        ArgumentNullException.ThrowIfNull(pointProbe);
        ArgumentNullException.ThrowIfNull(motionProbe);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        Array.Clear(_valid);
        Array.Clear(_closed);
        Array.Fill(_costs, float.PositiveInfinity);
        Array.Fill(_cameFrom, -1);
        _frontier.Clear();
        _reverse.Clear();
        _wallPath.Clear();
        LastProbeCount = 0;
        int phaseProbeStart = 0;
        int phaseProbeLimit = MaxWallFollowProbes;

        // Most exhausted routes are stale because the target moved into open ground. Prove that one
        // segment first; the polar search is only for an actual physical obstruction.
        if (Probe(from, destination, out _))
        {
            output.Add(from);
            output.Add(destination);
            return true;
        }

        // A polar lattice can alias a narrow doorway: shifting the body a few centimetres rotates all
        // spokes and changes whether any edge happens to pass through the opening. Follow the actual
        // collision front in both directions first. The resolver's safe endpoint tells us the local
        // normal, 0.5 m steps cannot jump a thin wall, and every emitted step is physically resolved.
        // This is what makes a door available immediately instead of after repeated partial-route loops
        // happen to move the lattice into a lucky phase.
        if (FollowWall(1f) || FollowWall(-1f))
            return true;

        phaseProbeStart = LastProbeCount;
        phaseProbeLimit = MaxPolarEdgeProbes;

        _points[0] = from;
        _valid[0] = true;
        float baseAngle = MathF.Atan2(destination.Z - from.Z, destination.X - from.X);
        for (int ring = 0; ring < Rings; ring++)
        {
            float distance = RingDistance(ring);
            for (int spoke = 0; spoke < Spokes; spoke++)
            {
                float angle = baseAngle + (MathF.Tau * spoke / Spokes);
                var candidate = new Vector3(from.X + (MathF.Cos(angle) * distance), from.Y,
                    from.Z + (MathF.Sin(angle) * distance));
                int index = Index(ring, spoke);
                if (pointProbe(candidate, out _points[index]))
                    _valid[index] = true;
            }
        }

        _costs[0] = 0f;
        _frontier.Enqueue(0, Distance(from, destination));
        int reached = -1;
        while (_frontier.TryDequeue(out int current, out _))
        {
            if (_closed[current])
                continue;
            _closed[current] = true;
            if (current != 0 && Probe(_points[current], destination, out _))
            {
                reached = current;
                break;
            }
            if (!HasBudget())
                break;

            if (current == 0)
            {
                for (int spoke = 0; spoke < Spokes && HasBudget(); spoke++)
                    Relax(current, Index(0, spoke));
                continue;
            }

            int currentRing = (current - 1) / Spokes;
            int currentSpoke = (current - 1) % Spokes;
            for (int dr = -1; dr <= 1 && HasBudget(); dr++)
                for (int ds = -1; ds <= 1 && HasBudget(); ds++)
                {
                    if (dr == 0 && ds == 0)
                        continue;
                    int nextRing = currentRing + dr;
                    if (nextRing < 0 || nextRing >= Rings)
                        continue;
                    int nextSpoke = (currentSpoke + ds + Spokes) % Spokes;
                    Relax(current, Index(nextRing, nextSpoke));
                }
        }
        if (reached < 0)
            return false;

        for (int at = reached; at > 0; at = _cameFrom[at])
            _reverse.Add(at);
        _reverse.Reverse();
        output.Add(from);
        foreach (int index in _reverse)
            output.Add(_points[index]);
        output.Add(destination);
        return true;

        bool HasBudget() => LastProbeCount - phaseProbeStart < phaseProbeLimit
            && LastProbeCount < MaxEdgeProbes;

        bool Probe(Vector3 a, Vector3 b, out Vector3 resolved)
        {
            if (!HasBudget())
            {
                resolved = a;
                return false;
            }
            LastProbeCount++;
            resolved = motionProbe(a, b, radius);
            // A centimetres-wide miss is still a collision, especially at a thin fence. The host
            // resolver returns the requested endpoint for a clear sweep; retain only a 1 mm numerical
            // tolerance, matching ZombieSystem's direct capsule reachability check.
            return resolved.DistanceSquaredTo(b) <= 1e-6f;
        }

        void Relax(int current, int next)
        {
            if (!_valid[next] || _closed[next]
                || !Probe(_points[current], _points[next], out _))
                return;
            float candidate = _costs[current] + Distance(_points[current], _points[next]);
            if (candidate + 1e-4f >= _costs[next])
                return;
            _costs[next] = candidate;
            _cameFrom[next] = current;
            _frontier.Enqueue(next, candidate + Distance(_points[next], destination));
        }

        bool FollowWall(float side)
        {
            _wallPath.Clear();
            _wallPath.Add(from);
            Vector3 current = from;
            Vector3 previousTangent = Vector3.Zero;
            for (int step = 0; step < WallFollowSteps && HasBudget(); step++)
            {
                if (Probe(current, destination, out Vector3 contact))
                {
                    _wallPath.Add(destination);
                    output.AddRange(_wallPath);
                    return true;
                }

                Vector3 normal = destination - contact;
                normal.Y = 0f;
                if (normal.LengthSquared() <= 1e-8f)
                    return false;
                normal = normal.Normalized();
                Vector3 tangent = new(-normal.Z * side, 0f, normal.X * side);
                if (previousTangent.LengthSquared() > 0f && tangent.Dot(previousTangent) < -0.25f)
                    tangent = -tangent;
                Vector3 desired = current + (tangent * WallFollowStep);
                if (!pointProbe(desired, out Vector3 grounded))
                    return false;

                bool reached = Probe(current, grounded, out Vector3 moved);
                Vector3 next = grounded;
                if (!reached)
                {
                    if (Distance(current, moved) < 0.1f
                        || !pointProbe(moved, out next))
                        return false;
                }
                if (Distance(current, next) < 0.1f)
                    return false;
                if (Distance(from, next) > MaxSearchRadius)
                    return false;
                for (int i = 0; i + 2 < _wallPath.Count; i++)
                    if (Distance(_wallPath[i], next) < 0.2f)
                        return false;
                _wallPath.Add(next);
                current = next;
                previousTangent = tangent;
            }
            return false;
        }
    }

    private static int Index(int ring, int spoke) => 1 + (ring * Spokes) + spoke;

    private static float RingDistance(int ring) => ring switch
    {
        0 => 0.5f,
        1 => 1f,
        _ => (ring - 1) * RingStep,
    };

    private static float Distance(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
