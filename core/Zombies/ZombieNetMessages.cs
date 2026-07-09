using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Net;

namespace UnturnedGodot.Zombies;

// A zombie as the client learns it on admission: identity + look, plus where it stands right now.
public struct ZombieListing
{
    public ushort Id;
    public byte Type;
    public EZombieSpeciality Speciality;
    public byte Shirt;
    public byte Pants;
    public byte Hat;
    public byte Gear;
    public byte Move; // animation variant seeds, rolled server-side and replicated (Move_N/Idle_N)
    public byte Idle;
    public Vector3 Position;
    public byte Yaw;
}

// A zombie's per-tick replicated state — only sent for zombies that are awake (non-idle).
public struct ZombieSnapshotState
{
    public ushort Id;
    public Vector3 Position;
    public byte Yaw;
    public EZombieState State;
}

public static class ZombieNetMessages
{
    // Reliable datagrams are not fragmented, so the full population ships in MTU-sized chunks.
    public const int ListChunkSize = 50;

    public static byte[] WriteZombieList(IReadOnlyList<ZombieListing> chunk)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.ZombieList);
        w.Write((byte)chunk.Count);
        foreach (ZombieListing z in chunk)
        {
            w.Write(z.Id);
            w.Write(z.Type);
            w.Write((byte)z.Speciality);
            w.Write(z.Shirt);
            w.Write(z.Pants);
            w.Write(z.Hat);
            w.Write(z.Gear);
            w.Write(z.Move);
            w.Write(z.Idle);
            w.Write(z.Position.X);
            w.Write(z.Position.Y);
            w.Write(z.Position.Z);
            w.Write(z.Yaw);
        }
        return ms.ToArray();
    }

    public static List<ZombieListing> ReadZombieList(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        int count = r.ReadByte();
        var chunk = new List<ZombieListing>(count);
        for (int i = 0; i < count; i++)
        {
            chunk.Add(new ZombieListing
            {
                Id = r.ReadUInt16(),
                Type = r.ReadByte(),
                Speciality = (EZombieSpeciality)r.ReadByte(),
                Shirt = r.ReadByte(),
                Pants = r.ReadByte(),
                Hat = r.ReadByte(),
                Gear = r.ReadByte(),
                Move = r.ReadByte(),
                Idle = r.ReadByte(),
                Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Yaw = r.ReadByte(),
            });
        }
        return chunk;
    }

    public static byte[] WriteZombieStates(uint tick, IReadOnlyList<ZombieSnapshotState> states)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.ZombieStates);
        w.Write(tick);
        w.Write((byte)states.Count);
        foreach (ZombieSnapshotState s in states)
        {
            w.Write(s.Id);
            w.Write(s.Position.X);
            w.Write(s.Position.Y);
            w.Write(s.Position.Z);
            w.Write(s.Yaw);
            w.Write((byte)s.State);
        }
        return ms.ToArray();
    }

    public static (uint Tick, List<ZombieSnapshotState> States) ReadZombieStates(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        uint tick = r.ReadUInt32();
        int count = r.ReadByte();
        var states = new List<ZombieSnapshotState>(count);
        for (int i = 0; i < count; i++)
        {
            states.Add(new ZombieSnapshotState
            {
                Id = r.ReadUInt16(),
                Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                Yaw = r.ReadByte(),
                State = (EZombieState)r.ReadByte(),
            });
        }
        return (tick, states);
    }

    private static BinaryReader Reader(byte[] payload)
    {
        var r = new BinaryReader(new MemoryStream(payload));
        r.ReadByte(); // message type
        return r;
    }
}
