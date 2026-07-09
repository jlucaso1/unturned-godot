using System;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// Ground truth throughout is Road.buildMesh (Road.cs in the game source): every expected value below is the
// source's formula evaluated by hand, not this port re-run against itself.
public class RoadMeshTests
{
    // PEI's material 0 shape: halfWidth 4, texture height 4... here depth 0.2 / offset 0 like the real file.
    private static readonly RoadMaterialConfig Config = new(4f, 4f, 0.2f, 0f, true);

    private sealed class FakeTerrain : IRoadTerrain
    {
        private readonly Func<Vector3, float> _height;
        private readonly Func<Vector3, Vector3> _normal;
        public int HeightCalls;

        public FakeTerrain(Func<Vector3, float>? height = null, Func<Vector3, Vector3>? normal = null)
        {
            _height = height ?? (_ => 10f);
            _normal = normal ?? (_ => Vector3.Up);
        }

        public float GetHeight(Vector3 position)
        {
            HeightCalls++;
            return _height(position);
        }

        public Vector3 GetNormal(Vector3 position) => _normal(position);
    }

    // A straight 12 m two-joint road with editor-style mirrored tangents. Those tangents are anti-parallel,
    // so getPosition falls back to the joint lerp (z = 12t) while getVelocity stays the Bezier derivative
    // (here the constant (0,0,12)) — exactly the source's split behavior on straight segments.
    private static PlacedRoad StraightRoad(float offset0 = 0f, float offset1 = 0f, bool ignoreTerrain = false) =>
        new(0, false, new[]
        {
            new RoadJoint(new Vector3(0, 0, 0), new Vector3(0, 0, -4), new Vector3(0, 0, 4), offset0, ignoreTerrain),
            new RoadJoint(new Vector3(0, 0, 12), new Vector3(0, 0, -4), new Vector3(0, 0, 4), offset1, ignoreTerrain),
        });

    private static void AssertVec(Vector3 expected, Vector3 actual, int digits = 4)
    {
        Assert.Equal(expected.X, actual.X, digits);
        Assert.Equal(expected.Y, actual.Y, digits);
        Assert.Equal(expected.Z, actual.Z, digits);
    }

    [Fact]
    public void Build_TooFewJoints_ReturnsEmpty()
    {
        var road = new PlacedRoad(0, false, new[] { new RoadJoint(Vector3.Zero, Vector3.Zero, Vector3.Zero) });
        RoadMeshData mesh = RoadMesh.Build(road, Config, 0.1f, new FakeTerrain());
        Assert.Empty(mesh.Vertices);
        Assert.Empty(mesh.Normals);
        Assert.Empty(mesh.Uvs);
        Assert.Empty(mesh.Indices);
    }

    [Fact]
    public void Build_StraightRoad_EmitsCrownSkirtsAndCaps()
    {
        // 12 m at 5 m spacing -> samples at t = 0, 5/12, 10/12 plus the closing t = 1: 4 rows + 2 caps.
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), Config, 0.1f, new FakeTerrain());
        Assert.Equal(6 * 4, mesh.Vertices.Length);

        // direction (0,0,1) x up = side (-1,0,0); flat ground at y=10; halfVertical 0.2, vertical 0.4.
        // Start cap: the section at skirt height (y 9.8) pulled back by direction * 0.8.
        AssertVec(new Vector3(-4.4f, 9.8f, -0.8f), mesh.Vertices[0]);
        AssertVec(new Vector3(-4f, 9.8f, -0.8f), mesh.Vertices[1]);
        AssertVec(new Vector3(4f, 9.8f, -0.8f), mesh.Vertices[2]);
        AssertVec(new Vector3(4.4f, 9.8f, -0.8f), mesh.Vertices[3]);

        // First sample: outer skirts 0.2 below grade at +-4.4, crown edges 0.2 above at +-4.
        AssertVec(new Vector3(-4.4f, 9.8f, 0f), mesh.Vertices[4]);
        AssertVec(new Vector3(-4f, 10.2f, 0f), mesh.Vertices[5]);
        AssertVec(new Vector3(4f, 10.2f, 0f), mesh.Vertices[6]);
        AssertVec(new Vector3(4.4f, 9.8f, 0f), mesh.Vertices[7]);

        // Second sample lands 5 m along; last sample at the end joint (z=12).
        Assert.Equal(5f, mesh.Vertices[8].Z, 4);
        Assert.Equal(12f, mesh.Vertices[16].Z, 4);

        // End cap: skirt height pushed forward by direction * 0.8.
        AssertVec(new Vector3(-4.4f, 9.8f, 12.8f), mesh.Vertices[20]);
        AssertVec(new Vector3(4.4f, 9.8f, 12.8f), mesh.Vertices[23]);

        // Every vertex normal is the terrain normal — the road shades like the ground (source line: all 4).
        foreach (Vector3 n in mesh.Normals)
            AssertVec(Vector3.Up, n);
    }

    [Fact]
    public void Build_Uvs_FollowAccumulatedDistance()
    {
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), Config, 0.1f, new FakeTerrain());

        // Cap + first sample rows: v = 0; u pattern 0,0,1,1 across the section.
        Assert.Equal(Vector2.Zero, mesh.Uvs[0]);
        Assert.Equal(Vector2.Right, mesh.Uvs[3]);
        Assert.Equal(Vector2.Zero, mesh.Uvs[4]);
        Assert.Equal(Vector2.Zero, mesh.Uvs[5]);
        Assert.Equal(Vector2.Right, mesh.Uvs[6]);
        Assert.Equal(Vector2.Right, mesh.Uvs[7]);

        // v accumulates 5, 10, 12 metres of centreline * inverseRepeat 0.1; the end cap keeps the final v.
        Assert.Equal(0.5f, mesh.Uvs[8].Y, 4);
        Assert.Equal(1.0f, mesh.Uvs[12].Y, 4);
        Assert.Equal(1.2f, mesh.Uvs[16].Y, 4);
        Assert.Equal(new Vector2(0f, 1.2f), mesh.Uvs[20]);
        Assert.Equal(new Vector2(1f, 1.2f), mesh.Uvs[23]);
    }

    [Fact]
    public void Build_Indices_MatchTheSourceTrianglePattern()
    {
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), Config, 0.1f, new FakeTerrain());
        int rows = mesh.Vertices.Length / 4;
        Assert.Equal((rows - 1) * 18, mesh.Indices.Length);

        // The source's 18-index bridge between adjacent rows, verbatim.
        int[] expected = { 5, 1, 4, 0, 4, 1, 6, 2, 5, 1, 5, 2, 7, 3, 6, 2, 6, 3 };
        for (int i = 0; i < 18; i++)
            Assert.Equal(expected[i], mesh.Indices[i]);
        // Later blocks are the same pattern shifted 4 vertices per row.
        for (int i = 0; i < 18; i++)
            Assert.Equal(expected[i] + 4, mesh.Indices[18 + i]);
    }

    [Fact]
    public void Build_CrownTriangle_FrontFacesUpAfterZReflection()
    {
        // The pipeline contract (UnityMeshConverter): reflect positions and normals by F = negate Z, keep the
        // winding; a Godot front face's outward normal is then Cross(e2, e1). For the crown that must point
        // up, agreeing with the reflected terrain normal.
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), Config, 0.1f, new FakeTerrain());

        // Second bridge block (rows 1-2), central strip triangle (1,5,2 shifted by 4): indices 5, 9, 6.
        Vector3 a = Reflect(mesh.Vertices[mesh.Indices[18 + 9]]);
        Vector3 b = Reflect(mesh.Vertices[mesh.Indices[18 + 10]]);
        Vector3 c = Reflect(mesh.Vertices[mesh.Indices[18 + 11]]);
        Vector3 front = (c - a).Cross(b - a); // Cross(e2, e1)
        Assert.True(front.Y > 0f);
        Assert.Equal(0f, front.X, 4);
        Assert.Equal(0f, front.Z, 4);

        static Vector3 Reflect(Vector3 v) => new(v.X, v.Y, -v.Z);
    }

    [Fact]
    public void Build_BanksTheSectionToTheTerrainNormal_AndLiftsTheBuriedLeftEdge()
    {
        // A slope whose normal leans in X: normal (-0.6, 0.8, 0), direction (0,0,1) -> side (-0.8,-0.6,0).
        // The left crown edge dips 2.4 m under grade, so the source's left correction lifts the whole
        // section by 2.4 (right edge then sits above grade: no second lift).
        var terrain = new FakeTerrain(normal: _ => new Vector3(-0.6f, 0.8f, 0f));
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), Config, 0.1f, terrain);

        Vector3 pos = new(0f, 12.4f, 0f);            // 10 + 2.4 lift
        Vector3 side = new(-0.8f, -0.6f, 0f);
        Vector3 normal = new(-0.6f, 0.8f, 0f);
        AssertVec(pos + (side * 4.4f) - (normal * 0.2f), mesh.Vertices[4]); // outer skirt
        AssertVec(pos + (side * 4f) + (normal * 0.2f), mesh.Vertices[5]);   // crown edge
        AssertVec(normal, mesh.Normals[5]);
    }

    [Fact]
    public void Build_LiftsTheBuriedRightEdge()
    {
        // Mirror slope: normal (0.6, 0.8, 0) -> side (-0.8, 0.6, 0); now the RIGHT edge (-side) is the
        // buried one, exercising the sequential second correction.
        var terrain = new FakeTerrain(normal: _ => new Vector3(0.6f, 0.8f, 0f));
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), Config, 0.1f, terrain);

        Vector3 pos = new(0f, 12.4f, 0f);
        Vector3 side = new(-0.8f, 0.6f, 0f);
        Vector3 normal = new(0.6f, 0.8f, 0f);
        AssertVec(pos - (side * 4f) + (normal * 0.2f), mesh.Vertices[6]); // right crown edge
    }

    [Fact]
    public void Build_IgnoreTerrain_KeepsSplineHeightUpNormalAndSkipsCorrection()
    {
        var road = new PlacedRoad(0, false, new[]
        {
            new RoadJoint(new Vector3(0, 50, 0), new Vector3(0, 0, -4), new Vector3(0, 0, 4), 0f, true),
            new RoadJoint(new Vector3(0, 50, 12), new Vector3(0, 0, -4), new Vector3(0, 0, 4), 0f, true),
        });
        var terrain = new FakeTerrain(height: _ => 10f, normal: _ => new Vector3(0.6f, 0.8f, 0f));
        RoadMeshData mesh = RoadMesh.Build(road, Config, 0.1f, terrain);

        Assert.Equal(0, terrain.HeightCalls);                    // no conform, no edge correction
        AssertVec(new Vector3(-4f, 50.2f, 0f), mesh.Vertices[5]); // crown at the spline height
        AssertVec(Vector3.Up, mesh.Normals[5]);                   // bridges use a flat up normal
    }

    [Fact]
    public void Build_VerticalOffset_LiftsAlongTheNormal()
    {
        var config = new RoadMaterialConfig(4f, 4f, 0.2f, 0.5f, true);
        RoadMeshData mesh = RoadMesh.Build(StraightRoad(), config, 0.1f, new FakeTerrain());
        Assert.Equal(10.7f, mesh.Vertices[5].Y, 4); // 10 + halfVertical 0.2 + offset 0.5
        Assert.Equal(10.3f, mesh.Vertices[4].Y, 4); // skirt: 10 - 0.2 + 0.5
    }

    [Fact]
    public void Build_JointOffsets_InterpolateAlongTheSegment()
    {
        RoadMeshData baseline = RoadMesh.Build(StraightRoad(), Config, 0.1f, new FakeTerrain());
        RoadMeshData offset = RoadMesh.Build(StraightRoad(0f, 6f), Config, 0.1f, new FakeTerrain());

        // Offsets 0 -> 6 lerp with t: the crown at t = 5/12 rises by 2.5, at t = 1 by 6.
        Assert.Equal(baseline.Vertices[9].Y + (6f * 5f / 12f), offset.Vertices[9].Y, 4);
        Assert.Equal(baseline.Vertices[17].Y + 6f, offset.Vertices[17].Y, 4);
    }

    [Fact]
    public void Build_Loop_ClosesOnItselfWithoutCaps()
    {
        // A triangle loop with mirrored tangents (anti-parallel -> polygonal lerp). The loop samples the
        // closing segment (last -> joint 0) and appends the (0, 0) sample, so the last row must equal the
        // first — a sealed ring with no cap rows.
        RoadMeshData mesh = BuildLoop(0f);

        Assert.Equal(0, mesh.Vertices.Length % 4);
        int rows = mesh.Vertices.Length / 4;
        Assert.Equal((rows - 1) * 18, mesh.Indices.Length);
        for (int k = 0; k < 4; k++)
            AssertVec(mesh.Vertices[k], mesh.Vertices[((rows - 1) * 4) + k]);
    }

    [Fact]
    public void Build_Loop_AppliesOffsetsThroughTheClosingSegment()
    {
        // Same loop with every joint offset = 3: each section (including the closing segment's, which wraps
        // its lerp back to joint 0) rises exactly 3 — proving the wrap branch lerps offsets like the source.
        RoadMeshData flat = BuildLoop(0f);
        RoadMeshData raised = BuildLoop(3f);
        Assert.Equal(flat.Vertices.Length, raised.Vertices.Length);
        for (int i = 0; i < flat.Vertices.Length; i++)
            AssertVec(flat.Vertices[i] + new Vector3(0f, 3f, 0f), raised.Vertices[i]);
    }

    private static RoadMeshData BuildLoop(float offset)
    {
        Vector3[] corners = { new(0, 0, 0), new(30, 0, 0), new(15, 0, 30) };
        var joints = new RoadJoint[3];
        for (int i = 0; i < 3; i++)
        {
            Vector3 toNext = (corners[(i + 1) % 3] - corners[i]).Normalized() * 5f;
            joints[i] = new RoadJoint(corners[i], -toNext, toNext, offset);
        }
        return RoadMesh.Build(new PlacedRoad(0, true, joints), Config, 0.1f, new FakeTerrain());
    }

    [Fact]
    public void Build_SampleSpacing_StaysUniformAcrossJoints()
    {
        // Two straight 7 m segments: 5 m stepping leaves a 2 m residual after the first, so the second
        // segment's first sample lands 3 m in — continuous 5 m spacing through the joint (source's
        // updateSamples carries the offset), never a reset at each joint.
        var road = new PlacedRoad(0, false, new[]
        {
            new RoadJoint(new Vector3(0, 0, 0), new Vector3(0, 0, -2), new Vector3(0, 0, 2)),
            new RoadJoint(new Vector3(0, 0, 7), new Vector3(0, 0, -2), new Vector3(0, 0, 2)),
            new RoadJoint(new Vector3(0, 0, 14), new Vector3(0, 0, -2), new Vector3(0, 0, 2)),
        });
        RoadMeshData mesh = RoadMesh.Build(road, Config, 0.1f, new FakeTerrain());

        // Rows: cap, z=0, z=5, z=10, z=14 (closing), cap.
        Assert.Equal(6 * 4, mesh.Vertices.Length);
        Assert.Equal(0f, mesh.Vertices[4].Z, 4);
        Assert.Equal(5f, mesh.Vertices[8].Z, 4);
        Assert.Equal(10f, mesh.Vertices[12].Z, 4);
        Assert.Equal(14f, mesh.Vertices[16].Z, 4);
    }

    [Fact]
    public void Build_CoincidentJoints_ProducesFiniteDegenerateStrip()
    {
        // Zero-length estimate: no interval samples, only the closing one; zero velocity -> zero side
        // (Unity's zero-safe normalized), the section collapses to the centreline but nothing NaNs.
        var road = new PlacedRoad(0, false, new[]
        {
            new RoadJoint(Vector3.Zero, Vector3.Zero, Vector3.Zero),
            new RoadJoint(Vector3.Zero, Vector3.Zero, Vector3.Zero),
        });
        RoadMeshData mesh = RoadMesh.Build(road, Config, 0.1f, new FakeTerrain());

        Assert.Equal(3 * 4, mesh.Vertices.Length); // closing sample + both caps
        foreach (Vector3 v in mesh.Vertices)
        {
            Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z));
        }
        Assert.Equal(0f, mesh.Vertices[5].X, 4); // collapsed: no side extent
    }

    [Fact]
    public void GetPosition_BezierPath_WhenTangentsAreNotAntiParallel()
    {
        // Perpendicular tangents (dot 0): the real cubic. p = Bezier((0,0,0),(4,0,0),(0,0,8),(0,0,12), 0.5)
        // = (1.5, 0, 4.5) by hand.
        var road = new PlacedRoad(0, false, new[]
        {
            new RoadJoint(new Vector3(0, 0, 0), Vector3.Zero, new Vector3(4, 0, 0)),
            new RoadJoint(new Vector3(0, 0, 12), new Vector3(0, 0, -4), Vector3.Zero),
        });
        AssertVec(new Vector3(1.5f, 0f, 4.5f), RoadMesh.GetPosition(road, 0, 0.5f));
    }

    [Fact]
    public void GetPosition_AntiParallelTangents_FallBackToLerp()
    {
        PlacedRoad road = StraightRoad();
        AssertVec(new Vector3(0f, 0f, 3f), RoadMesh.GetPosition(road, 0, 0.25f)); // pure lerp, z = 12t
    }

    [Fact]
    public void GetPosition_ClampsIndexAndTime()
    {
        PlacedRoad road = StraightRoad();
        AssertVec(RoadMesh.GetPosition(road, 0, 0.5f), RoadMesh.GetPosition(road, -3, 0.5f));
        // Index past the end clamps to the last joint, whose segment wraps to joint 0 (t=1 -> its vertex).
        AssertVec(new Vector3(0f, 0f, 0f), RoadMesh.GetPosition(road, 5, 2f));
    }

    [Fact]
    public void GetVelocity_IsTheBezierDerivative_WithoutTheAntiParallelFallback()
    {
        // The mirrored-tangent straight road: getPosition lerps but getVelocity stays Bezier — here the
        // constant derivative (0,0,12) (control points evenly spaced along z).
        PlacedRoad road = StraightRoad();
        AssertVec(new Vector3(0f, 0f, 12f), RoadMesh.GetVelocity(road, 0, 0f));
        AssertVec(new Vector3(0f, 0f, 12f), RoadMesh.GetVelocity(road, 0, 0.7f));
    }

    [Fact]
    public void GetLengthEstimate_AntiParallel_UsesTheChord()
    {
        Assert.Equal(12f, RoadMesh.GetLengthEstimate(StraightRoad(), 0), 4);
    }

    [Fact]
    public void GetLengthEstimate_Bezier_AveragesChordAndControlPolygon()
    {
        var road = new PlacedRoad(0, false, new[]
        {
            new RoadJoint(new Vector3(0, 0, 0), Vector3.Zero, new Vector3(4, 0, 0)),
            new RoadJoint(new Vector3(0, 0, 12), new Vector3(0, 0, -4), Vector3.Zero),
        });
        // (|d-a| + |d-c| + |b-c| + |b-a|) / 2 = (12 + 4 + sqrt(80) + 4) / 2.
        float expected = (12f + 4f + Mathf.Sqrt(80f) + 4f) / 2f;
        Assert.Equal(expected, RoadMesh.GetLengthEstimate(road, 0), 4);
    }
}
