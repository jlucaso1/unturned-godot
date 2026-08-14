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
    public readonly ushort Id;             // legacy identifier (pre-GUID maps)
    public readonly Guid Guid;             // modern identifier

    // The file this tree came out of predates version 8, so the identity rotation and unit scale above
    // are placeholders rather than authored values: the file stored neither, and Unturned derives both
    // from the asset and the position instead (LevelGround.cs:782, and again at 873 and 908 for the
    // region encodings). Nothing here can do that derivation — it needs the ResourceAsset — so the fact
    // that it is still owed travels with the tree until LegacyPlacements has the asset database.
    public readonly bool NeedsLegacyRotationAndScale;

    public PlacedTree(Vector3 position, Vector3 euler, Vector3 scale, Guid guid, ushort id = 0,
        bool needsLegacyRotationAndScale = false)
    {
        Position = position;
        EulerDegrees = euler;
        Scale = scale;
        Id = id;
        Guid = guid;
        NeedsLegacyRotationAndScale = needsLegacyRotationAndScale;
    }
}

// Ports LevelGround's Trees.dat reader (Unturned/Level/LevelGround.cs).
//
// Two encodings ship in the official maps. From version 7 the file is a flat list: a GUID, a position and
// (from 8) an Euler rotation and scale per tree. Before that the trees are bucketed into Regions' 64x64
// grid, each cell prefixed with its count, and each tree carries the legacy ushort id it was placed with —
// plus, from version 6, the GUID that replaced it. Alpha Valley (6) and Monolith (5) are still written that
// way, and skipping those files left both maps without a single tree.
public static class LevelTrees
{
    private const byte GuidVersion = 6;             // SAVEDATA_TREES_VERSION_GUID
    private const byte IntRegionCoordsVersion = 7;  // SAVEDATA_TREES_VERSION_INT_REGION_COORDS
    private const byte RotationAndScaleVersion = 8;  // SAVEDATA_TREES_VERSION_ROTATION_AND_SCALE
    private const int WorldSize = LevelObjects.WORLD_SIZE; // Regions.WORLD_SIZE

    public static List<PlacedTree> Load(string treesDatPath)
    {
        var result = new List<PlacedTree>();
        if (!File.Exists(treesDatPath))
            return result;

        using var river = new River(treesDatPath);

        byte version = river.ReadByte();
        if (version == 0)
            return result;
        if (version < IntRegionCoordsVersion)
            return LoadRegions(river, version, result);

        int count = river.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            Guid guid = river.ReadGuid();
            Vector3 position = river.ReadSingleVector3();

            Vector3 euler = Vector3.Zero;
            Vector3 scale = Vector3.One;
            bool needsLegacy = version < RotationAndScaleVersion;
            if (!needsLegacy)
            {
                euler = river.ReadEulerDegrees();
                scale = river.ReadSingleVector3();
            }

            river.ReadBoolean(); // isGenerated (procedural placement flag, unused for rendering)

            // Unturned discards trees with no asset.
            if (guid != Guid.Empty)
                result.Add(new PlacedTree(position, euler, scale, guid, id: 0, needsLegacy));
        }

        return result;
    }

    // The pre-7 encoding: one ushort count per region of the 64x64 grid, then that region's trees.
    // Neither rotation nor scale exists yet (both arrived in 8), so every tree here is flagged as still
    // owing both — LegacyPlacements derives them from the asset once it has one.
    private static List<PlacedTree> LoadRegions(River river, byte version, List<PlacedTree> result)
    {
        for (int x = 0; x < WorldSize; x++)
        {
            for (int y = 0; y < WorldSize; y++)
            {
                ushort count = river.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    ushort id = river.ReadUInt16();
                    Guid guid = version >= GuidVersion ? river.ReadGuid() : Guid.Empty;
                    Vector3 position = river.ReadSingleVector3();
                    river.ReadBoolean(); // isGenerated

                    // Unturned discards trees with no asset; before 6 that identity is the legacy id alone.
                    if (guid != Guid.Empty || id != 0)
                    {
                        result.Add(new PlacedTree(position, Vector3.Zero, Vector3.One, guid, id,
                            needsLegacyRotationAndScale: true));
                    }
                }
            }
        }

        return result;
    }
}
