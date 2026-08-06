using System;
using System.Buffers.Binary;
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

// Zombie.OnUpdate decides whether the client should leave idle/startle for the movement clip by
// comparing each component of the latest authoritative position with the currently rendered body.
// Keep this next to the snapshot shape so every client uses the exact original 1 cm threshold.
public static class ZombieClientMotion
{
    public const float MovementThreshold = 0.01f;

    public static bool IsMoving(Vector3 latest, Vector3 rendered) =>
        MathF.Abs(latest.X - rendered.X) > MovementThreshold
        || MathF.Abs(latest.Y - rendered.Y) > MovementThreshold
        || MathF.Abs(latest.Z - rendered.Z) > MovementThreshold;
}

public static class ZombieNetMessages
{
    // Reliable datagrams are not fragmented, so the full population ships in MTU-sized chunks.
    public const int ListChunkSize = 50;

    private const int ListingBytes = 23;  // id 2 + look bytes 8 + position 12 + yaw 1
    private const int SnapshotBytes = 16; // id 2 + position 12 + yaw 1 + state 1

    // These payloads are rebuilt every server tick, so they are written straight into an exact-sized
    // array (same little-endian bytes a BinaryWriter produced) instead of growing a MemoryStream.
    // A list carries ONE region's zombies (SendZombies ships per region, to one connection): the bound
    // rides in the header so the client can group avatars by region and drop them on region change.
    public static byte[] WriteZombieList(byte bound, IReadOnlyList<ZombieListing> chunk)
    {
        var payload = new byte[3 + (chunk.Count * ListingBytes)];
        payload[0] = (byte)ENetMessage.ZombieList;
        payload[1] = bound;
        payload[2] = (byte)chunk.Count;
        int o = 3;
        for (int i = 0; i < chunk.Count; i++)
        {
            ZombieListing z = chunk[i];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(o), z.Id);
            payload[o + 2] = z.Type;
            payload[o + 3] = (byte)z.Speciality;
            payload[o + 4] = z.Shirt;
            payload[o + 5] = z.Pants;
            payload[o + 6] = z.Hat;
            payload[o + 7] = z.Gear;
            payload[o + 8] = z.Move;
            payload[o + 9] = z.Idle;
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 10), z.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 14), z.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 18), z.Position.Z);
            payload[o + 22] = z.Yaw;
            o += ListingBytes;
        }
        return payload;
    }

    public static (byte Bound, List<ZombieListing> Listings) ReadZombieList(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        byte bound = r.ReadByte();
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
        return (bound, chunk);
    }

    public static byte[] WriteZombieStates(uint tick, IReadOnlyList<ZombieSnapshotState> states)
    {
        var payload = new byte[6 + (states.Count * SnapshotBytes)];
        payload[0] = (byte)ENetMessage.ZombieStates;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), tick);
        payload[5] = (byte)states.Count;
        int o = 6;
        for (int i = 0; i < states.Count; i++)
        {
            ZombieSnapshotState s = states[i];
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(o), s.Id);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 2), s.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 6), s.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 10), s.Position.Z);
            payload[o + 14] = s.Yaw;
            payload[o + 15] = (byte)s.State;
            o += SnapshotBytes;
        }
        return payload;
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

    // The zombies that died this tick, for one region. Reliable and event-shaped for the same reason a
    // gesture is: a death is a moment, and the state stream carries no "alive" bit a client could infer
    // it from — an unreplicated death simply leaves the avatar standing there, since the server stops
    // sending snapshots for a zombie that no longer exists.
    //
    // The bound rides along like the list's does, so a client can ignore a death in a region it has
    // already walked out of rather than reaching into avatars it is about to drop wholesale anyway.
    //
    // One byte of count, like the list and snapshot payloads: a region holds at most 255 zombies
    // (NavBound.MaxZombies is a byte), so a single tick's deaths cannot overflow it.
    public static byte[] WriteZombieKilled(byte bound, IReadOnlyList<ushort> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        // The count header is one byte, and a silent truncation here would be the worst possible
        // failure: a payload advertising zero ids that the reader believes, leaving every one of those
        // corpses standing forever. A region cannot hold more than 255 zombies, so this can only fire
        // on a caller that has already gone wrong somewhere else.
        if (ids.Count > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ids),
                $"a ZombieKilled payload carries at most {byte.MaxValue} ids, not {ids.Count}");
        var payload = new byte[3 + (ids.Count * 2)];
        payload[0] = (byte)ENetMessage.ZombieKilled;
        payload[1] = bound;
        payload[2] = (byte)ids.Count;
        for (int i = 0; i < ids.Count; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3 + (i * 2)), ids[i]);
        return payload;
    }

    public static (byte Bound, List<ushort> Ids) ReadZombieKilled(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        byte bound = r.ReadByte();
        int count = r.ReadByte();
        var ids = new List<ushort>(count);
        for (int i = 0; i < count; i++)
            ids.Add(r.ReadUInt16());
        return (bound, ids);
    }

    // ZombieManager.sendZombieStun: which zombies staggered, and which reel each plays.
    //
    // Reliable, like a kill, and for the same reason: a dropped stun leaves a body standing still on the
    // server while the client keeps walking it, and nothing later says otherwise. Unlike a kill, though,
    // a stun is not final — the next state snapshot will move the zombie again — so this is bounded by
    // the same one-byte count and addressed to the same region.
    public static byte[] WriteZombieStunned(byte bound, IReadOnlyList<(ushort Id, byte Clip)> stuns)
    {
        ArgumentNullException.ThrowIfNull(stuns);
        if (stuns.Count > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(stuns),
                $"a ZombieStunned payload carries at most {byte.MaxValue} zombies, not {stuns.Count}");

        var payload = new byte[3 + (stuns.Count * 3)];
        payload[0] = (byte)ENetMessage.ZombieStunned;
        payload[1] = bound;
        payload[2] = (byte)stuns.Count;
        for (int i = 0; i < stuns.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(3 + (i * 3)), stuns[i].Id);
            payload[5 + (i * 3)] = stuns[i].Clip;
        }

        return payload;
    }

    public static (byte Bound, List<(ushort Id, byte Clip)> Stuns) ReadZombieStunned(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        byte bound = r.ReadByte();
        int count = r.ReadByte();
        var stuns = new List<(ushort, byte)>(count);
        for (int i = 0; i < count; i++)
            stuns.Add((r.ReadUInt16(), r.ReadByte()));
        return (bound, stuns);
    }

    private static BinaryReader Reader(byte[] payload)
    {
        var r = new BinaryReader(new MemoryStream(payload));
        r.ReadByte(); // message type
        return r;
    }
}
