using System;
using System.Collections.Generic;
using System.IO;
using Chickensoft.GoDotTest;
using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.RuntimeTests;

// Lofting the map's road splines into drawn geometry.
//
// Reading Paths.dat and RoadMesh's own lofting are pure and unit tested in core/. What is only testable
// here is the batching and the materials — and the batching is the point: roads of one material share a
// texture, a tiling and a width, so their strips merge into ONE mesh per material rather than one draw
// call per road. PEI has 23 roads and three materials.
//
// Both merge paths are covered, because the project ships with a switch between them (UG_ROAD_ARRAYS) and
// a switch nobody exercises on one side is a switch that is broken on that side.
public class RoadsBuilderTests : TestClass
{
    public RoadsBuilderTests(Node testScene) : base(testScene) { }

    // A map with no Paths.dat still builds — plenty of workshop maps have no roads, and an empty root is
    // the honest answer rather than a failure.
    [Test]
    public void AMapWithNoRoadsBuildsAnEmptyRoot()
    {
        using var dir = new TempDir();

        Node3D root = RoadsBuilder.Build(dir.Path, Flat());
        TestScene.AddChild(root);

        Assert.Equal(0, root.GetChildCount());
        root.QueueFree();
    }

    // Roads sharing a material end up in one mesh. Two roads, one material, one draw call — which is the
    // difference between a handful of calls and one per road on a map with dozens.
    [Test]
    public void RoadsOfOneMaterialMergeIntoOneMesh()
    {
        using var dir = new TempDir();
        dir.WritePaths(
            (Material: (byte)0, Straight(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -100f))),
            (Material: (byte)0, Straight(new Vector3(50f, 0f, 0f), new Vector3(50f, 0f, -100f))));

        Node3D root = RoadsBuilder.Build(dir.Path, Flat());
        TestScene.AddChild(root);

        Assert.Equal(1, CountMeshes(root));
        root.QueueFree();
    }

    // …and roads of different materials do not, because they cannot: a merged mesh carries one material.
    [Test]
    public void RoadsOfDifferentMaterialsStayApart()
    {
        using var dir = new TempDir();
        dir.WritePaths(
            (Material: (byte)0, Straight(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -100f))),
            (Material: (byte)1, Straight(new Vector3(50f, 0f, 0f), new Vector3(50f, 0f, -100f))));

        Node3D root = RoadsBuilder.Build(dir.Path, Flat());
        TestScene.AddChild(root);

        Assert.Equal(2, CountMeshes(root));
        root.QueueFree();
    }

    // The other merge path. The project ships a switch between them, and a switch nobody exercises on one
    // side is a switch that is broken on that side — both must produce the same batching.
    [Test]
    public void TheSurfaceToolPathBatchesTheSameWay()
    {
        using var dir = new TempDir();
        dir.WritePaths(
            (Material: (byte)0, Straight(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -100f))),
            (Material: (byte)0, Straight(new Vector3(50f, 0f, 0f), new Vector3(50f, 0f, -100f))),
            (Material: (byte)1, Straight(new Vector3(-50f, 0f, 0f), new Vector3(-50f, 0f, -100f))));

        string previous = OS.GetEnvironment("UG_ROAD_ARRAYS");
        OS.SetEnvironment("UG_ROAD_ARRAYS", "0");
        try
        {
            Node3D root = RoadsBuilder.Build(dir.Path, Flat());
            TestScene.AddChild(root);

            Assert.Equal(2, CountMeshes(root)); // same two materials, same two meshes
            root.QueueFree();
        }
        finally
        {
            OS.SetEnvironment("UG_ROAD_ARRAYS", previous);
        }
    }

    // A road whose spline lofts to nothing — a single joint, no length — contributes no geometry rather
    // than an empty surface in the merged mesh.
    [Test]
    public void ARoadThatLoftsToNothingIsSkipped()
    {
        using var dir = new TempDir();
        dir.WritePaths((Material: (byte)0, new[] { new Vector3(0f, 0f, 0f) }));

        Node3D root = RoadsBuilder.Build(dir.Path, Flat());
        TestScene.AddChild(root);

        Assert.Equal(0, CountMeshes(root));
        root.QueueFree();
    }

    // Roads follow the ground rather than floating at the height their joints were authored at: the
    // spline is sampled against the heightmap as it is lofted.
    [Test]
    public void RoadsFollowTheGround()
    {
        using var dir = new TempDir();
        dir.WritePaths((Material: (byte)0, Straight(new Vector3(0f, 0f, 0f), new Vector3(0f, 0f, -100f))));

        Node3D onFlat = RoadsBuilder.Build(dir.Path, Flat(0.2f));
        Node3D onHigher = RoadsBuilder.Build(dir.Path, Flat(0.6f));
        TestScene.AddChild(onFlat);
        TestScene.AddChild(onHigher);

        float low = FirstVertexHeight(onFlat);
        float high = FirstVertexHeight(onHigher);
        Assert.True(high > low + 1f,
            $"the road did not follow the ground: {low} on low terrain, {high} on high");

        onFlat.QueueFree();
        onHigher.QueueFree();
    }

    // --- helpers -------------------------------------------------------------------------------------

    private static int CountMeshes(Node parent)
    {
        int found = 0;
        foreach (Node child in parent.GetChildren())
        {
            if (child is MeshInstance3D mesh && mesh.Mesh != null && mesh.Mesh.GetSurfaceCount() > 0)
                found++;
            found += CountMeshes(child);
        }
        return found;
    }

    private static float FirstVertexHeight(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            if (child is MeshInstance3D mesh && mesh.Mesh is { } m && m.GetSurfaceCount() > 0)
            {
                var vertices = (Vector3[])m.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex];
                if (vertices.Length > 0)
                    return vertices[0].Y;
            }
            float nested = FirstVertexHeight(child);
            if (!float.IsNaN(nested))
                return nested;
        }
        return float.NaN;
    }

    // One flat tile at a given normalised height, which is all the road lofting asks of the terrain.
    private static HeightmapSampler Flat(float height = 0.2f)
    {
        const int res = Landscape.HEIGHTMAP_RESOLUTION;
        var heights = new float[res, res];
        for (int i = 0; i < res; i++)
            for (int j = 0; j < res; j++)
                heights[i, j] = height;
        return new HeightmapSampler(new[] { HeightmapTile.FromHeights(0, 0, heights) });
    }

    private static Vector3[] Straight(Vector3 from, Vector3 to) => new[] { from, to };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "unturned-godot-roads-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        // A Paths.dat in the real layout (version 5: every optional field except the road-asset guid,
        // which arrived in 6). The reader is exercised for real in core's LevelRoadsTests.
        public void WritePaths(params (byte Material, Vector3[] Joints)[] roads)
        {
            var bytes = new List<byte> { 5 };
            bytes.AddRange(BitConverter.GetBytes((ushort)roads.Length));
            foreach ((byte material, Vector3[] joints) in roads)
            {
                bytes.AddRange(BitConverter.GetBytes((ushort)joints.Length));
                bytes.Add(material);
                bytes.Add(0);                                  // loop
                foreach (Vector3 joint in joints)
                {
                    WriteVector(bytes, joint);
                    WriteVector(bytes, joint + Vector3.Forward * 10f); // tangent in
                    WriteVector(bytes, joint + Vector3.Back * 10f);    // tangent out
                    bytes.Add(0);                              // tangent mode
                    bytes.AddRange(BitConverter.GetBytes(0f)); // offset (v > 4)
                    bytes.Add(0);                              // ignore terrain (v > 3)
                }
            }
            File.WriteAllBytes(System.IO.Path.Combine(Path, "Paths.dat"), bytes.ToArray());
        }

        private static void WriteVector(List<byte> into, Vector3 v)
        {
            into.AddRange(BitConverter.GetBytes(v.X));
            into.AddRange(BitConverter.GetBytes(v.Y));
            into.AddRange(BitConverter.GetBytes(v.Z));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory is not worth failing a test over.
            }
        }
    }
}
