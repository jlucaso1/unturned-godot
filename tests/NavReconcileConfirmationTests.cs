using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// Reconciliation now samples the navmesh against CollisionField on a worker and only asks the physics
// server about what that cannot settle. Two things have to hold for that to be a speed-up rather than a
// behaviour change: the CPU sampling must reach the same verdict the physics probe reaches, and the set
// of faces it hands back for confirmation must contain everything where it might not.
public class NavReconcileConfirmationTests
{
    private const float StepOffset = 0.5f;

    // A flat navmesh grid of 1 m quads over [0, size] x [0, size], indexed so triangles share vertices.
    private static NavFlag FlatGrid(int quads)
    {
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        int stride = quads + 1;
        for (int x = 0; x <= quads; x++)
            for (int z = 0; z <= quads; z++)
                vertices.Add(new Vector3(x, 0f, z));
        for (int x = 0; x < quads; x++)
            for (int z = 0; z < quads; z++)
            {
                int a = (x * stride) + z, b = a + 1, c = a + stride, d = c + 1;
                triangles.AddRange(new[] { a, b, c, b, d, c });
            }
        return new NavFlag
        {
            Vertices = vertices.ToArray(),
            Triangles = triangles.ToArray(),
            Center = new Vector3(quads / 2f, 0f, quads / 2f),
            Size = new Vector3(quads + 1, 10f, quads + 1),
        };
    }

    private static void AddGround(CollisionFieldBuilder builder, int quads, float height)
    {
        int res = quads + 1;
        var data = new float[res * res];
        Array.Fill(data, height);
        float centre = quads * 0.5f;
        builder.AddHeightfield(
            new Transform3D(Basis.Identity.Scaled(new Vector3(1f, 1f, 1f)),
                new Vector3(centre, 0f, centre)),
            res, res, data);
    }

    // The window sill this whole pass exists for: a block the navmesh is laid straight over, standing a
    // metre above ground where the body can only step half of one. Bounds are deliberately off every
    // sixth and half of a metre so no sample point lands inside the graze band and the two probes are
    // being compared on geometry, not on tie-breaking.
    private const float SillFrom = 3.3712f;
    private const float SillTo = 6.6413f;
    private const float SillTop = 1f;

    private static CollisionField SillWorld(int quads)
    {
        var builder = new CollisionFieldBuilder();
        AddGround(builder, quads, 0f);
        float centre = (SillFrom + SillTo) * 0.5f;
        float span = SillTo - SillFrom;
        int block = builder.AddBoxShape(new Vector3(span, SillTop, span));
        builder.AddInstance(block,
            new Transform3D(Basis.Identity, new Vector3(centre, SillTop * 0.5f, centre)));
        return builder.Build();
    }

    private static NavmeshReachability.SurfaceProbe AnalyticSill()
    {
        return (Vector3 point, out float y) =>
        {
            bool onSill = point.X >= SillFrom && point.X <= SillTo
                && point.Z >= SillFrom && point.Z <= SillTo;
            y = onSill ? SillTop : 0f;
            return true;
        };
    }

    [Fact]
    public void SamplingTheFieldReachesTheSameVerdictAsProbingTheWorld()
    {
        NavFlag flag = FlatGrid(10);
        CollisionField field = SillWorld(10);

        HashSet<int> reference = NavmeshReachability.Unreachable(flag, StepOffset, AnalyticSill());
        NavmeshSurfaceSampling.FlagSurfaces sampled =
            NavmeshSurfaceSampling.Sample(flag, field, StepOffset);
        HashSet<int> fromField =
            NavmeshReachability.Unreachable(flag, StepOffset, sampled.Clearance, sampled.Known);

        Assert.NotEmpty(reference);
        Assert.Equal(reference, fromField);
    }

    [Fact]
    public void SamplingAContinuousSlopeReportsClearanceNotWorldHeight()
    {
        var flag = new NavFlag
        {
            Vertices = new[]
            {
                new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
                new Vector3(-1f, 1f, 10f), new Vector3(1f, 1f, 10f),
                new Vector3(-1f, 2f, 20f), new Vector3(1f, 2f, 20f),
            },
            Triangles = new[] { 0, 1, 2, 1, 3, 2, 2, 3, 4, 3, 5, 4 },
            Center = new Vector3(0f, 1f, 10f),
            Size = new Vector3(4f, 4f, 22f),
        };
        var faces = new Vector3[flag.Triangles.Length];
        for (int i = 0; i < faces.Length; i++)
            faces[i] = flag.Vertices[flag.Triangles[i]];
        var builder = new CollisionFieldBuilder();
        int slope = builder.AddMeshShape(faces);
        builder.AddInstance(slope, Transform3D.Identity);

        NavmeshSurfaceSampling.FlagSurfaces sampled =
            NavmeshSurfaceSampling.Sample(flag, builder.Build(), StepOffset);

        Assert.All(sampled.Known, Assert.True);
        Assert.All(sampled.Clearance, value => Assert.InRange(value, -0.0001f, 0.0001f));
        Assert.Empty(NavmeshReachability.Unreachable(flag, StepOffset, sampled.Clearance,
            sampled.Known));
    }

    [Fact]
    public void EverySampleOverKnownGroundIsAnswered()
    {
        NavFlag flag = FlatGrid(10);
        NavmeshSurfaceSampling.FlagSurfaces sampled =
            NavmeshSurfaceSampling.Sample(flag, SillWorld(10), StepOffset);

        Assert.Equal(flag.Triangles.Length / 3 * 7, sampled.Samples);
        Assert.All(sampled.Known, Assert.True);
    }

    [Fact]
    public void FacesFarFromTheStepThresholdAreNotSentToTheServer()
    {
        // The point of the whole exercise: on ordinary ground almost nothing needs confirming, because
        // almost nothing is close to the decision.
        NavFlag flag = FlatGrid(10);
        NavmeshSurfaceSampling.FlagSurfaces sampled =
            NavmeshSurfaceSampling.Sample(flag, SillWorld(10), StepOffset);

        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset,
            sampled.Clearance,
            sampled.Known, sampled.Slack, sampled.Uncertain, margin: 0.05f);

        int faces = flag.Triangles.Length / 3;
        Assert.True(confirm.Count * 4 < faces,
            $"expected a small confirmation set, got {confirm.Count} of {faces}");
    }

    [Fact]
    public void AFaceSittingExactlyOnTheThresholdIsSentToTheServer()
    {
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        var slack = new float[faces];
        var uncertain = new bool[faces];

        // Face 0 stands exactly one step above the ground its neighbours sit on: either verdict is
        // defensible, so neither is this code's to reach.
        surface[0] = StepOffset;

        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            slack, uncertain, margin: 0.05f);

        Assert.Contains(0, confirm);
        Assert.DoesNotContain(3, confirm);
    }

    [Fact]
    public void EveryFaceTheSampledSurfacesWouldDropIsSentToTheServer()
    {
        // Keeping a face leaves the baked navmesh as shipped; dropping one takes route out of the graph.
        // The second is not a verdict a cheaper measurement gets to reach on its own, however far from
        // the threshold it thinks it is — a route removed in error is the failure this pass exists to
        // prevent.
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        var slack = new float[faces];
        var uncertain = new bool[faces];
        surface[0] = StepOffset + 4f; // nowhere near the threshold, and dropped

        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            slack, uncertain, margin: 0.05f);

        Assert.Contains(0, NavmeshReachability.Unreachable(flag, StepOffset, surface, known));
        Assert.Contains(0, confirm);
    }

    [Fact]
    public void SlackWidensTheBandExactlyWhereItWasMeasured()
    {
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        var uncertain = new bool[faces];
        // Clear of the threshold by more than the fixed margin, and on the keeping side of it — the
        // dropping side is confirmed unconditionally, so it could not show what slack does.
        surface[0] = StepOffset - 0.2f;

        var none = new float[faces];
        Assert.DoesNotContain(0, NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            none, uncertain, margin: 0.05f));

        var measured = new float[faces];
        measured[0] = 0.3f; // ... but the sampler said its own answer could move by that much
        Assert.Contains(0, NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            measured, uncertain, margin: 0.05f));
    }

    [Fact]
    public void ANeighboursSlackCannotChangeAFaceLocalClearance()
    {
        // Each face is judged against its own authored plane. Uncertainty in a neighbour cannot turn a
        // walkable clearance into a step and needlessly send this face to the server.
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        var uncertain = new bool[faces];
        var slack = new float[faces];
        surface[0] = StepOffset - 0.2f;

        // Whichever faces share an edge with face 0 — the strip's own adjacency decides that.
        for (int t = 1; t < faces; t++)
            slack[t] = 0.3f;

        Assert.DoesNotContain(0, NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            slack, uncertain, margin: 0.05f));
    }

    [Fact]
    public void AnUncertainSampleAlwaysReachesTheServer()
    {
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        var slack = new float[faces];
        var uncertain = new bool[faces];
        uncertain[5] = true;

        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            slack, uncertain, margin: 0.05f);

        Assert.Contains(5, confirm);
    }

    [Fact]
    public void GroundTheFieldNeverSawIsHandedOverRatherThanTreatedAsMissing()
    {
        // A navmesh flag reaching past the recorded collision world. The old path would have found the
        // real bodies there; this must not quietly answer "nothing under it" instead.
        NavFlag flag = FlatGrid(10);
        var builder = new CollisionFieldBuilder();
        AddGround(builder, quads: 3, height: 0f); // covers only a corner of the flag
        CollisionField field = builder.Build();

        NavmeshSurfaceSampling.FlagSurfaces sampled =
            NavmeshSurfaceSampling.Sample(flag, field, StepOffset);
        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset,
            sampled.Clearance,
            sampled.Known, sampled.Slack, sampled.Uncertain, margin: 0.05f);

        int faces = flag.Triangles.Length / 3;
        int unknown = 0;
        for (int t = 0; t < faces; t++)
            if (!sampled.Known[t])
                unknown++;
        Assert.True(unknown > 0, "the fixture is meant to leave part of the flag uncovered");
        for (int t = 0; t < faces; t++)
            if (!sampled.Known[t])
                Assert.Contains(t, confirm);
    }

    [Fact]
    public void AFlagWithNoFacesAsksForNothing()
    {
        var empty = new NavFlag { Vertices = Array.Empty<Vector3>(), Triangles = Array.Empty<int>() };
        Assert.Empty(NavmeshReachability.NeedsConfirmation(empty, StepOffset, Array.Empty<float>(),
            Array.Empty<bool>(), Array.Empty<float>(), Array.Empty<bool>(), margin: 0.05f));
        Assert.Empty(NavmeshReachability.Unreachable(empty, StepOffset, Array.Empty<float>(),
            Array.Empty<bool>()));
        Assert.Empty(NavmeshReachability.UnverifiedDrops(empty, StepOffset, Array.Empty<float>(),
            Array.Empty<bool>(), new HashSet<int>()));
    }

    [Fact]
    public void AnIsolatedNearThresholdFaceNeedsNoDestructiveConfirmation()
    {
        var isolated = new NavFlag
        {
            Vertices = new[] { Vector3.Zero, Vector3.Right, Vector3.Forward },
            Triangles = new[] { 0, 1, 2 },
        };

        Assert.Empty(NavmeshReachability.NeedsConfirmation(isolated, StepOffset,
            new[] { StepOffset }, new[] { true }, new[] { 0.01f }, new[] { false },
            margin: 0.05f));
    }

    [Fact]
    public void UnverifiedDrops_IgnoresUnknownAndAlreadyVerifiedFaces()
    {
        NavFlag flag = FlatGrid(1); // two connected faces
        float[] clearance = { 1f, 1f };
        bool[] known = { true, false };

        Assert.Equal(new[] { 0 }, NavmeshReachability.UnverifiedDrops(flag, StepOffset,
            clearance, known, new HashSet<int>()));
        Assert.Empty(NavmeshReachability.UnverifiedDrops(flag, StepOffset,
            clearance, known, new HashSet<int> { 0 }));
    }

    [Fact]
    public void AFaceTheFieldAnsweredWithNoSurfaceAtAllIsLeftAlone()
    {
        // Not the same as an uncertain face. Over ground the field models, finding nothing between the
        // two probe heights is an answer, and the answer is that the baked data stands.
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        known[2] = false;
        var slack = new float[faces];
        var uncertain = new bool[faces];

        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            slack, uncertain, margin: 0.05f);

        Assert.DoesNotContain(2, confirm);
        Assert.DoesNotContain(2, NavmeshReachability.Unreachable(flag, StepOffset, surface, known));
    }

    [Fact]
    public void ConfirmingOneFaceCannotMakeANeighbourDroppable()
    {
        // Clearances are relative to each face's own authored plane. Replacing one CPU answer with the
        // server's answer therefore cannot manufacture a step on adjacent, otherwise unchanged faces.
        NavFlag flag = FlatGrid(4);
        int faces = flag.Triangles.Length / 3;
        var surface = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true); // every face level with the navmesh, and so with every other face
        var slack = new float[faces];
        var uncertain = new bool[faces];

        HashSet<int> planned = NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            slack, uncertain, margin: 0.05f);
        Assert.Empty(planned); // nothing near the threshold, nothing droppable: nothing to settle

        // The server measures face 1 (flagged for some other reason) and finds the ground well below its
        // authored plane. That does not alter any other face's clearance.
        var verified = new HashSet<int> { 1 };
        surface[1] = -0.54f;

        HashSet<int> again = NavmeshReachability.UnverifiedDrops(flag, StepOffset, surface, known,
            verified);

        Assert.Empty(again);
        Assert.Empty(NavmeshReachability.Unreachable(flag, StepOffset, surface, known));
    }

    [Fact]
    public void NoDropSurvivesOnAnUnmeasuredHeight()
    {
        // The loop's invariant, stated directly: keep confirming what UnverifiedDrops names until it
        // names nothing, and every dropped face rests on its own measured clearance.
        NavFlag flag = FlatGrid(6);
        int faces = flag.Triangles.Length / 3;
        var sampled = new float[faces];
        var truth = new float[faces];
        var known = new bool[faces];
        Array.Fill(known, true);
        var random = new Random(4242);
        for (int t = 0; t < faces; t++)
        {
            sampled[t] = (float)(random.NextDouble() * 1.2);
            // The server's answer differs from the sampler's by up to a whole step in places.
            truth[t] = MathF.Max(0f, sampled[t] + (float)((random.NextDouble() - 0.5) * 1.0));
        }
        float[] surface = (float[])sampled.Clone();

        var verified = new HashSet<int>();
        HashSet<int> round = NavmeshReachability.NeedsConfirmation(flag, StepOffset, surface, known,
            new float[faces], new bool[faces], margin: 0.05f);
        int rounds = 0;
        while (round.Count > 0)
        {
            foreach (int t in round)
                surface[t] = truth[t];
            verified.UnionWith(round);
            round = NavmeshReachability.UnverifiedDrops(flag, StepOffset, surface, known, verified);
            Assert.True(++rounds < 50, "the confirmation loop must converge");
        }

        Assert.NotEmpty(NavmeshReachability.Unreachable(flag, StepOffset, surface, known));
        Assert.Empty(NavmeshReachability.UnverifiedDrops(flag, StepOffset, surface, known, verified));
        Assert.True(verified.Count < faces, "the whole point is that this stops short of every face");
    }

    [Fact]
    public void ConfirmationIsWhatMakesTheDropSetExact()
    {
        // Re-probing the named faces against the truth and leaving the rest as the field sampled them
        // has to reproduce the verdict a full probe of every face would have reached. This is the
        // property the whole split rests on.
        NavFlag flag = FlatGrid(10);
        CollisionField field = SillWorld(10);
        NavmeshReachability.SurfaceProbe truth = AnalyticSill();

        NavmeshSurfaceSampling.FlagSurfaces sampled =
            NavmeshSurfaceSampling.Sample(flag, field, StepOffset);
        HashSet<int> confirm = NavmeshReachability.NeedsConfirmation(flag, StepOffset,
            sampled.Clearance,
            sampled.Known, sampled.Slack, sampled.Uncertain, margin: 0.05f);

        float[] surface = sampled.Clearance;
        bool[] known = sampled.Known;
        foreach (int t in confirm)
        {
            Vector3 a = flag.Vertices[flag.Triangles[t * 3]];
            Vector3 b = flag.Vertices[flag.Triangles[(t * 3) + 1]];
            Vector3 c = flag.Vertices[flag.Triangles[(t * 3) + 2]];
            float highestClearance = float.MinValue;
            var points = new Vector3[7];
            var heights = new float[7];
            int samples = 0;
            foreach (Vector3 point in NavmeshReachability.SamplePoints(a, b, c))
                if (truth(point, out float y))
                {
                    float sampleClearance = y - point.Y;
                    highestClearance = MathF.Max(highestClearance, sampleClearance);
                    points[samples] = point;
                    heights[samples++] = y;
                }
            known[t] = highestClearance != float.MinValue;
            surface[t] = known[t]
                ? NavmeshReachability.RequiredClimbForFace(a, b, c, StepOffset, highestClearance,
                    points.AsSpan(0, samples), heights.AsSpan(0, samples)) : 0f;
        }

        Assert.Equal(NavmeshReachability.Unreachable(flag, StepOffset, truth),
            NavmeshReachability.Unreachable(flag, StepOffset, surface, known));
    }
}
