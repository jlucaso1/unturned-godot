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

// Ports LevelNodes.load (Environment/Nodes.dat): the map's nodes — named LOCATION markers plus gameplay
// volumes (safezone, purchase, arena, deadzone, airdrop, effect). We keep the LOCATION nodes for labelling
// and parse the others only to advance past their (version-gated) fields. Little-endian; current on-disk
// version is 9. An unknown node type stops parsing, since its length is unknown.
public static class LevelNodes
{
    private const byte Location = 0, Safezone = 1, Purchase = 2, Arena = 3, Deadzone = 4, Airdrop = 5, Effect = 6;

    public static List<LocationNode> LoadLocations(string nodesDatPath)
    {
        var result = new List<LocationNode>();
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
            byte type = river.ReadByte();
            switch (type)
            {
                case Location:
                    result.Add(new LocationNode(Landscape.UnityToGodot(point), river.ReadString()));
                    break;
                case Safezone:
                    river.ReadSingle();                                   // radius
                    if (version > 1) river.ReadBoolean();                 // isHeight
                    if (version > 4) { river.ReadBoolean(); river.ReadBoolean(); } // noWeapons, noBuildables
                    break;
                case Purchase:
                    river.ReadSingle(); river.ReadUInt16(); river.ReadUInt32(); // radius, id, cost
                    break;
                case Arena:
                    river.ReadSingle();                                   // radius
                    break;
                case Deadzone:
                    river.ReadSingle();                                   // radius
                    if (version > 6) river.ReadByte();                    // deadzone type
                    break;
                case Airdrop:
                    river.ReadUInt16();                                   // spawn table id
                    break;
                case Effect:
                    if (version > 2) river.ReadByte();                    // shape
                    river.ReadSingle();                                   // radius
                    if (version > 2) river.ReadSingleVector3();           // bounds
                    river.ReadUInt16();                                   // effect id
                    river.ReadBoolean();                                  // no water
                    if (version > 3) river.ReadBoolean();                 // no lighting
                    break;
                default:
                    return result; // unknown type: its length is unknown, so we can't advance safely
            }
        }
        return result;
    }
}
