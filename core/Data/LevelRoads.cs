using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One control point of a road spline: its position plus the two Bezier tangents (in and out).
public readonly struct RoadJoint
{
    public readonly Vector3 Vertex;   // Unity world space
    public readonly Vector3 Tangent0; // incoming handle (relative to Vertex)
    public readonly Vector3 Tangent1; // outgoing handle (relative to Vertex)
    public readonly float Offset;     // vertical offset added on top of the conformed/spline height
    public readonly bool IgnoreTerrain; // true for bridges: keep the spline height instead of conforming

    public RoadJoint(Vector3 vertex, Vector3 tangent0, Vector3 tangent1, float offset = 0f, bool ignoreTerrain = false)
    {
        Vertex = vertex;
        Tangent0 = tangent0;
        Tangent1 = tangent1;
        Offset = offset;
        IgnoreTerrain = ignoreTerrain;
    }
}

// A road: a Bezier spline of joints plus the index of the material (width + texture) it uses.
public readonly struct PlacedRoad
{
    public readonly int Material;
    public readonly bool IsLoop;
    public readonly IReadOnlyList<RoadJoint> Joints;

    // From Paths.dat version 6 a road may name a RoadAsset instead of an index into the legacy
    // Roads.dat table, and the two disagree about everything that matters: RoadAsset carries its own
    // Width, Depth, OffsetAlongNormal, RepeatDistanceScale and texture (Bundles/RoadAsset.cs), so a road
    // rendered off the legacy table's entry number instead comes out the wrong width, at the wrong
    // height and with the wrong surface. Empty means the road really does use the legacy table — which
    // is every one of PEI's 23 roads, so nothing shipped changes shape today; a map that migrated to
    // RoadAssets is what this is kept for.
    public readonly Guid RoadAssetGuid;

    public PlacedRoad(int material, bool isLoop, IReadOnlyList<RoadJoint> joints, Guid roadAssetGuid = default)
    {
        Material = material;
        IsLoop = isLoop;
        Joints = joints;
        RoadAssetGuid = roadAssetGuid;
    }
}

// A road material's shape: width across, and the texture repeat height used for UVs along the road.
public readonly struct RoadMaterialConfig
{
    public readonly float Width;
    public readonly float Height; // divides the texture height to get metres per texture repeat
    public readonly float Depth;
    public readonly float Offset;
    public readonly bool IsConcrete;

    public RoadMaterialConfig(float width, float height, float depth, float offset, bool isConcrete)
    {
        Width = width;
        Height = height;
        Depth = depth;
        Offset = offset;
        IsConcrete = isConcrete;
    }
}

// Ports Unturned's road files: Environment/Roads.dat (per-material width/height, LevelRoads.load) and
// Environment/Paths.dat (the Bezier road splines). Together with the Roads.unity3d textures these are the
// paved highways and dirt roads that connect the map's towns.
public static class LevelRoads
{
    private const byte PathsAddedRoadAsset = 6; // SAVEDATA_PATHS_VERSION_ADDED_ROAD_ASSET

    public static List<RoadMaterialConfig> LoadMaterials(string roadsDatPath)
    {
        var result = new List<RoadMaterialConfig>();
        if (!File.Exists(roadsDatPath))
            return result;

        using var river = new River(roadsDatPath);
        byte version = river.ReadByte();
        if (version < 1)
            return result;

        byte count = river.ReadByte();
        for (int i = 0; i < count; i++)
        {
            float width = river.ReadSingle();
            float height = river.ReadSingle();
            float depth = river.ReadSingle();
            float offset = version > 1 ? river.ReadSingle() : 0f;
            bool isConcrete = river.ReadBoolean();
            result.Add(new RoadMaterialConfig(width, height, depth, offset, isConcrete));
        }
        return result;
    }

    public static List<PlacedRoad> LoadPaths(string pathsDatPath)
    {
        var result = new List<PlacedRoad>();
        if (!File.Exists(pathsDatPath))
            return result;

        using var river = new River(pathsDatPath);
        byte version = river.ReadByte();
        if (version <= 1)
            return result;

        ushort count = river.ReadUInt16();
        for (int i = 0; i < count; i++)
        {
            ushort length = river.ReadUInt16();
            byte material = river.ReadByte();
            bool isLoop = version > 2 && river.ReadBoolean();
            Guid roadAssetGuid = version >= PathsAddedRoadAsset ? river.ReadGuid() : Guid.Empty;

            var joints = new RoadJoint[length];
            for (int step = 0; step < length; step++)
            {
                Vector3 vertex = river.ReadSingleVector3();
                Vector3 tangent0 = Vector3.Zero;
                Vector3 tangent1 = Vector3.Zero;
                if (version > 2)
                {
                    tangent0 = river.ReadSingleVector3();
                    tangent1 = river.ReadSingleVector3();
                    river.ReadByte(); // ERoadMode (unused)
                }
                float offset = version > 4 ? river.ReadSingle() : 0f;
                bool ignoreTerrain = version > 3 && river.ReadBoolean();
                joints[step] = new RoadJoint(vertex, tangent0, tangent1, offset, ignoreTerrain);
            }
            result.Add(new PlacedRoad(material, isLoop, joints, roadAssetGuid));
        }
        return result;
    }

    // Cubic Bezier point on the segment from a to b (BezierTool.getPosition with Unturned's control
    // points: a.Vertex, a.Vertex + a.Tangent1, b.Vertex + b.Tangent0, b.Vertex).
    public static Vector3 BezierPoint(RoadJoint a, RoadJoint b, float t)
    {
        Vector3 p0 = a.Vertex;
        Vector3 p1 = a.Vertex + a.Tangent1;
        Vector3 p2 = b.Vertex + b.Tangent0;
        Vector3 p3 = b.Vertex;
        float u = 1f - t;
        return (u * u * u * p0) + (3f * u * u * t * p1) + (3f * u * t * t * p2) + (t * t * t * p3);
    }
}
