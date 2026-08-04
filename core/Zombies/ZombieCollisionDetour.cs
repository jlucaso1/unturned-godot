using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Zombies;

internal delegate bool ZombieDetourPointProbe(Vector3 point, out Vector3 grounded);
internal delegate bool ZombieDetourSegmentProbe(Vector3 from, Vector3 to, float radius);

// A bounded physical fallback for obstacles that lie wholly inside one baked navmesh face. This is
// deliberately not another general pathfinder: nine polar rings out to 14 m, local neighbour edges,
// one fixed probe budget, and no edge the capsule has not traversed successfully in a single resolve.
internal sealed class ZombieCollisionDetour
{
    internal const int Rings = 9;
    internal const int Spokes = 32;
    internal const float RingStep = 2f;
    internal const int MaxEdgeProbes = 1024;

    private const int PointCount = 1 + (Rings * Spokes);
    private readonly Vector3[] _points = new Vector3[PointCount];
    private readonly bool[] _valid = new bool[PointCount];
    private readonly bool[] _closed = new bool[PointCount];
    private readonly float[] _costs = new float[PointCount];
    private readonly int[] _cameFrom = new int[PointCount];
    private readonly PriorityQueue<int, float> _frontier = new();
    private readonly List<int> _reverse = new(PointCount);

    internal int LastProbeCount { get; private set; }

    internal bool TryFind(Vector3 from, Vector3 destination, float radius,
        ZombieDetourPointProbe pointProbe, ZombieDetourSegmentProbe segmentProbe,
        List<Vector3> output)
    {
        ArgumentNullException.ThrowIfNull(pointProbe);
        ArgumentNullException.ThrowIfNull(segmentProbe);
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        Array.Clear(_valid);
        Array.Clear(_closed);
        Array.Fill(_costs, float.PositiveInfinity);
        Array.Fill(_cameFrom, -1);
        _frontier.Clear();
        _reverse.Clear();
        LastProbeCount = 0;

        // Most exhausted routes are stale because the target moved into open ground. Prove that one
        // segment first; the polar search is only for an actual physical obstruction.
        if (Probe(from, destination))
        {
            output.Add(from);
            output.Add(destination);
            return true;
        }

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
            if (current != 0 && Probe(_points[current], destination))
            {
                reached = current;
                break;
            }
            if (LastProbeCount >= MaxEdgeProbes)
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

        bool HasBudget() => LastProbeCount < MaxEdgeProbes;

        bool Probe(Vector3 a, Vector3 b)
        {
            if (!HasBudget())
                return false;
            LastProbeCount++;
            return segmentProbe(a, b, radius);
        }

        void Relax(int current, int next)
        {
            if (!_valid[next] || _closed[next] || !Probe(_points[current], _points[next]))
                return;
            float candidate = _costs[current] + Distance(_points[current], _points[next]);
            if (candidate + 1e-4f >= _costs[next])
                return;
            _costs[next] = candidate;
            _cameFrom[next] = current;
            _frontier.Enqueue(next, candidate + Distance(_points[next], destination));
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
