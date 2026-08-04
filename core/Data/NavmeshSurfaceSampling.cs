using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace UnturnedGodot.Data;

// Samples a navmesh flag's walking surface against the CPU collision world, in exactly the geometry the
// physics probe uses: look down from above the highest thing the agent could step onto, and stop below the
// navmesh so a floor beneath it is never mistaken for this one's ground.
//
// This is the whole point of CollisionField. The identical loop against PhysicsDirectSpaceState3D can only
// run inside a physics notification, so it is serialised into a slice of each tick and a map's first
// session spends minutes on it; here it is pure CPU over an immutable structure, so it runs on workers, in
// parallel, while the physics thread does nothing at all.
public static class NavmeshSurfaceSampling
{
    // How far above a sample point the probe starts, on top of the agent's step offset. Anything the agent
    // could climb onto has to be inside this, and nothing on the storey above should be.
    public const float ProbeHeadroom = 1.5f;

    // How far below a sample point the probe reaches. Beyond this it would find the floor under this one.
    public const float ProbeReach = 1f;

    public static float TopOf(Vector3 point, float stepOffset) => point.Y + stepOffset + ProbeHeadroom;

    public static float BottomOf(Vector3 point) => point.Y - ProbeReach;

    public readonly record struct FlagSurfaces(
        float[] Clearance,
        bool[] Known,
        float[] Slack,
        bool[] Uncertain,
        int Samples,
        int UncertainSamples);

    // Every face of `flag`, sampled at the seven points NavmeshReachability asks for. The resulting
    // clearance is the climb a body crossing the face would actually have to make: height above the
    // authored plane or a discontinuity within it. Absolute heights are never compared across broad,
    // sloping faces.
    public static FlagSurfaces Sample(NavFlag flag, CollisionField field, float stepOffset,
        CancellationToken cancellation = default)
    {
        int count = flag.Triangles.Length / 3;
        var clearance = new float[count];
        var known = new bool[count];
        var slack = new float[count];
        var uncertain = new bool[count];
        int samples = 0, uncertainSamples = 0;

        var options = new ParallelOptions { CancellationToken = cancellation };
        Parallel.For(0, count, options, () => (Samples: 0, Uncertain: 0), (triangle, _, local) =>
        {
            Vector3 a = flag.Vertices[flag.Triangles[triangle * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(triangle * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(triangle * 3) + 2]];

            float highestClearance = float.MinValue;
            Span<Vector3> points = stackalloc Vector3[7];
            Span<float> surfaces = stackalloc float[7];
            int hitSamples = 0;
            float worstSlack = 0f;
            bool doubtful = false;
            foreach (Vector3 point in NavmeshReachability.SamplePoints(a, b, c))
            {
                SurfaceSample sample = field.Probe(point, TopOf(point, stepOffset), BottomOf(point));
                local.Samples++;
                if (sample.Uncertain)
                {
                    doubtful = true;
                    local.Uncertain++;
                }
                if (!sample.Hit)
                    continue;
                float clearance = sample.Y - point.Y;
                if (clearance > highestClearance)
                    highestClearance = clearance;
                points[hitSamples] = point;
                surfaces[hitSamples++] = sample.Y;
                if (sample.Slack > worstSlack)
                    worstSlack = sample.Slack;
            }

            if (highestClearance != float.MinValue)
            {
                clearance[triangle] = NavmeshReachability.RequiredClimbForFace(a, b, c, stepOffset,
                    highestClearance, points[..hitSamples], surfaces[..hitSamples]);
                known[triangle] = true;
            }
            slack[triangle] = worstSlack;
            uncertain[triangle] = doubtful;
            return local;
        },
        local =>
        {
            Interlocked.Add(ref samples, local.Samples);
            Interlocked.Add(ref uncertainSamples, local.Uncertain);
        });

        return new FlagSurfaces(clearance, known, slack, uncertain, samples, uncertainSamples);
    }
}
