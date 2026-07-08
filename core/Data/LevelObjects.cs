using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

public readonly struct PlacedObject
{
    public readonly Vector3 Position;      // Unity world space
    public readonly Vector3 EulerDegrees;
    public readonly Vector3 Scale;
    public readonly ushort Id;             // legacy identifier
    public readonly Guid Guid;             // modern identifier

    public PlacedObject(Vector3 position, Vector3 euler, Vector3 scale, ushort id, Guid guid)
    {
        Position = position;
        EulerDegrees = euler;
        Scale = scale;
        Id = id;
        Guid = guid;
    }
}

// Ports LevelObjects.load (Unturned/Level/LevelObjects.cs) for SAVEDATA_VERSION 12.
public static class LevelObjects
{
    public const int WORLD_SIZE = 64; // Regions.WORLD_SIZE: 64x64 region grid

    public static List<PlacedObject> Load(string objectsDatPath)
    {
        var result = new List<PlacedObject>();
        if (!File.Exists(objectsDatPath))
            return result;

        using var river = new River(objectsDatPath);

        byte version = river.ReadByte();

        if (version > 1 && version < 3)
            river.ReadUInt64();

        if (version > 8)
            river.ReadUInt32(); // availableInstanceID

        for (int x = 0; x < WORLD_SIZE; x++)
        {
            for (int y = 0; y < WORLD_SIZE; y++)
            {
                ushort count = river.ReadUInt16();
                for (int i = 0; i < count; i++)
                {
                    Vector3 position = river.ReadSingleVector3();
                    Vector3 euler = river.ReadEulerDegrees();
                    Vector3 scale = version > 3 ? river.ReadSingleVector3() : Vector3.One;
                    ushort id = river.ReadUInt16();

                    if (version >= 6 && version <= 9)
                        river.ReadString(); // legacy name

                    Guid guid = version > 7 ? river.ReadGuid() : Guid.Empty;

                    if (version > 6)
                        river.ReadByte(); // placementOrigin

                    if (version > 8)
                        river.ReadUInt32(); // instanceID

                    if (version >= 11)
                    {
                        river.ReadGuid();   // materialPaletteOverride
                        river.ReadInt32();  // materialIndexOverride
                    }

                    if (version >= 12)
                        river.ReadBoolean(); // isOwnedCullingVolumeAllowed

                    // Unturned discards entries with no identifier.
                    if (guid != Guid.Empty || id != 0)
                        result.Add(new PlacedObject(position, euler, scale, id, guid));
                }
            }
        }

        return result;
    }
}
