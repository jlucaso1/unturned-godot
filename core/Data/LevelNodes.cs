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

// The radius every volume node stores on disk is NORMALIZED, not metres.
//
// Each node type keeps a 0..1 slider — SafezoneNode._normalizedRadius and friends — and turns it into a
// world radius the same way, with its own end points:
//
//     public static float CalculateRadiusFromNormalizedRadius(float normalizedRadius)
//         => Mathf.Lerp(MIN_SIZE, MAX_SIZE, normalizedRadius) * 0.5f;
//
// (SafezoneNode.cs:36, DeadzoneNode.cs:78, ArenaNode.cs:36, PurchaseNode.cs:36, EffectNode.cs:57 —
// the same three lines five times, with MIN/MAX of 32/1024, 32/1024, 128/8192, 2/16 and 8/256.)
//
// The port used to keep the raw float and call it a radius in metres, which made every safezone the
// game ships a volume nothing could ever be inside: Alpha Valley's slider is 0.02, so its 26-metre
// safezone read as a 2 CENTIMETRE one. Both numbers are kept below — the slider because it is what the
// file says and what a save would have to write back, and the metres because that is the only form a
// containment test can use.
internal static class NodeRadius
{
    // UnityEngine.Mathf.Lerp CLAMPS its weight to 0..1 and Godot's Mathf.Lerp does not, so the clamp is
    // spelled out rather than left to the engine's version of the same name. It is not hypothetical
    // tidiness: a hand-edited or truncated Nodes.dat carrying a slider of 10 would be a four-kilometre
    // safezone here and a 512-metre one in the game.
    public static float FromNormalized(float normalized, float minSize, float maxSize) =>
        Mathf.Lerp(minSize, maxSize, Mathf.Clamp(normalized, 0f, 1f)) * 0.5f;
}

// SafezoneNode: a volume the game treats as safe. The three flags are the gameplay rules attached to it
// — SafezoneManager.checkPointValid rejects a zombie respawn inside one (ZombieManager.cs:1368), and
// noWeapons is the gate PlayerEquipment's swing is waiting on.
public readonly struct SafezoneNode
{
    public const float MinSize = 32f, MaxSize = 1024f; // SafezoneNode.MIN_SIZE / MAX_SIZE

    public readonly Vector3 Position;

    // The 0..1 slider as the file stores it, and the metres it means. See NodeRadius.
    public readonly float NormalizedRadius;
    public float Radius => NodeRadius.FromNormalized(NormalizedRadius, MinSize, MaxSize);

    // "isHeight". NOT a vertical bound: it is the paintball arena hack, and LevelNodes.AutoConvertLegacy-
    // Volumes says so outright when it converts one into a modern volume (LevelNodes.cs:141-155):
    //
    //     if (safezoneNode.isHeight)
    //         // This type was hacked-in for the paintball arena event. It was an infinite plane above
    //         // the selected point, so we approximate that with a giant box.
    //         volumeTransform.position = node.point + new Vector3(0.0f, 1000.0f, 0.0f);
    //         volumeTransform.localScale = new Vector3(10000.0f, 2000.0f, 10000.0f);
    //     else
    //         volume.SetSphereRadius(CalculateRadiusFromNormalizedRadius(...));
    //
    // So the flag chooses between "everything above this point" and a plain sphere — and the RADIUS is
    // only read in the second case, which is why Paintball_Arena_0's zone ships with a slider of zero.
    public readonly bool IsHeight;
    public readonly bool NoWeapons;
    public readonly bool NoBuildables;

    public SafezoneNode(Vector3 position, float normalizedRadius, bool isHeight, bool noWeapons,
        bool noBuildables)
    {
        Position = position;
        NormalizedRadius = normalizedRadius;
        IsHeight = isHeight;
        NoWeapons = noWeapons;
        NoBuildables = noBuildables;
    }

    // Half the box's own extents: localScale 10000 x 2000 x 10000 on Unity's 1x1x1 box collider,
    // centred a thousand metres above the node.
    private const float HeightBoxHalfWidth = 5000f;
    private const float HeightBoxHalfHeight = 1000f;

    public bool Contains(Vector3 point)
    {
        if (!IsHeight)
        {
            float radius = Radius;
            return Position.DistanceSquaredTo(point) < radius * radius;
        }
        // The giant box: the two kilometres of air starting at the node, and five kilometres out in
        // every horizontal direction — which on any map the game ships is "everywhere above it".
        float centreY = Position.Y + HeightBoxHalfHeight;
        return Mathf.Abs(point.X - Position.X) <= HeightBoxHalfWidth
            && Mathf.Abs(point.Z - Position.Z) <= HeightBoxHalfWidth
            && Mathf.Abs(point.Y - centreY) <= HeightBoxHalfHeight;
    }
}

// DeadzoneNode: the contaminated volumes. Always a sphere.
public readonly struct DeadzoneNode
{
    public const float MinSize = 32f, MaxSize = 1024f; // DeadzoneNode.MIN_SIZE / MAX_SIZE

    public readonly Vector3 Position;
    public readonly float NormalizedRadius;
    public float Radius => NodeRadius.FromNormalized(NormalizedRadius, MinSize, MaxSize);
    public readonly EDeadzoneType Type;

    public DeadzoneNode(Vector3 position, float normalizedRadius, EDeadzoneType type)
    {
        Position = position;
        NormalizedRadius = normalizedRadius;
        Type = type;
    }

    public bool Contains(Vector3 point)
    {
        float radius = Radius;
        return Position.DistanceSquaredTo(point) < radius * radius;
    }
}

// PurchaseNode: a vendor volume — buy item `Id` for `Cost` inside `Radius`.
public readonly struct PurchaseNode
{
    public const float MinSize = 2f, MaxSize = 16f; // PurchaseNode.MIN_SIZE / MAX_SIZE

    public readonly Vector3 Position;
    public readonly float NormalizedRadius;
    public float Radius => NodeRadius.FromNormalized(NormalizedRadius, MinSize, MaxSize);
    public readonly ushort Id;
    public readonly uint Cost;

    public PurchaseNode(Vector3 position, float normalizedRadius, ushort id, uint cost)
    {
        Position = position;
        NormalizedRadius = normalizedRadius;
        Id = id;
        Cost = cost;
    }
}

// ArenaNode: the shrinking-circle volume the arena gamemode plays inside.
public readonly struct ArenaNode
{
    public const float MinSize = 128f, MaxSize = 8192f; // ArenaNode.MIN_SIZE / MAX_SIZE

    public readonly Vector3 Position;
    public readonly float NormalizedRadius;
    public float Radius => NodeRadius.FromNormalized(NormalizedRadius, MinSize, MaxSize);

    public ArenaNode(Vector3 position, float normalizedRadius)
    {
        Position = position;
        NormalizedRadius = normalizedRadius;
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
    public const float MinSize = 8f, MaxSize = 256f; // EffectNode.MIN_SIZE / MAX_SIZE

    public readonly Vector3 Position;
    public readonly byte Shape;     // ENodeShape: 0 sphere, 1 box
    public readonly float NormalizedRadius;
    public float Radius => NodeRadius.FromNormalized(NormalizedRadius, MinSize, MaxSize);
    public readonly Vector3 Bounds; // box extents, when Shape is a box
    public readonly ushort EffectId;
    public readonly bool NoWater;
    public readonly bool NoLighting;

    public EffectNode(Vector3 position, byte shape, float normalizedRadius, Vector3 bounds,
        ushort effectId, bool noWater, bool noLighting)
    {
        Position = position;
        Shape = shape;
        NormalizedRadius = normalizedRadius;
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
    //
    // LevelNodes.isPointInsideSafezone, which is the one query PlayerMovement makes per player: it
    // answers `isSafe` and fills in `isSafeInfo` at once, and the punch gate needs both. The controller
    // asks it at the player's feet — see PlayerController.CurrentHandState.
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
                        // TRUE, not false, for a file too old to carry them: LevelNodes.load declares
                        // `bool noWeapons = true;` and `bool noBuildables = true;` and only overwrites
                        // them when version > 4. A safezone predating the flags is the strictest kind,
                        // and defaulting them off silently re-armed the fist inside every one of them.
                        bool noWeapons = true, noBuildables = true;
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
                    // "Max diameter was doubled from 4096 to 8192 in v6", and the fix-up is applied to
                    // the SLIDER rather than to the metres, so an older file lands on the same volume
                    // through the new end points.
                    result.Arenas.Add(new ArenaNode(godot,
                        version < 6 ? river.ReadSingle() * 0.5f : river.ReadSingle()));
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
