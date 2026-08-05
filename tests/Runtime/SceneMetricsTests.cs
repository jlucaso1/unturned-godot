using System;
using System.Collections.Generic;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Benchmark;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// What the benchmark counts when it looks at a built world.
//
// The rule that makes these numbers mean anything is that geometry is charged ONCE PER UNIQUE MESH
// RESOURCE, not once per instance. Sixteen distinct terrain tiles are sixteen uploads; a MultiMesh's
// thousands of trees share one mesh and are one upload. Counting per instance would report a forest as
// hundreds of millions of vertices and make every rendering change look like an improvement, because the
// number it moved was never the number the GPU sees.
//
// The other one is that a spatial renderer with nothing resident still has to answer. Tier 1 attaches no
// camera and advances no frame, so a streaming renderer has no GPU buffers at all — but its index is the
// complete deterministic scene, and it must contribute the same counts as the all-resident path or the
// tier would report a world that got emptier every time streaming got better.
public class SceneMetricsTests : TestClass
{
    public SceneMetricsTests(Node testScene) : base(testScene) { }

    // Nothing is nothing, rather than a crash or a fabricated baseline.
    [Test]
    public void AnEmptyWorldMeasuresEmpty()
    {
        SceneMetricsResult result = SceneMetrics.Collect(Array.Empty<Node>());

        Assert.Equal(0, result.Nodes);
        Assert.Equal(0, result.MeshInstances);
        Assert.Equal(0, result.UniqueMeshes);
        Assert.Equal(0L, result.UploadedTriangles);
    }

    // Every node is counted, including the ones that draw nothing. The node count is what tells a reader
    // whether a scene got heavier in structure rather than in geometry.
    [Test]
    public void EveryNodeIsCountedEvenTheOnesThatDrawNothing()
    {
        using var scene = new Scratch();
        Node3D root = scene.Root;
        root.AddChild(new Node3D { Name = "Empty" });
        root.AddChild(new Node { Name = "AlsoEmpty" });

        SceneMetricsResult result = SceneMetrics.Collect(new[] { (Node)root });

        Assert.Equal(3, result.Nodes);
        Assert.Equal(0, result.MeshInstances);
    }

    // A mesh instance charges its geometry once, and its triangles are the real count off the surface.
    [Test]
    public void AMeshInstanceChargesItsGeometry()
    {
        using var scene = new Scratch();
        scene.Root.AddChild(new MeshInstance3D { Mesh = Triangle() });

        SceneMetricsResult result = SceneMetrics.Collect(new[] { (Node)scene.Root });

        Assert.Equal(1, result.MeshInstances);
        Assert.Equal(1, result.UniqueMeshes);
        Assert.Equal(3L, result.UploadedVertices);
        Assert.Equal(1L, result.UploadedTriangles);
    }

    // Two instances of the SAME mesh are two draws and one upload. This is the whole point: the shared
    // resource is uploaded once however many things point at it.
    [Test]
    public void SharingOneMeshIsOneUpload()
    {
        using var scene = new Scratch();
        ArrayMesh shared = Triangle();
        scene.Root.AddChild(new MeshInstance3D { Mesh = shared });
        scene.Root.AddChild(new MeshInstance3D { Mesh = shared });

        SceneMetricsResult result = SceneMetrics.Collect(new[] { (Node)scene.Root });

        Assert.Equal(2, result.MeshInstances);
        Assert.Equal(1, result.UniqueMeshes);
        Assert.Equal(3L, result.UploadedVertices);
    }

    // And two DIFFERENT meshes are two uploads, which is the other half of the same rule — sixteen
    // distinct terrain tiles must not collapse into one.
    [Test]
    public void TwoDistinctMeshesAreTwoUploads()
    {
        using var scene = new Scratch();
        scene.Root.AddChild(new MeshInstance3D { Mesh = Triangle() });
        scene.Root.AddChild(new MeshInstance3D { Mesh = Triangle() });

        SceneMetricsResult result = SceneMetrics.Collect(new[] { (Node)scene.Root });

        Assert.Equal(2, result.UniqueMeshes);
        Assert.Equal(6L, result.UploadedVertices);
    }

    // A MultiMesh is one upload and many instances, counted separately. Charging its thousands of trees
    // as thousands of uploads would report a forest as hundreds of millions of vertices.
    [Test]
    public void AMultiMeshIsOneUploadAndManyInstances()
    {
        using var scene = new Scratch();
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = Triangle(),
            InstanceCount = 500,
        };
        scene.Root.AddChild(new MultiMeshInstance3D { Multimesh = mm });

        SceneMetricsResult result = SceneMetrics.Collect(new[] { (Node)scene.Root });

        Assert.Equal(1, result.MultiMeshInstances);
        Assert.Equal(500L, result.MultiMeshTotalInstances);
        Assert.Equal(1, result.UniqueMeshes);
        Assert.Equal(3L, result.UploadedVertices);
        Assert.Equal(0, result.MeshInstances); // it is not also a mesh instance
    }

    // The batch SPREAD — how far apart a merged batch's instances sit, which is the number that says
    // whether a merge was a good idea, since a batch scattered across the map is either all drawn or all
    // skipped — is not covered here, and cannot be from this suite.
    //
    // A MultiMesh keeps its per-instance transforms in the rendering server rather than in the object,
    // and a headless run has the dummy driver: SetInstanceTransform is accepted and GetInstanceTransform
    // hands back the identity, so every batch a test builds measures a spread of zero however it is
    // arranged. Verified rather than assumed. The counts above are all read off the objects themselves,
    // which is why they work.

    // The walk is recursive: geometry parked deep under empty nodes counts the same as geometry at the
    // top. A world is built as a tree of streamers and regions, and almost nothing sits at the root.
    [Test]
    public void GeometryDeepInTheTreeCountsTheSame()
    {
        using var scene = new Scratch();
        var region = new Node3D { Name = "Region" };
        var cell = new Node3D { Name = "Cell" };
        cell.AddChild(new MeshInstance3D { Mesh = Triangle() });
        region.AddChild(cell);
        scene.Root.AddChild(region);

        SceneMetricsResult result = SceneMetrics.Collect(new[] { (Node)scene.Root });

        Assert.Equal(1, result.MeshInstances);
        Assert.Equal(1L, result.UploadedTriangles);
    }

    // Several roots are one measurement. The tier hands in the world and the player separately, and a
    // mesh shared across both must still be charged once.
    [Test]
    public void SeveralRootsAreOneMeasurement()
    {
        using var scene = new Scratch();
        ArrayMesh shared = Triangle();
        var a = new Node3D { Name = "A" };
        var b = new Node3D { Name = "B" };
        a.AddChild(new MeshInstance3D { Mesh = shared });
        b.AddChild(new MeshInstance3D { Mesh = shared });
        scene.Root.AddChild(a);
        scene.Root.AddChild(b);

        SceneMetricsResult result = SceneMetrics.Collect(new List<Node> { a, b });

        Assert.Equal(2, result.MeshInstances);
        Assert.Equal(1, result.UniqueMeshes);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static ArrayMesh Triangle()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[]
        {
            Vector3.Zero, new Vector3(1f, 0f, 0f), new Vector3(0f, 0f, 1f),
        };
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    // A node the test owns and frees, kept out of the shared scene's own child list so one test's
    // geometry can never be counted by another's measurement.
    private sealed class Scratch : IDisposable
    {
        public Node3D Root { get; } = new() { Name = "MeasuredWorld" };

        public void Dispose() => Root.Free();
    }
}
