using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Repro;
using Xunit;

namespace UnturnedGodot.Tests.Repro;

// The maths a physics-free replay stands on. Each case here is a configuration the sweep actually
// meets — a body beside a wall, above a floor, threaded through a doorway — rather than a random
// triangle, because the failure mode of getting one of these wrong is a replay that quietly disagrees
// with the session it is supposed to reproduce.
public class ReproGeometryTests
{
    private static readonly Vector3 A = new(0f, 0f, 0f);
    private static readonly Vector3 B = new(2f, 0f, 0f);
    private static readonly Vector3 C = new(0f, 0f, 2f);

    [Fact]
    public void ClosestPointOnTriangle_InsideTheFace_ProjectsOntoIt()
    {
        Vector3 point = ReproGeometry.ClosestPointOnTriangle(new Vector3(0.5f, 3f, 0.5f), A, B, C);
        Assert.Equal(new Vector3(0.5f, 0f, 0.5f), point);
    }

    [Theory]
    [InlineData(-1f, -1f, 0f, 0f)]   // vertex A's region
    [InlineData(4f, -1f, 2f, 0f)]    // vertex B's region
    [InlineData(-1f, 4f, 0f, 2f)]    // vertex C's region
    [InlineData(1f, -1f, 1f, 0f)]    // the AB edge
    [InlineData(-1f, 1f, 0f, 1f)]    // the CA edge
    [InlineData(2f, 2f, 1f, 1f)]     // the BC edge
    public void ClosestPointOnTriangle_CoversEveryVoronoiRegion(float x, float z,
        float expectedX, float expectedZ)
    {
        Vector3 point = ReproGeometry.ClosestPointOnTriangle(new Vector3(x, 0f, z), A, B, C);
        Assert.True(point.IsEqualApprox(new Vector3(expectedX, 0f, expectedZ)), point.ToString());
    }

    [Fact]
    public void ClosestPointsSegments_Crossing_MeetInTheMiddle()
    {
        ReproGeometry.ClosestPointsSegments(new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, -1f), new Vector3(0f, 1f, 1f), out Vector3 p, out Vector3 q);
        Assert.True(p.IsEqualApprox(Vector3.Zero), p.ToString());
        Assert.True(q.IsEqualApprox(new Vector3(0f, 1f, 0f)), q.ToString());
    }

    [Fact]
    public void ClosestPointsSegments_Parallel_PickAnEndpointPair()
    {
        ReproGeometry.ClosestPointsSegments(new Vector3(0f, 0f, 0f), new Vector3(2f, 0f, 0f),
            new Vector3(0f, 1f, 0f), new Vector3(2f, 1f, 0f), out Vector3 p, out Vector3 q);
        Assert.Equal(1f, (p - q).Length(), 4);
    }

    [Fact]
    public void ClosestPointsSegments_Degenerate_StillAnswers()
    {
        Vector3 point = new(1f, 2f, 3f);
        ReproGeometry.ClosestPointsSegments(point, point, point, point, out Vector3 p, out Vector3 q);
        Assert.Equal(point, p);
        Assert.Equal(point, q);

        // A mega's capsule collapses to nearly a point; the wall does not.
        ReproGeometry.ClosestPointsSegments(new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f),
            new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 5f), out p, out q);
        Assert.True(q.IsEqualApprox(Vector3.Zero), q.ToString());
        Assert.Equal(1f, (p - q).Length(), 4);

        // ...and the other way round: a point-sized triangle edge against a real capsule.
        ReproGeometry.ClosestPointsSegments(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 5f),
            new Vector3(1f, 0f, 0f), new Vector3(1f, 0f, 0f), out p, out q);
        Assert.Equal(1f, (p - q).Length(), 4);

        // The far end of a segment, when the perpendicular foot lies past it.
        ReproGeometry.ClosestPointsSegments(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, 1f),
            new Vector3(5f, 0f, 9f), new Vector3(6f, 0f, 9f), out p, out q);
        Assert.True(p.IsEqualApprox(new Vector3(0f, 0f, 1f)), p.ToString());
        Assert.True(q.IsEqualApprox(new Vector3(5f, 0f, 9f)), q.ToString());
    }

    [Fact]
    public void SegmentTriangleDistance_AboveTheFace_IsTheHeight()
    {
        float distance = ReproGeometry.SegmentTriangleDistanceSquared(
            new Vector3(0.5f, 2f, 0.5f), new Vector3(0.5f, 4f, 0.5f), A, B, C, out Vector3 direction);
        Assert.Equal(4f, distance, 3);
        Assert.True(direction.Normalized().IsEqualApprox(Vector3.Up), direction.ToString());
    }

    [Fact]
    public void SegmentTriangleDistance_ThroughTheFace_IsZeroWithASidedNormal()
    {
        float above = ReproGeometry.SegmentTriangleDistanceSquared(
            new Vector3(0.5f, 1f, 0.5f), new Vector3(0.5f, -1f, 0.5f), A, B, C, out Vector3 fromAbove);
        Assert.Equal(0f, above);
        Assert.True(fromAbove.Y > 0f, fromAbove.ToString());

        float below = ReproGeometry.SegmentTriangleDistanceSquared(
            new Vector3(0.5f, -1f, 0.5f), new Vector3(0.5f, 1f, 0.5f), A, B, C, out Vector3 fromBelow);
        Assert.Equal(0f, below);
        Assert.True(fromBelow.Y < 0f, fromBelow.ToString());
    }

    // Crossing the triangle's PLANE outside the triangle itself is the case a plane test alone gets
    // wrong: the body is beside the wall, not inside it.
    [Fact]
    public void SegmentTriangleDistance_CrossingThePlaneBesideTheFace_MeasuresTheEdge()
    {
        float distance = ReproGeometry.SegmentTriangleDistanceSquared(
            new Vector3(5f, 1f, 5f), new Vector3(5f, -1f, 5f), A, B, C, out _);
        Assert.True(distance > 1f, distance.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void SegmentTriangleDistance_Degenerate_StillMeasures()
    {
        float distance = ReproGeometry.SegmentTriangleDistanceSquared(
            new Vector3(0f, 1f, 0f), new Vector3(0f, 1f, 0f), A, A, A, out _);
        Assert.Equal(1f, distance, 3);
    }

    [Fact]
    public void RayTriangle_HitsAndMisses()
    {
        Assert.True(ReproGeometry.RayTriangle(new Vector3(0.5f, 5f, 0.5f), Vector3.Down, A, B, C,
            out float distance));
        Assert.Equal(5f, distance, 3);

        // Backfaces count: a dump's slice cuts through meshes, so inward faces are ordinary.
        Assert.True(ReproGeometry.RayTriangle(new Vector3(0.5f, -5f, 0.5f), Vector3.Up, A, B, C, out _));
        // Beside it, parallel to it, and behind it.
        Assert.False(ReproGeometry.RayTriangle(new Vector3(5f, 5f, 5f), Vector3.Down, A, B, C, out _));
        Assert.False(ReproGeometry.RayTriangle(new Vector3(0.5f, 1f, 0.5f), Vector3.Right, A, B, C, out _));
        Assert.False(ReproGeometry.RayTriangle(new Vector3(0.5f, 5f, 0.5f), Vector3.Up, A, B, C, out _));
        // Past the third edge, where u + v exceeds one.
        Assert.False(ReproGeometry.RayTriangle(new Vector3(1.9f, 5f, 1.9f), Vector3.Down, A, B, C, out _));
    }

    [Fact]
    public void TheGridFindsWhatOverlapsAndNothingElse()
    {
        var geometry = new ReproWorlds.Geometry()
            .Box("pole", new Vector3(10f, 1.5f, 10f), new Vector3(0.25f, 1.5f, 0.25f));
        ReproTriangles triangles = ReproTriangles.From(geometry.Build(Vector3.Zero, 512f))!;
        Assert.Equal(12, triangles.TriangleCount);

        var found = new List<int>();
        triangles.Gather(9f, 9f, 11f, 11f, found);
        Assert.Equal(12, found.Count); // deduplicated across the cells the box spans
        Assert.Equal("pole", triangles.OwnerOf(found[0]));

        triangles.Gather(100f, 100f, 101f, 101f, found);
        Assert.Empty(found);
    }

    [Fact]
    public void AnEmptySliceIsNoWorldAtAll()
    {
        Assert.Null(ReproTriangles.From(null));
        Assert.Null(ReproTriangles.From(new ReproGeometryData()));
        Assert.Null(ReproCollisionWorld.From(new ReproWorldData()));
    }

    // Layers and owners are optional in the file: a hand-written dump (or an older one) still loads,
    // with everything on the solid-world layer and no names.
    [Fact]
    public void MissingLayersAndOwnersFallBack()
    {
        var data = new ReproGeometryData
        {
            Vertices = new[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f },
            Triangles = new[] { 0, 1, 2 },
        };
        ReproTriangles triangles = ReproTriangles.From(data)!;
        Assert.True(triangles.Matches(0, CollisionLayers.World));
        Assert.Equal("?", triangles.OwnerOf(0));
        Assert.Equal("?", triangles.OwnerOf(9));
        Assert.True(triangles.NormalOf(0).IsEqualApprox(Vector3.Down)
            || triangles.NormalOf(0).IsEqualApprox(Vector3.Up));
    }

    [Fact]
    public void ADegenerateTriangleHasAnUprightNormal()
    {
        var data = new ReproGeometryData
        {
            Vertices = new[] { 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f },
            Triangles = new[] { 0, 1, 2 },
        };
        Assert.Equal(Vector3.Up, ReproTriangles.From(data)!.NormalOf(0));
    }

    [Fact]
    public void ConstructionRejectsMissingArrays()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ReproTriangles(null!, Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() =>
            new ReproTriangles(Array.Empty<Vector3>(), null!, Array.Empty<int>(), Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() =>
            new ReproTriangles(Array.Empty<Vector3>(), Array.Empty<int>(), null!, Array.Empty<int>()));
        Assert.Throws<ArgumentNullException>(() =>
            new ReproTriangles(Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>(), null!));
        Assert.Throws<ArgumentNullException>(() => new ReproCollisionWorld(null!));
    }

    [Fact]
    public void AnEmptyGridGathersNothing()
    {
        var empty = new ReproTriangles(Array.Empty<Vector3>(), Array.Empty<int>(),
            Array.Empty<int>(), Array.Empty<int>());
        var found = new List<int>();
        empty.Gather(-10f, -10f, 10f, 10f, found);
        Assert.Empty(found);
        Assert.Throws<ArgumentNullException>(() => empty.Gather(0f, 0f, 1f, 1f, null!));
    }
}
