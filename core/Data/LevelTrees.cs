using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// One placed tree: a ResourceAsset GUID plus its transform. Trees are Unturned "resources" (harvestable),
// stored separately from objects in Terrain/Trees.dat, which is why the map looks bare without them.
public readonly struct PlacedTree
{
    public readonly Vector3 Position;      // Unity world space
    public readonly Vector3 EulerDegrees;
    public readonly Vector3 Scale;
    public readonly Guid Guid;

    public PlacedTree(Vector3 position, Vector3 euler, Vector3 scale, Guid guid)
    {
        Position = position;
        EulerDegrees = euler;
        Scale = scale;
        Guid = guid;
    }
}

// Ports LevelGround's Trees.dat reader (Unturned/Level/LevelGround.cs) for the current flat-list format
// (version >= 7). Each tree stores a GUID, position, Euler rotation and scale. The pre-7 region-grid
// encoding is not written by current maps and is skipped.
public static class LevelTrees
{
    private const byte IntRegionCoordsVersion = 7;  // SAVEDATA_TREES_VERSION_INT_REGION_COORDS
    private const byte RotationAndScaleVersion = 8;  // SAVEDATA_TREES_VERSION_ROTATION_AND_SCALE

    public static List<PlacedTree> Load(string treesDatPath)
    {
        var result = new List<PlacedTree>();
        if (!File.Exists(treesDatPath))
            return result;

        using var river = new River(treesDatPath);

        byte version = river.ReadByte();
        if (version < IntRegionCoordsVersion)
            return result;

        int count = river.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            Guid guid = river.ReadGuid();
            Vector3 position = river.ReadSingleVector3();

            Vector3 euler = Vector3.Zero;
            Vector3 scale = Vector3.One;
            if (version >= RotationAndScaleVersion)
            {
                euler = river.ReadEulerDegrees();
                scale = river.ReadSingleVector3();
            }

            river.ReadBoolean(); // isGenerated (procedural placement flag, unused for rendering)

            // Unturned discards trees with no asset.
            if (guid != Guid.Empty)
                result.Add(new PlacedTree(position, euler, scale, guid));
        }

        return result;
    }
}
