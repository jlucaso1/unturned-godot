using System.Collections.Generic;
using System.IO;
using Godot;

namespace UnturnedGodot.Data;

// A named place on the map (town or landmark), converted to Godot world space.
public readonly struct LocationNode
{
    public readonly Vector3 Position;
    public readonly string Name;

    public LocationNode(Vector3 position, string name)
    {
        Position = position;
        Name = name;
    }
}

// SDG.Unturned.EDeadzoneType — which contamination a deadzone applies.
public enum EDeadzoneType : byte
{
    Default = 0,
    Radiation = 1,
    Bloodthirst = 2,
}

// SafezoneNode: a volume the game treats as safe. The three flags are the gameplay rules attached to it
// — SafezoneManager.checkPointValid rejects a zombie respawn inside one (ZombieManager.cs:1368), and
// noWeapons is the gate PlayerEquipment's swing is waiting on.
public readonly struct SafezoneNode
{
    public readonly Vector3 Position;
    public readonly float Radius;

    // "isHeight": whether the volume is bounded vertically. False makes it an infinite cylinder, which
    // is what a town safezone wants — a building's roof is inside it too.
    public readonly bool IsHeight;
    public readonly bool NoWeapons;
    public readonly bool NoBuildables;

    public SafezoneNode(Vector3 position, float radius, bool isHeight, bool noWeapons, bool noBuildables)
    {
        Position = position;
        Radius = radius;
        IsHeight = isHeight;
        NoWeapons = noWeapons;
        NoBuildables = noBuildables;
    }

    // Node.isPointInside for a safezone: a sphere when isHeight, otherwise the XZ disc at any height.
    public bool Contains(Vector3 point)
    {
        if (IsHeight)
            return Position.DistanceSquaredTo(point) < Radius * Radius;
        float dx = point.X - Position.X;
        float dz = point.Z - Position.Z;
        return (dx * dx) + (dz * dz) < Radius * Radius;
    }
}

// DeadzoneNode: the contaminated volumes. Always a sphere.
public readonly struct DeadzoneNode
{
    public readonly Vector3 Position;
    public readonly float Radius;
    public readonly EDeadzoneType Type;

    public DeadzoneNode(Vector3 position, float radius, EDeadzoneType type)
    {
        Position = position;
        Radius = radius;
        Type = type;
    }

    public bool Contains(Vector3 point) => Position.DistanceSquaredTo(point) < Radius * Radius;
}

// PurchaseNode: a vendor volume — buy item `Id` for `Cost` inside `Radius`.
public readonly struct PurchaseNode
{
    public readonly Vector3 Position;
    public readonly float Radius;
    public readonly ushort Id;
    public readonly uint Cost;

    public PurchaseNode(Vector3 position, float radius, ushort id, uint cost)
    {
        Position = position;
        Radius = radius;
        Id = id;
        Cost = cost;
    }
}

// ArenaNode: the shrinking-circle volume the arena gamemode plays inside.
public readonly struct ArenaNode
{
    public readonly Vector3 Position;
    public readonly float Radius;

    public ArenaNode(Vector3 position, float radius)
    {
        Position = position;
        Radius = radius;
    }
}

// AirdropNode: where a drop lands, and which spawn table fills it.
public readonly struct AirdropNode
{
    public readonly Vector3 Position;
    public readonly ushort SpawnTableId;

    public AirdropNode(Vector3 position, ushort spawnTableId)
    {
        Position = position;
        SpawnTableId = spawnTableId;
    }
}

// EffectNode: an ambient effect volume (a waterfall's spray, a chimney's smoke).
public readonly struct EffectNode
{
    public readonly Vector3 Position;
    public readonly byte Shape;     // ENodeShape: 0 sphere, 1 box
    public readonly float Radius;
    public readonly Vector3 Bounds; // box extents, when Shape is a box
    public readonly ushort EffectId;
    public readonly bool NoWater;
    public readonly bool NoLighting;

    public EffectNode(Vector3 position, byte shape, float radius, Vector3 bounds, ushort effectId,
        bool noWater, bool noLighting)
    {
        Position = position;
        Shape = shape;
        Radius = radius;
        Bounds = bounds;
        EffectId = effectId;
        NoWater = noWater;
        NoLighting = noLighting;
    }
}

// Everything Environment/Nodes.dat holds, kept rather than skipped.
public sealed class LevelNodeSet
{
    public readonly List<LocationNode> Locations = new();
    public readonly List<SafezoneNode> Safezones = new();
    public readonly List<PurchaseNode> Purchases = new();
    public readonly List<ArenaNode> Arenas = new();
    public readonly List<DeadzoneNode> Deadzones = new();
    public readonly List<AirdropNode> Airdrops = new();
    public readonly List<EffectNode> Effects = new();

    // SafezoneManager.checkPointValid: is this point inside ANY safezone? The zombie respawner asks it
    // before placing a body, and it is the gate the punch will want.
    public bool IsPointInSafezone(Vector3 point)
    {
        foreach (SafezoneNode zone in Safezones)
            if (zone.Contains(point))
                return true;
        return false;
    }

    // The safezone containing this point, so a caller can read its noWeapons/noBuildables rules rather
    // than only learning that some safezone is there. Null when the point is outside all of them.
    public SafezoneNode? SafezoneAt(Vector3 point)
    {
        foreach (SafezoneNode zone in Safezones)
            if (zone.Contains(point))
                return zone;
        return null;
    }

    public DeadzoneNode? DeadzoneAt(Vector3 point)
    {
        foreach (DeadzoneNode zone in Deadzones)
            if (zone.Contains(point))
                return zone;
        return null;
    }
}

// Ports LevelNodes.load (Environment/Nodes.dat): the map's nodes — named LOCATION markers plus the
// gameplay volumes (safezone, purchase, arena, deadzone, airdrop, effect). Little-endian; current
// on-disk version is 9. An unknown node type stops parsing, since its length is unknown.
//
// Every type except LOCATION used to be parsed purely to advance the cursor: the radii, the flags and
// the ids were read off the file and dropped on the floor. A map's safezones — the volumes deciding
// where a zombie may respawn and where a weapon may be swung — were invisible to this port even though
// it had just finished reading them.
public static class LevelNodes
{
    private const byte Location = 0, Safezone = 1, Purchase = 2, Arena = 3, Deadzone = 4, Airdrop = 5, Effect = 6;

    // Kept as the narrow entry point everything that only wants place names already calls.
    public static List<LocationNode> LoadLocations(string nodesDatPath) => Load(nodesDatPath).Locations;

    public static LevelNodeSet Load(string nodesDatPath)
    {
        var result = new LevelNodeSet();
        if (!File.Exists(nodesDatPath))
            return result;

        using var river = new River(nodesDatPath);
        byte version = river.ReadByte();
        if (version == 0)
            return result;

        int count = river.ReadByte();
        for (int i = 0; i < count; i++)
        {
            Vector3 point = river.ReadSingleVector3();
            Vector3 godot = Landscape.UnityToGodot(point);
            byte type = river.ReadByte();
            switch (type)
            {
                case Location:
                    result.Locations.Add(new LocationNode(godot, river.ReadString()));
                    break;

                case Safezone:
                {
                    float radius = river.ReadSingle();
                    bool isHeight = version > 1 && river.ReadBoolean();
                    bool noWeapons = false, noBuildables = false;
                    if (version > 4)
                    {
                        noWeapons = river.ReadBoolean();
                        noBuildables = river.ReadBoolean();
                    }
                    result.Safezones.Add(
                        new SafezoneNode(godot, radius, isHeight, noWeapons, noBuildables));
                    break;
                }

                case Purchase:
                {
                    float radius = river.ReadSingle();
                    ushort id = river.ReadUInt16();
                    uint cost = river.ReadUInt32();
                    result.Purchases.Add(new PurchaseNode(godot, radius, id, cost));
                    break;
                }

                case Arena:
                    result.Arenas.Add(new ArenaNode(godot, river.ReadSingle()));
                    break;

                case Deadzone:
                {
                    float radius = river.ReadSingle();
                    var kind = version > 6 ? (EDeadzoneType)river.ReadByte() : EDeadzoneType.Default;
                    result.Deadzones.Add(new DeadzoneNode(godot, radius, kind));
                    break;
                }

                case Airdrop:
                    result.Airdrops.Add(new AirdropNode(godot, river.ReadUInt16()));
                    break;

                case Effect:
                {
                    byte shape = version > 2 ? river.ReadByte() : (byte)0;
                    float radius = river.ReadSingle();
                    Vector3 bounds = version > 2 ? river.ReadSingleVector3() : Vector3.Zero;
                    ushort effectId = river.ReadUInt16();
                    bool noWater = river.ReadBoolean();
                    bool noLighting = version > 3 && river.ReadBoolean();
                    result.Effects.Add(
                        new EffectNode(godot, shape, radius, bounds, effectId, noWater, noLighting));
                    break;
                }

                default:
                    return result; // unknown type: its length is unknown, so we can't advance safely
            }
        }
        return result;
    }
}
