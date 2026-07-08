using System.Collections.Generic;
using Godot;
using UnturnedGodot.Data;
using UnturnedGodot.Tests.Helpers;
using Xunit;

namespace UnturnedGodot.Tests;

public class LevelRoadsTests
{
    private readonly record struct Joint(Vector3 Vertex, Vector3 T0, Vector3 T1);

    private static byte[] BuildPaths(byte version, params (byte material, bool loop, Joint[] joints)[] roads)
    {
        var w = new RiverBytes().Byte(version).UInt16((ushort)roads.Length);
        foreach (var (material, loop, joints) in roads)
        {
            w.UInt16((ushort)joints.Length).Byte(material);
            if (version > 2) w.Bool(loop);
            if (version >= 6) w.Guid(System.Guid.NewGuid());
            foreach (Joint j in joints)
            {
                w.Vector3(j.Vertex);
                if (version > 2) { w.Vector3(j.T0); w.Vector3(j.T1); w.Byte(0); }
                if (version > 4) w.Single(0f);
                if (version > 3) w.Bool(false);
            }
        }
        return w.ToArray();
    }

    [Fact]
    public void LoadPaths_MissingFile_ReturnsEmpty() => Assert.Empty(LevelRoads.LoadPaths("/no/such/Paths.dat"));

    [Fact]
    public void LoadPaths_ReadsRoadsAndJoints()
    {
        using var dir = new TempDir();
        var joints = new[]
        {
            new Joint(new Vector3(0, 32, 0), new Vector3(-1, 0, 0), new Vector3(1, 0, 0)),
            new Joint(new Vector3(10, 34, 0), new Vector3(-1, 0, 0), new Vector3(1, 0, 0)),
        };
        string path = dir.Write("Paths.dat", BuildPaths(6, ((byte)0, false, joints), ((byte)5, true, joints)));

        List<PlacedRoad> roads = LevelRoads.LoadPaths(path);

        Assert.Equal(2, roads.Count);
        Assert.Equal(0, roads[0].Material);
        Assert.False(roads[0].IsLoop);
        Assert.Equal(2, roads[0].Joints.Count);
        Assert.Equal(new Vector3(0, 32, 0), roads[0].Joints[0].Vertex);
        Assert.Equal(new Vector3(1, 0, 0), roads[0].Joints[0].Tangent1);
        Assert.Equal(5, roads[1].Material);
        Assert.True(roads[1].IsLoop);
    }

    [Fact]
    public void LoadPaths_ObsoleteVersion_ReturnsEmpty()
    {
        using var dir = new TempDir();
        string path = dir.Write("Paths.dat", new RiverBytes().Byte(1).ToArray());
        Assert.Empty(LevelRoads.LoadPaths(path));
    }

    [Fact]
    public void LoadMaterials_ReadsWidthsAndTypes()
    {
        using var dir = new TempDir();
        var w = new RiverBytes().Byte(2).Byte(2)
            .Single(8f).Single(4f).Single(0.2f).Single(0f).Bool(true)   // paved
            .Single(4f).Single(8f).Single(0.3f).Single(0f).Bool(false); // dirt
        string path = dir.Write("Roads.dat", w.ToArray());

        List<RoadMaterialConfig> mats = LevelRoads.LoadMaterials(path);

        Assert.Equal(2, mats.Count);
        Assert.Equal(8f, mats[0].Width);
        Assert.Equal(4f, mats[0].Height);
        Assert.True(mats[0].IsConcrete);
        Assert.Equal(4f, mats[1].Width);
        Assert.False(mats[1].IsConcrete);
    }

    [Fact]
    public void LoadMaterials_MissingFile_ReturnsEmpty() =>
        Assert.Empty(LevelRoads.LoadMaterials("/no/such/Roads.dat"));

    [Fact]
    public void LoadMaterials_ObsoleteVersion_ReturnsEmpty()
    {
        using var dir = new TempDir();
        string path = dir.Write("Roads.dat", new RiverBytes().Byte(0).ToArray());
        Assert.Empty(LevelRoads.LoadMaterials(path));
    }

    [Fact]
    public void LoadMaterials_Version1_DefaultsOffset()
    {
        using var dir = new TempDir();
        // Version 1 has no per-material offset field.
        var w = new RiverBytes().Byte(1).Byte(1).Single(8f).Single(4f).Single(0.2f).Bool(true);
        List<RoadMaterialConfig> mats = LevelRoads.LoadMaterials(dir.Write("Roads.dat", w.ToArray()));
        Assert.Single(mats);
        Assert.Equal(0f, mats[0].Offset);
    }

    [Fact]
    public void LoadPaths_Version2_OmitsLoopTangentsAndAsset()
    {
        using var dir = new TempDir();
        var joints = new[]
        {
            new Joint(new Vector3(0, 32, 0), Vector3.Zero, Vector3.Zero),
            new Joint(new Vector3(10, 34, 0), Vector3.Zero, Vector3.Zero),
        };
        string path = dir.Write("Paths.dat", BuildPaths(2, ((byte)0, false, joints)));

        List<PlacedRoad> roads = LevelRoads.LoadPaths(path);

        Assert.Single(roads);
        Assert.False(roads[0].IsLoop);
        Assert.Equal(2, roads[0].Joints.Count);
    }

    [Fact]
    public void BezierPoint_EndpointsAndMidpoint()
    {
        var a = new RoadJoint(new Vector3(0, 0, 0), Vector3.Zero, new Vector3(3, 0, 0));
        var b = new RoadJoint(new Vector3(10, 0, 0), new Vector3(-3, 0, 0), Vector3.Zero);

        Assert.Equal(a.Vertex, LevelRoads.BezierPoint(a, b, 0f));
        Assert.Equal(b.Vertex, LevelRoads.BezierPoint(a, b, 1f));
        // Symmetric handles keep the midpoint on the straight line between the vertices.
        Assert.Equal(5f, LevelRoads.BezierPoint(a, b, 0.5f).X, 0.001f);
        Assert.Equal(0f, LevelRoads.BezierPoint(a, b, 0.5f).Y, 0.001f);
    }
}
