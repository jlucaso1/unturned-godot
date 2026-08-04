using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// CollisionField stands in for PhysicsDirectSpaceState3D during navmesh reconciliation, so what matters is
// that it answers the same downward probe the same way — and, where it cannot, that it says so instead of
// guessing. These cover both halves: the geometry, and the uncertainty reporting the escalation rests on.
public class CollisionFieldTests
{
    [Fact]
    public void BuilderRetainsEffectiveCollisionPoliciesUntilRelease()
    {
        var builder = new CollisionFieldBuilder();
        Guid fence = Guid.NewGuid();
        builder.RecordCollisionPolicy(Guid.Empty, 99u);
        builder.RecordCollisionPolicy(fence, CollisionLayers.MediumFurniture);
        builder.RecordCollisionPolicy(fence,
            CollisionLayers.World | CollisionLayers.MediumFurniture);

        IReadOnlyDictionary<Guid, uint> snapshot = builder.CollisionPolicies;
        Assert.Single(snapshot);
        Assert.Equal(CollisionLayers.World | CollisionLayers.MediumFurniture, snapshot[fence]);
        builder.Release();
        Assert.Empty(builder.CollisionPolicies);
        Assert.Single(snapshot); // the fingerprint snapshot is not mutated behind the worker's back
    }

    private static Transform3D Grid(Vector3 origin, float cell) =>
        new(Basis.Identity.Scaled(new Vector3(cell, 1f, cell)), origin);

    // A flat heightfield covering [0, size] on both axes, at height `height`.
    private static void AddFlatGround(CollisionFieldBuilder builder, int cells, float cell, float height)
    {
        int res = cells + 1;
        var data = new float[res * res];
        Array.Fill(data, height);
        float half = cells * cell * 0.5f;
        builder.AddHeightfield(Grid(new Vector3(half, 0f, half), cell), res, res, data);
    }

    [Fact]
    public void FlatHeightfield_ReportsItsHeightExactlyAndWithoutDoubt()
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 10, cell: 1f, height: 4f);
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(3.3f, 7.1f, topY: 10f, bottomY: -10f);

        Assert.True(sample.Hit);
        Assert.Equal(4f, sample.Y, 0.0001f);
        Assert.Equal(0f, sample.Slack, 0.0001f);
        Assert.False(sample.Uncertain);
    }

    [Fact]
    public void AHeightfieldPlacedHighSitsThatMuchHigher()
    {
        // The samples are heights in the shape's own frame, so the placement's Y is part of where the
        // surface actually is — the tile's bounds already say so, and the sampled height has to agree
        // with them or every hit on that tile comes back short by the offset.
        var builder = new CollisionFieldBuilder();
        var data = new float[4];
        Array.Fill(data, 3f);
        builder.AddHeightfield(
            new Transform3D(Basis.Identity.Scaled(new Vector3(1f, 1f, 1f)), new Vector3(0f, 12f, 0f)),
            2, 2, data);
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(0f, 0f, topY: 20f, bottomY: 0f);
        Assert.True(sample.Hit);
        Assert.Equal(15f, sample.Y, 0.0001f);
    }

    [Fact]
    public void PlanarHeightfieldCell_InterpolatesWithoutSlack()
    {
        // A plane is the one surface both triangulations of a cell agree on everywhere, so the sampler
        // must report it exactly and claim no uncertainty about it.
        var builder = new CollisionFieldBuilder();
        var data = new float[4];
        for (int d = 0; d < 2; d++)
            for (int w = 0; w < 2; w++)
                data[(d * 2) + w] = w + (2 * d);
        builder.AddHeightfield(Grid(Vector3.Zero, 1f), 2, 2, data);
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(-0.25f, 0.1f, topY: 10f, bottomY: -10f);

        // Local (w, d) = (0.25, 0.6) on the plane h = w + 2d.
        Assert.Equal(0.25f + 1.2f, sample.Y, 0.0001f);
        Assert.Equal(0f, sample.Slack, 0.0001f);
        Assert.False(sample.Uncertain);
    }

    [Fact]
    public void TwistedHeightfieldCell_ReportsTheTallerDiagonalAndTheGapAsSlack()
    {
        // Two opposite corners high, two low: the cell is a saddle and its two triangulations genuinely
        // differ. Godot's heightfield picks one; this reports the taller and hands the difference back as
        // slack so the caller can widen its confirmation margin by exactly that much.
        var builder = new CollisionFieldBuilder();
        var data = new float[4];
        data[0] = 2f;  // (w0, d0)
        data[1] = 0f;  // (w1, d0)
        data[2] = 0f;  // (w0, d1)
        data[3] = 2f;  // (w1, d1)
        builder.AddHeightfield(Grid(Vector3.Zero, 1f), 2, 2, data);
        CollisionField field = builder.Build();

        // The cell's centre is on both diagonals at once, and they disagree by the saddle's whole depth:
        // one triangulation runs the ridge across it at 2, the other the trough at 0.
        SurfaceSample centre = field.Probe(0f, 0f, topY: 10f, bottomY: -10f);
        Assert.Equal(2f, centre.Y, 0.0001f);
        Assert.Equal(2f, centre.Slack, 0.0001f);

        // A corner is a corner under either triangulation, so nothing is in doubt there.
        SurfaceSample corner = field.Probe(-0.5f, -0.5f, topY: 10f, bottomY: -10f);
        Assert.Equal(2f, corner.Y, 0.0001f);
        Assert.Equal(0f, corner.Slack, 0.0001f);
    }

    [Fact]
    public void AColumnNobodyRecorded_IsUncertainRatherThanEmpty()
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 4, cell: 1f, height: 0f);
        CollisionField field = builder.Build();

        SurfaceSample offMap = field.Probe(100f, 100f, topY: 10f, bottomY: -10f);
        Assert.False(offMap.Hit);
        Assert.True(offMap.Uncertain);
    }

    [Fact]
    public void OverGroundThisFieldModels_FindingNothingIsAnAnswer()
    {
        // Every body a navmesh probe can hit is recorded as the load builds it, so a probe that finds
        // nothing between its two heights over modelled ground has been answered, not defeated — and
        // escalating those to the physics server is most of what the old path spent its time on.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 4, cell: 1f, height: 0f);
        CollisionField field = builder.Build();

        SurfaceSample outOfRange = field.Probe(1f, 1f, topY: 10f, bottomY: 5f);
        Assert.False(outOfRange.Hit);
        Assert.False(outOfRange.Uncertain);
    }

    [Fact]
    public void BoxTop_IsFoundThroughItsPlacement()
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 20, cell: 1f, height: 0f);
        int box = builder.AddBoxShape(new Vector3(2f, 1f, 2f));
        builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(5f, 1.5f, 5f)));
        CollisionField field = builder.Build();

        SurfaceSample onBox = field.Probe(5f, 5f, topY: 6f, bottomY: -1f);
        Assert.True(onBox.Hit);
        Assert.Equal(2f, onBox.Y, 0.0001f);
        Assert.False(onBox.Uncertain);

        SurfaceSample besideBox = field.Probe(8f, 5f, topY: 6f, bottomY: -1f);
        Assert.Equal(0f, besideBox.Y, 0.0001f);
        Assert.False(besideBox.Uncertain);
    }

    [Fact]
    public void RotatedBox_IsProbedInItsOwnFrame()
    {
        var builder = new CollisionFieldBuilder();
        int box = builder.AddBoxShape(new Vector3(2f, 2f, 2f));
        var basis = new Basis(new Quaternion(Vector3.Right, Mathf.Pi / 4f));
        builder.AddInstance(box, new Transform3D(basis, new Vector3(0f, 3f, 0f)));
        CollisionField field = builder.Build();

        // A unit cube on edge reaches sqrt(2) above its centre.
        SurfaceSample sample = field.Probe(0f, 0f, topY: 10f, bottomY: 0f);
        Assert.True(sample.Hit);
        Assert.Equal(3f + MathF.Sqrt(2f), sample.Y, 0.001f);
    }

    [Fact]
    public void ProbeStartingInsideAShape_FindsNoCrossing_LikeTheServerWithHitFromInsideOff()
    {
        var builder = new CollisionFieldBuilder();
        int box = builder.AddBoxShape(new Vector3(4f, 4f, 4f));
        builder.AddInstance(box, new Transform3D(Basis.Identity, Vector3.Zero));
        CollisionField field = builder.Build();

        SurfaceSample inside = field.Probe(0f, 0f, topY: 1f, bottomY: -1f);
        Assert.False(inside.Hit);
    }

    [Theory]
    [InlineData(0f, false)]          // through the middle of the top face
    [InlineData(0.995f, true)]       // a hair inside the silhouette
    [InlineData(1.005f, true)]       // a hair outside it
    [InlineData(1.2f, false)]        // clear of it
    public void ProbesNearASilhouette_AreEscalatedRatherThanDecided(float offset, bool expectUncertain)
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 20, cell: 1f, height: 0f);
        int box = builder.AddBoxShape(new Vector3(2f, 2f, 2f));
        builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(10f, 3f, 10f)));
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(10f + offset, 10f, topY: 8f, bottomY: -1f);

        Assert.Equal(expectUncertain, sample.Uncertain);
    }

    [Fact]
    public void SphereAndCapsuleApexes_AreFound()
    {
        var builder = new CollisionFieldBuilder();
        int sphere = builder.AddSphereShape(1.5f);
        builder.AddInstance(sphere, new Transform3D(Basis.Identity, new Vector3(0f, 4f, 0f)));
        int capsule = builder.AddCapsuleShape(0.5f, 3f);
        builder.AddInstance(capsule, new Transform3D(Basis.Identity, new Vector3(20f, 4f, 0f)));
        CollisionField field = builder.Build();

        Assert.Equal(5.5f, field.Probe(0f, 0f, 20f, 0f).Y, 0.001f);
        Assert.Equal(5.5f, field.Probe(20f, 0f, 20f, 0f).Y, 0.001f);
        // Off-axis, the upper cap is what the probe lands on: the cap's centre is a metre above the
        // capsule's own, so the surface only drops by the sphere's own curvature.
        SurfaceSample offAxis = field.Probe(20.3f, 0f, 20f, 0f);
        Assert.Equal(5f + MathF.Sqrt(0.25f - 0.09f), offAxis.Y, 0.001f);
    }

    // A flat quad of two coplanar triangles sharing the (0,0)-(10,10) diagonal, spanning [0, 10] x
    // [0, 10] at `height`, wound so its normal points up — a surface a downward probe meets front-on,
    // like the roof of a real collider.
    private static Vector3[] Quad(float height) => new[]
    {
        new Vector3(0f, height, 0f), new Vector3(10f, height, 10f), new Vector3(10f, height, 0f),
        new Vector3(0f, height, 0f), new Vector3(0f, height, 10f), new Vector3(10f, height, 10f),
    };

    [Fact]
    public void ProbeInsideATessellatedFace_IsNotCalledUncertainByItsOwnDiagonal()
    {
        // The regression this whole classification exists for. A collision mesh is a shell of triangles,
        // so a probe near the diagonal shared by two coplanar ones grazes a triangle boundary — but the
        // surface there is not in doubt at all, because the neighbour is hit squarely at the same height.
        var builder = new CollisionFieldBuilder();
        int quad = builder.AddMeshShape(Quad(5f));
        builder.AddInstance(quad, Transform3D.Identity);
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(5f, 4f, topY: 10f, bottomY: 0f);
        Assert.True(sample.Hit);
        Assert.Equal(5f, sample.Y, 0.0001f);
        Assert.False(sample.Uncertain);
    }

    [Fact]
    public void TheBandAroundAnInternalEdgeIsCorroborated_NotEscalated()
    {
        // The diagonal is not a special case — every shared edge of a tessellated collider has a band
        // around it, and on a triangle-heavy mesh that band is a large share of the surface. A probe
        // inside it crosses BOTH faces at the same height, and a surface two faces agree on is not a
        // silhouette; escalating those would push ordinary samples down the slow path in bulk.
        var builder = new CollisionFieldBuilder();
        int quad = builder.AddMeshShape(Quad(5f));
        builder.AddInstance(quad, Transform3D.Identity);
        CollisionField field = builder.Build();

        foreach (float offset in new[] { 0f, 0.001f, -0.001f, 0.01f, -0.01f })
        {
            SurfaceSample sample = field.Probe(5f + offset, 5f, topY: 10f, bottomY: 0f);
            Assert.True(sample.Hit);
            Assert.Equal(5f, sample.Y, 0.0001f);
            Assert.False(sample.Uncertain, $"the shared diagonal is not a silhouette (offset {offset})");
        }
    }

    [Fact]
    public void ASliverTriangleStillGetsAMetricEdgeBand()
    {
        // The band is metric — 2 cm — and a barycentric coordinate is a fraction of the triangle, so the
        // conversion is per edge, by that edge's altitude. Scaling all three by the longest side instead
        // understates the band along the long edge of a sliver by its aspect ratio, and a collision mesh
        // is mostly slivers: a probe a millimetre off the edge would come back a certain miss.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 40, cell: 1f, height: 0f); // so a clean miss is an answer, not a gap
        int sliver = builder.AddMeshShape(new[]
        {
            new Vector3(0f, 5f, 0f), new Vector3(20f, 5f, 0.05f), new Vector3(20f, 5f, 0f),
        });
        // Placed well inside the ground, so a miss beside it still lands on modelled terrain.
        builder.AddInstance(sliver, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 5f)));
        CollisionField field = builder.Build();

        // A centimetre beyond the long edge: inside the band, so the answer is the server's to give.
        Assert.True(field.Probe(10f, 4.99f, topY: 10f, bottomY: -1f).Uncertain);
        // A whole metre away is not a near miss by anyone's measure — that is the ground, and only the
        // ground.
        SurfaceSample clear = field.Probe(10f, 4f, topY: 10f, bottomY: -1f);
        Assert.False(clear.Uncertain);
        Assert.Equal(0f, clear.Y, 0.0001f);
    }

    [Fact]
    public void AFaceTurnedAwayFromTheProbeIsStillASurface()
    {
        // ObjectsBuilder leaves BackfaceCollision off, so in principle the server culls a crossing of a
        // face turned away from the ray and this should escalate rather than report one. Measured on
        // PEI's 42,642 faces it does not: escalating back-facing crossings took the uncertain probes from
        // 842 to 99,525 — a third of every probe on the map — and the confirmation set from 8,054 faces
        // to 21,488 (19% to 50%), while the disagreement count stayed at 90 and the verdict differences
        // went 19 to 15. A third of crossings being back-facing while the server reports the same
        // surfaces says the server is not culling them here. The audit is what would catch it if that
        // ever changed; see the note in CollisionField.Intersect.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 20, cell: 1f, height: 0f);
        Vector3[] facingUp = Quad(5f);
        var facingDown = new Vector3[facingUp.Length];
        for (int i = 0; i < facingUp.Length; i += 3)
        {
            facingDown[i] = facingUp[i + 2]; // the same triangle, wound the other way
            facingDown[i + 1] = facingUp[i + 1];
            facingDown[i + 2] = facingUp[i];
        }
        int up = builder.AddMeshShape(facingUp);
        int down = builder.AddMeshShape(facingDown);
        builder.AddInstance(up, Transform3D.Identity);
        builder.AddInstance(down, new Transform3D(Basis.Identity, new Vector3(0f, 0f, 12f)));
        CollisionField field = builder.Build();

        foreach (float z in new[] { 4f, 16f })
        {
            SurfaceSample sample = field.Probe(5f, z, topY: 10f, bottomY: -1f);
            Assert.Equal(5f, sample.Y, 0.0001f);
            Assert.False(sample.Uncertain, $"winding does not decide whether a surface is there (z={z})");
        }
    }

    [Fact]
    public void ProbeOnTheOuterEdgeOfAMesh_IsEscalated()
    {
        var builder = new CollisionFieldBuilder();
        int quad = builder.AddMeshShape(Quad(5f));
        builder.AddInstance(quad, Transform3D.Identity);
        CollisionField field = builder.Build();

        Assert.True(field.Probe(9.995f, 4f, topY: 10f, bottomY: 0f).Uncertain);
        Assert.False(field.Probe(9.5f, 4f, topY: 10f, bottomY: 0f).Uncertain);
    }

    [Fact]
    public void TheHighestSurfaceWins_AcrossKinds()
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 20, cell: 1f, height: 0f);
        int quad = builder.AddMeshShape(Quad(2f));
        builder.AddInstance(quad, Transform3D.Identity);
        int box = builder.AddBoxShape(new Vector3(4f, 1f, 4f));
        builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(5f, 3.5f, 5f)));
        CollisionField field = builder.Build();

        Assert.Equal(4f, field.Probe(5f, 5f, topY: 20f, bottomY: -1f).Y, 0.0001f);
        Assert.Equal(2f, field.Probe(8f, 4f, topY: 20f, bottomY: -1f).Y, 0.0001f);
        Assert.Equal(0f, field.Probe(15f, 15f, topY: 20f, bottomY: -1f).Y, 0.0001f);
    }

    [Fact]
    public void TheBroadphaseFindsEveryInstance_HoweverFarApartTheyAre()
    {
        // The instance index exists so a probe does not walk every collider on the map. Anything it drops
        // is a hole in the reconciled navmesh, so it is checked against the brute-force answer.
        var random = new Random(20260803);
        var builder = new CollisionFieldBuilder();
        int box = builder.AddBoxShape(new Vector3(3f, 3f, 3f));
        var placements = new List<Vector3>();
        for (int i = 0; i < 400; i++)
        {
            var origin = new Vector3(
                (float)((random.NextDouble() * 4000.0) - 2000.0),
                (float)(random.NextDouble() * 20.0),
                (float)((random.NextDouble() * 4000.0) - 2000.0));
            placements.Add(origin);
            builder.AddInstance(box, new Transform3D(Basis.Identity, origin));
        }
        // One collider spanning far more of the map than a broadphase cell, so the oversized path is used.
        int slab = builder.AddBoxShape(new Vector3(6000f, 1f, 6000f));
        builder.AddInstance(slab, new Transform3D(Basis.Identity, new Vector3(0f, -50f, 0f)));
        CollisionField field = builder.Build();

        for (int probe = 0; probe < 2000; probe++)
        {
            float x = (float)((random.NextDouble() * 4200.0) - 2100.0);
            float z = (float)((random.NextDouble() * 4200.0) - 2100.0);
            float expected = float.MinValue;
            foreach (Vector3 origin in placements)
                if (MathF.Abs(x - origin.X) <= 1.5f && MathF.Abs(z - origin.Z) <= 1.5f)
                    expected = MathF.Max(expected, origin.Y + 1.5f);
            if (MathF.Abs(x) <= 3000f && MathF.Abs(z) <= 3000f)
                expected = MathF.Max(expected, -49.5f);

            SurfaceSample sample = field.Probe(x, z, topY: 100f, bottomY: -100f);
            Assert.Equal(expected != float.MinValue, sample.Hit);
            if (sample.Hit)
                Assert.Equal(expected, sample.Y, 0.001f);
        }
    }

    [Fact]
    public void TheBvhAgreesWithBruteForceOverADenseMesh()
    {
        // The per-collider BVH is the only part of the traversal that can quietly skip geometry, and a
        // skipped triangle is a surface the reconciliation never sees. Checked against every triangle.
        var random = new Random(7);
        const int Cells = 24;
        var faces = new List<Vector3>();
        var heights = new float[Cells + 1, Cells + 1];
        for (int i = 0; i <= Cells; i++)
            for (int j = 0; j <= Cells; j++)
                heights[i, j] = (float)(random.NextDouble() * 6.0);
        for (int i = 0; i < Cells; i++)
            for (int j = 0; j < Cells; j++)
            {
                Vector3 a = new(i, heights[i, j], j);
                Vector3 b = new(i + 1, heights[i + 1, j], j);
                Vector3 c = new(i + 1, heights[i + 1, j + 1], j + 1);
                Vector3 d = new(i, heights[i, j + 1], j + 1);
                faces.Add(a); faces.Add(b); faces.Add(c);
                faces.Add(a); faces.Add(c); faces.Add(d);
            }

        var builder = new CollisionFieldBuilder();
        int shape = builder.AddMeshShape(faces.ToArray());
        builder.AddInstance(shape, Transform3D.Identity);
        CollisionField field = builder.Build();

        for (int probe = 0; probe < 3000; probe++)
        {
            float x = (float)(random.NextDouble() * Cells);
            float z = (float)(random.NextDouble() * Cells);
            float expected = float.MinValue;
            for (int f = 0; f < faces.Count; f += 3)
                if (VerticalHit(faces[f], faces[f + 1], faces[f + 2], x, z, out float y))
                    expected = MathF.Max(expected, y);

            SurfaceSample sample = field.Probe(x, z, topY: 50f, bottomY: -50f);
            Assert.True(sample.Hit);
            Assert.Equal(expected, sample.Y, 0.002f);
        }
    }

    // Straightforward point-in-triangle in XZ plus a barycentric height: the reference the BVH is judged
    // against, deliberately written a different way from the traversal it checks.
    private static bool VerticalHit(Vector3 a, Vector3 b, Vector3 c, float x, float z, out float y)
    {
        y = 0f;
        float area = ((b.X - a.X) * (c.Z - a.Z)) - ((c.X - a.X) * (b.Z - a.Z));
        if (MathF.Abs(area) < 1e-9f)
            return false;
        float u = (((b.X - x) * (c.Z - z)) - ((c.X - x) * (b.Z - z))) / area;
        float v = (((c.X - x) * (a.Z - z)) - ((a.X - x) * (c.Z - z))) / area;
        float w = 1f - u - v;
        if (u < 0f || v < 0f || w < 0f)
            return false;
        y = (u * a.Y) + (v * b.Y) + (w * c.Y);
        return true;
    }

    [Fact]
    public void ShapesNothingPlaces_AreDroppedRatherThanCarried()
    {
        var builder = new CollisionFieldBuilder();
        int used = builder.AddBoxShape(Vector3.One);
        builder.AddSphereShape(3f); // a MEDIUM-furniture shape the navmesh probe never sees
        builder.AddInstance(used, Transform3D.Identity);
        CollisionField field = builder.Build();

        Assert.Equal(2, field.ShapeCount);
        Assert.Equal(1, field.InstanceCount);
        Assert.False(field.Probe(0f, 0f, 10f, 2f).Hit); // the unplaced sphere is not in the world
    }

    [Fact]
    public void HeightfieldPlacementsThatCannotBeSampledAreRefused()
    {
        var builder = new CollisionFieldBuilder();
        var data = new float[4];
        Assert.Throws<ArgumentException>(() => builder.AddHeightfield(Grid(Vector3.Zero, 1f), 2, 2,
            new float[3]));
        Assert.Throws<ArgumentException>(() => builder.AddHeightfield(Grid(Vector3.Zero, 1f), 1, 1,
            new float[1]));
        var rotated = new Transform3D(new Basis(new Quaternion(Vector3.Up, 0.4f)), Vector3.Zero);
        Assert.Throws<ArgumentException>(() => builder.AddHeightfield(rotated, 2, 2, data));
        var stretched = new Transform3D(Basis.Identity.Scaled(new Vector3(1f, 2f, 1f)), Vector3.Zero);
        Assert.Throws<ArgumentException>(() => builder.AddHeightfield(stretched, 2, 2, data));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddInstance(0, Transform3D.Identity));
    }

    [Fact]
    public void GroundOutsideTheProbedSegment_IsAnAnswerRatherThanIgnorance()
    {
        // Reconciliation probes a bounded segment around each navmesh face, so most of the map's terrain is
        // out of range of any given probe. Being out of range is not the same as being unrecorded: the
        // column is still modelled, so "nothing here" is a verdict the server does not need to re-check.
        var above = new CollisionFieldBuilder();
        AddFlatGround(above, cells: 4, cell: 1f, height: 40f); // whole tile above the segment
        SurfaceSample high = above.Build().Probe(2f, 2f, topY: 2f, bottomY: 0f);
        Assert.False(high.Hit);
        Assert.False(high.Uncertain);

        var below = new CollisionFieldBuilder();
        AddFlatGround(below, cells: 4, cell: 1f, height: -40f); // whole tile below it
        SurfaceSample low = below.Build().Probe(2f, 2f, topY: 2f, bottomY: 0f);
        Assert.False(low.Hit);
        Assert.False(low.Uncertain);

        // A cliff spans the segment, so the tile's range cannot rule it out and each column has to be
        // sampled and then judged on its own height.
        var cliff = new CollisionFieldBuilder();
        var data = new float[] { -20f, -20f, 20f, 20f };
        cliff.AddHeightfield(Grid(new Vector3(1f, 0f, 1f), 2f), 2, 2, data);
        CollisionField field = cliff.Build();
        Assert.False(field.Probe(0.1f, 0.1f, topY: 2f, bottomY: 0f).Hit);  // sampled below the segment
        Assert.False(field.Probe(0.1f, 1.9f, topY: 2f, bottomY: 0f).Hit);  // sampled above it
        Assert.True(field.Probe(0.1f, 1f, topY: 2f, bottomY: -2f).Hit);    // and through it in between
    }

    [Fact]
    public void SpheresOutsideTheSegment_DoNotAnswerForIt()
    {
        // The broadphase pads by the graze band, so a body sitting just outside the probed segment is still
        // handed to the ray test and has to be rejected there — once for starting past it, once for
        // sitting behind the ray's origin.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 4, cell: 1f, height: 0f);
        int sphere = builder.AddSphereShape(1f);
        builder.AddInstance(sphere, new Transform3D(Basis.Identity, new Vector3(1f, 5f, 1f)));
        builder.AddInstance(sphere, new Transform3D(Basis.Identity, new Vector3(3f, -5f, 3f)));
        CollisionField field = builder.Build();

        // topY sits just under the upper sphere: it is inside the padded bounds, but the ray points away.
        SurfaceSample overhead = field.Probe(1f, 1f, topY: 3.99f, bottomY: 0f);
        Assert.True(overhead.Hit);
        Assert.Equal(0f, overhead.Y, 0.0001f); // the ground, not the sphere overhead

        // bottomY stops just above the lower sphere, so its entry lies past the end of the segment.
        SurfaceSample underfoot = field.Probe(3f, 3f, topY: -3.5f, bottomY: -3.99f);
        Assert.False(underfoot.Hit);
    }

    [Fact]
    public void ATwistedCellWhoseOtherDiagonalIsInRange_IsAQuestionNotAMiss()
    {
        // A saddle cell has two legal triangulations and Godot picked one when it built the shape. The
        // sampler reports the taller and the gap between them, which the slack carries downstream — but
        // only while the taller is in range. Out of range it used to be discarded outright, and with it
        // the fact that the shorter one is still sitting inside the probed segment.
        var builder = new CollisionFieldBuilder();
        builder.AddHeightfield(Grid(new Vector3(0.5f, 0f, 0.5f), 1f), 2, 2, new[] { 0f, 4f, 4f, 0f });
        CollisionField field = builder.Build();

        // Here the two triangulations give 4 m and 2 m.
        SurfaceSample both = field.Probe(0.75f, 0.25f, topY: 5f, bottomY: 0f);
        Assert.True(both.Hit);
        Assert.Equal(4f, both.Y, 0.0001f);
        Assert.Equal(2f, both.Slack, 0.0001f);

        // The taller is above the segment and the shorter inside it: whether this column has a surface
        // depends on which triangulation the collision shape got, so it is the server's to answer.
        SurfaceSample split = field.Probe(0.75f, 0.25f, topY: 3f, bottomY: 1f);
        Assert.False(split.Hit);
        Assert.True(split.Uncertain);

        // Both above the segment: neither triangulation can reach it, and that is a clean answer.
        SurfaceSample clear = field.Probe(0.75f, 0.25f, topY: 1.5f, bottomY: 0.5f);
        Assert.False(clear.Hit);
        Assert.False(clear.Uncertain);
    }

    [Fact]
    public void ACapsuleIsMissedCleanlyBesideIt_AndAboveAndBelowItsEnds()
    {
        // A lying capsule is the one shape where the ray can miss three separate ways, and each has to
        // come back as a clean miss rather than as a doubt the server pays for.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 8, cell: 1f, height: 0f);
        int rail = builder.AddCapsuleShape(radius: 0.25f, height: 4f);
        // Laid along X at y = 2, so its axis runs from x = 2 to x = 6 through z = 4.
        var lying = new Transform3D(new Basis(new Quaternion(Vector3.Back, MathF.PI / 2f)),
            new Vector3(4f, 2f, 4f));
        builder.AddInstance(rail, lying);
        CollisionField field = builder.Build();

        // Squarely over the middle, the answer is the top of the cylinder rather than its axis.
        Assert.Equal(2.25f, field.Probe(4f, 4f, topY: 5f, bottomY: 0.5f).Y, 0.01f);

        // Just beside the rail in Z: the ray never reaches the cylinder's radius, but it passes close
        // enough that which side of the surface it fell on is the collision engine's call, not ours.
        SurfaceSample beside = field.Probe(4f, 4.26f, topY: 5f, bottomY: 0.5f);
        Assert.False(beside.Hit);
        Assert.True(beside.Uncertain);

        // Past the end caps along the axis: the cylinder's solution is beyond the segment the caps close
        // off, and neither cap is in the way either.
        SurfaceSample past = field.Probe(6.01f, 4f, topY: 5f, bottomY: 0.5f);
        Assert.False(past.Hit);

        // Over the rail but stopping a centimetre short of it, so the entry lies past the end of the ray.
        SurfaceSample stopsShort = field.Probe(4f, 4f, topY: 5f, bottomY: 2.26f);
        Assert.False(stopsShort.Hit);
    }

    [Fact]
    public void ADegenerateMeshTriangleIsNothingToStandOn()
    {
        // Collision meshes carry collapsed triangles. One has no area, so it has no altitude to measure a
        // graze band against and no surface to report — it must drop out rather than divide by zero and
        // hand the whole column to the server.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 4, cell: 1f, height: 0f);
        int mesh = builder.AddMeshShape(new[]
        {
            new Vector3(1f, 2f, 1f), new Vector3(3f, 2f, 1f), new Vector3(2f, 2f, 1f), // collinear
        });
        builder.AddInstance(mesh, Transform3D.Identity);
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(2f, 1f, topY: 5f, bottomY: -1f);
        Assert.True(sample.Hit);
        Assert.Equal(0f, sample.Y, 0.0001f); // the ground below it, never the sliver
        Assert.False(sample.Uncertain);
    }

    [Fact]
    public void ScaledInstancePlacementsAreRefused()
    {
        // ProbeInstances rotates the ray into shape space and reads the returned parameter back as a world
        // distance, which only holds while the basis is rigid. A scaled placement would answer with a
        // plausible height in the wrong units and no uncertainty flag, so it is refused at the door.
        var builder = new CollisionFieldBuilder();
        int shape = builder.AddSphereShape(1f);
        foreach (Vector3 scale in new[]
        {
            new Vector3(1.5f, 1f, 1f), new Vector3(1f, 1.5f, 1f), new Vector3(1f, 1f, 1.5f),
        })
        {
            var scaled = new Transform3D(Basis.Identity.Scaled(scale), Vector3.Zero);
            Assert.Throws<ArgumentException>(() => builder.AddInstance(shape, scaled));
        }

        // Rotation alone is rigid, and the tolerance absorbs the drift of a basis built from a quaternion.
        var turned = new Transform3D(new Basis(new Quaternion(Vector3.Up, 0.7f)), new Vector3(3f, 0f, 0f));
        builder.AddInstance(shape, turned);
        Assert.Equal(1, builder.Build().InstanceCount);
    }

    [Fact]
    public void ACapsuleLyingDown_IsProbedThroughItsCylinder()
    {
        // Every capsule in the tests above stands on its axis, so a downward probe runs parallel to it and
        // only ever meets the caps. A fence rail lies across the ray instead, and it is the cylinder that
        // has to answer.
        var builder = new CollisionFieldBuilder();
        int rail = builder.AddCapsuleShape(0.5f, 6f);
        var lying = new Basis(new Quaternion(Vector3.Right, Mathf.Pi / 2f)); // Y axis -> Z
        builder.AddInstance(rail, new Transform3D(lying, new Vector3(0f, 3f, 0f)));
        CollisionField field = builder.Build();

        // Over the middle of the rail: the cylinder's own apex, half a metre above its axis.
        Assert.Equal(3.5f, field.Probe(0f, 0f, 10f, 0f).Y, 0.001f);
        // Half the radius off the axis: the cylinder curves away by the same circle as a sphere would.
        Assert.Equal(3f + MathF.Sqrt(0.25f - 0.0625f), field.Probe(0.25f, 1f, 10f, 0f).Y, 0.001f);
        // Past the rail's end the caps take over, and past those there is nothing.
        Assert.True(field.Probe(0f, 2.9f, 10f, 0f).Hit);
        Assert.False(field.Probe(0f, 3.6f, 10f, 0f).Hit);
        // Beside it, still inside its bounding box: a miss, and one it is sure of.
        SurfaceSample beside = field.Probe(0.9f, 0f, 10f, 0f);
        Assert.False(beside.Hit);
    }

    [Fact]
    public void ProbesThatMissAShapeInsideItsOwnBoundingBox_AreMisses()
    {
        // A rotated box fills less than half of the box its bounds describe, so the corners of those
        // bounds are where a bad slab test would invent a surface.
        var builder = new CollisionFieldBuilder();
        int box = builder.AddBoxShape(new Vector3(2f, 2f, 2f));
        var turned = new Basis(new Quaternion(Vector3.Up, Mathf.Pi / 4f));
        builder.AddInstance(box, new Transform3D(turned, new Vector3(0f, 5f, 0f)));
        int sphere = builder.AddSphereShape(1f);
        builder.AddInstance(sphere, new Transform3D(Basis.Identity, new Vector3(20f, 5f, 0f)));
        CollisionField field = builder.Build();

        Assert.True(field.Probe(0f, 0f, 10f, 0f).Hit);          // through the middle
        Assert.False(field.Probe(1.3f, 1.3f, 10f, 0f).Hit);     // the corner of the bounds, past the box
        Assert.True(field.Probe(20f, 0f, 10f, 0f).Hit);         // through the sphere
        Assert.False(field.Probe(20.9f, 0.9f, 10f, 0f).Hit);    // the corner of its bounds
    }

    [Fact]
    public void AProbeWhollyAboveOrBelowAShape_FindsNothing()
    {
        var builder = new CollisionFieldBuilder();
        int box = builder.AddBoxShape(new Vector3(2f, 2f, 2f));
        builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(0f, 5f, 0f)));
        int sphere = builder.AddSphereShape(1f);
        builder.AddInstance(sphere, new Transform3D(Basis.Identity, new Vector3(0f, 5f, 0f)));
        CollisionField field = builder.Build();

        // The segment stops above the shapes, so the ray never reaches them.
        Assert.False(field.Probe(0f, 0f, 12f, 8f).Hit);
        // Pointing away: the segment lies entirely under them.
        Assert.False(field.Probe(0f, 0f, 3f, 0f).Hit);
    }

    [Fact]
    public void MeshTrianglesTheRayCannotMeet_ContributeNothing()
    {
        // Three shapes of triangle a vertical probe has to survive: one standing edge-on to the ray (no
        // crossing to compute at all), one whose crossing is outside the segment, and one small enough
        // that the graze band swallows the whole face.
        var builder = new CollisionFieldBuilder();
        // Ground under all of it, well below: an uncovered column is uncertain on its own, which would
        // let every assertion below hold without the triangles having said anything.
        AddFlatGround(builder, cells: 40, cell: 1f, height: -5f);
        int mesh = builder.AddMeshShape(new[]
        {
            // Edge-on: a vertical wall, parallel to the probe.
            new Vector3(0f, 0f, 0f), new Vector3(0f, 8f, 0f), new Vector3(0f, 8f, 4f),
            // Well above any segment used below.
            new Vector3(-2f, 40f, -2f), new Vector3(2f, 40f, -2f), new Vector3(0f, 40f, 2f),
            // Smaller than the graze band in every direction.
            new Vector3(10f, 5f, 10f), new Vector3(10.004f, 5f, 10f), new Vector3(10f, 5f, 10.004f),
        });
        builder.AddInstance(mesh, Transform3D.Identity);
        CollisionField field = builder.Build();

        // Segments stop above the ground, so a miss is the triangles' answer and not the terrain's.
        SurfaceSample alongWall = field.Probe(0.5f, 2f, topY: 6f, bottomY: 0f);
        Assert.False(alongWall.Hit);
        Assert.False(alongWall.Uncertain); // modelled ground below, nothing in range: an answer
        Assert.False(field.Probe(4f, 4f, topY: 30f, bottomY: 0f).Hit); // the high triangle is out of reach
        Assert.True(field.Probe(10.001f, 10.001f, topY: 8f, bottomY: 0f).Uncertain); // all graze, no solid
    }

    [Fact]
    public void ADegenerateMeshIsRefusedRatherThanIndexed()
    {
        var builder = new CollisionFieldBuilder();
        int empty = builder.AddMeshShape(Array.Empty<Vector3>());
        int unsupported = builder.AddUnsupportedShape();
        builder.AddInstance(empty, Transform3D.Identity);
        builder.AddInstance(unsupported, Transform3D.Identity);
        CollisionField field = builder.Build();

        Assert.Equal(2, field.ShapeCount);
        Assert.Equal(0, field.InstanceCount); // neither models any surface, so neither is placed
        Assert.Equal(0, field.TriangleCount);
        Assert.False(field.Probe(0f, 0f, 10f, -10f).Hit);
    }

    [Fact]
    public void TheBuilderReportsWhatItHasCollected_AndReleasesIt()
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 4, cell: 1f, height: 0f);
        int box = builder.AddBoxShape(Vector3.One);
        builder.AddInstance(box, Transform3D.Identity);
        Assert.Equal(1, builder.ShapeCount);
        Assert.Equal(1, builder.InstanceCount);
        Assert.Equal(1, builder.TileCount);

        CollisionField field = builder.Build();
        Assert.Equal(1, field.TileCount);

        // Reconciliation is a one-shot pass; once the field has what it kept, the map's whole collision
        // geometry has no second consumer. The field goes on answering after the builder is emptied.
        builder.Release();
        Assert.Equal(0, builder.ShapeCount);
        Assert.Equal(0, builder.InstanceCount);
        Assert.Equal(0, builder.TileCount);
        Assert.True(field.Probe(1f, 1f, 10f, -10f).Hit);
    }

    [Fact]
    public void TheAnswerIsTheHighestSurface_WhateverOrderItIsFoundIn()
    {
        // The probe keeps a running best, so a candidate found after the winner has to leave it alone.
        // Recorded high-then-low, which is the order that exercises that.
        var builder = new CollisionFieldBuilder();
        var high = new float[4];
        Array.Fill(high, 6f);
        var low = new float[4];
        Array.Fill(low, 2f);
        builder.AddHeightfield(Grid(Vector3.Zero, 8f), 2, 2, high);
        builder.AddHeightfield(Grid(Vector3.Zero, 8f), 2, 2, low); // the same ground, lower
        int box = builder.AddBoxShape(new Vector3(4f, 1f, 4f));
        builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(0f, 9f, 0f)));
        builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(0f, 7f, 0f)));
        CollisionField field = builder.Build();

        SurfaceSample sample = field.Probe(0f, 0f, topY: 20f, bottomY: -5f);
        Assert.Equal(9.5f, sample.Y, 0.001f);
        Assert.False(sample.Uncertain);

        // With the boxes out of reach the taller ground still wins over the shorter.
        Assert.Equal(6f, field.Probe(0f, 0f, topY: 6.4f, bottomY: -5f).Y, 0.001f);
    }

    [Fact]
    public void ARayThatMissesATiltedBoxInsideItsBounds_IsAMiss()
    {
        // Tilted about X, so in the box's own frame the probe is a slanted ray and every slab has to be
        // crossed properly — the axis-aligned shortcut cannot answer this one.
        var builder = new CollisionFieldBuilder();
        int box = builder.AddBoxShape(new Vector3(2f, 2f, 8f));
        var tilted = new Basis(new Quaternion(Vector3.Right, Mathf.Pi / 4f));
        builder.AddInstance(box, new Transform3D(tilted, new Vector3(0f, 10f, 0f)));
        CollisionField field = builder.Build();

        Assert.True(field.Probe(0f, 0f, 20f, 0f).Hit);
        // Along the tilted length, past the end of the box but still inside its bounding box.
        Assert.False(field.Probe(0f, 4.4f, 20f, 16f).Hit);
    }

    [Fact]
    public void ASphereTheSegmentCannotReach_IsNotASurface()
    {
        var builder = new CollisionFieldBuilder();
        int sphere = builder.AddSphereShape(1f);
        builder.AddInstance(sphere, new Transform3D(Basis.Identity, new Vector3(0f, 5f, 0f)));
        CollisionField field = builder.Build();

        // The segment starts inside it: a ray that begins in a solid reports no crossing, which is what
        // PhysicsRayQueryParameters3D does with HitFromInside off.
        Assert.False(field.Probe(0f, 0f, 5f, 0f).Hit);
        // The segment starts below the middle and the sphere is above: nothing in that direction.
        Assert.False(field.Probe(0f, 0f, 4.5f, 0f).Hit);
    }

    [Fact]
    public void ARayStartingInsideACapsule_FindsNoSurfaceInsideIt()
    {
        // A capsule is three overlapping pieces, and a point inside its cylinder is outside both cap
        // spheres — so testing the pieces separately let a cap report the ray entering its own upper
        // hemisphere, buried inside the solid. Both the grown and the shrunk test agree on that invented
        // surface, so nothing would have marked it uncertain either.
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 20, cell: 1f, height: 0f);
        int capsule = builder.AddCapsuleShape(0.5f, 4f);
        builder.AddInstance(capsule, new Transform3D(Basis.Identity, new Vector3(5f, 5f, 5f)));
        CollisionField field = builder.Build();

        // From the capsule's own centre, straight down: inside the cylinder the whole way.
        SurfaceSample inside = field.Probe(5f, 5f, topY: 5f, bottomY: 4f);
        Assert.False(inside.Hit);

        // From inside the lower cap, likewise.
        Assert.False(field.Probe(5f, 5f, topY: 3.4f, bottomY: 3f).Hit);

        // And from above it, the capsule is still a surface.
        Assert.Equal(7f, field.Probe(5f, 5f, topY: 9f, bottomY: 0f).Y, 0.001f);
    }

    [Fact]
    public void TheFieldIsSafeToProbeFromManyThreadsAtOnce()
    {
        var builder = new CollisionFieldBuilder();
        AddFlatGround(builder, cells: 32, cell: 1f, height: 0f);
        int box = builder.AddBoxShape(new Vector3(2f, 2f, 2f));
        for (int i = 0; i < 64; i++)
            builder.AddInstance(box, new Transform3D(Basis.Identity, new Vector3(i / 8 * 4f, 1f,
                i % 8 * 4f)));
        CollisionField field = builder.Build();

        System.Threading.Tasks.Parallel.For(0, 4000, i =>
        {
            float x = i % 320 / 10f;
            float z = i % 317 / 10f;
            SurfaceSample sample = field.Probe(x, z, topY: 10f, bottomY: -5f);
            Assert.True(sample.Hit);
            Assert.InRange(sample.Y, -0.001f, 2.001f);
        });
    }
}
