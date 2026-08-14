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
    private const int ListingBytes = 23;  // id 2 + look bytes 8 + position 12 + yaw 1
    private const int SnapshotBytes = 16; // id 2 + position 12 + yaw 1 + state 1
    private const int KilledBytes = 14;   // id 2 + the killing blow's shove 12
    private const int StunnedBytes = 3;   // id 2 + clip 1
    private const int RegionHeaderBytes = 3; // type 1 + bound 1 + count 1
    private const int StatesHeaderBytes = 6; // type 1 + tick 4 + count 1

    // Two ceilings, and a payload has to respect the LOWER of them.
    //
    // The MTU budget is usually the tighter one — sixteen bytes a snapshot means a region's worth is
    // three IP fragments, all of which have to arrive. But every one of these payloads counts its
    // records in a single byte, and where the records are small enough the byte runs out first: three
    // bytes a stagger fits 399 of them inside the budget and the 400th would wrap the header to 144.
    // Taking the minimum is what keeps both true without either being stated twice.
    private static int PerDatagram(int headerBytes, int itemBytes) =>
        Math.Min(NetChunks.Capacity(headerBytes, itemBytes), byte.MaxValue);

    // Reliable datagrams are not fragmented, so the full population ships in MTU-sized chunks. Derived
    // from the budget rather than written down: the hand-picked 50 was right for a 23-byte listing and
    // would have quietly stopped being right the first time a look byte was added to one.
    public static readonly int ListChunkSize = PerDatagram(RegionHeaderBytes, ListingBytes);

    // A region holds up to 255 zombies (NavBound.MaxZombies is a byte), and at 16 bytes a snapshot that
    // is 4 KB on the unreliable stream that carries the whole map's motion.
    public static readonly int MaxStatesPerDatagram = PerDatagram(StatesHeaderBytes, SnapshotBytes);

    public static readonly int MaxKilledPerDatagram = PerDatagram(RegionHeaderBytes, KilledBytes);

    public static readonly int MaxStunnedPerDatagram = PerDatagram(RegionHeaderBytes, StunnedBytes);

    // These payloads are rebuilt every server tick, so they are written straight into an exact-sized
    // array (same little-endian bytes a BinaryWriter produced) instead of growing a MemoryStream.
    // A list carries ONE region's zombies (SendZombies ships per region, to one connection): the bound
    // rides in the header so the client can group avatars by region and drop them on region change.
    public static List<byte[]> WriteZombieLists(byte bound, IReadOnlyList<ZombieListing> listings) =>
        NetChunks.Split(listings, ListChunkSize, chunk => WriteZombieList(bound, chunk));

    public static byte[] WriteZombieList(byte bound, IReadOnlyList<ZombieListing> chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        if (chunk.Count > ListChunkSize)
            throw new ArgumentOutOfRangeException(nameof(chunk),
                $"a ZombieList datagram carries at most {ListChunkSize} listings, not {chunk.Count}; "
                + "use WriteZombieLists, which splits.");
        var payload = new byte[RegionHeaderBytes + (chunk.Count * ListingBytes)];
        payload[0] = (byte)ENetMessage.ZombieList;
        payload[1] = bound;
        payload[2] = (byte)chunk.Count;
        int o = RegionHeaderBytes;
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

    public static List<byte[]> WriteZombieStateChunks(uint tick,
        IReadOnlyList<ZombieSnapshotState> states) =>
        NetChunks.Split(states, MaxStatesPerDatagram, chunk => WriteZombieStates(tick, chunk));

    // Threw nothing and truncated silently at 256: `payload[5] = (byte)states.Count` wrapped, so a
    // 256-zombie tick advertised zero states and the reader believed it — every one of those zombies
    // frozen on the client with nothing to say why. Its two siblings below have always thrown on
    // exactly this, and the asymmetry was the kind that survives the refactor that makes it reachable.
    // The MTU bound below now fires long before the byte one could, but both are stated.
    public static byte[] WriteZombieStates(uint tick, IReadOnlyList<ZombieSnapshotState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count > MaxStatesPerDatagram)
            throw new ArgumentOutOfRangeException(nameof(states),
                $"a ZombieStates datagram carries at most {MaxStatesPerDatagram} zombies, not "
                + $"{states.Count}; use WriteZombieStateChunks, which splits.");
        var payload = new byte[StatesHeaderBytes + (states.Count * SnapshotBytes)];
        payload[0] = (byte)ENetMessage.ZombieStates;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), tick);
        payload[5] = (byte)states.Count;
        int o = StatesHeaderBytes;
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
    //
    // Each death also carries the SHOVE its killing blow gave it, as three plain floats. Quantizing them
    // was the first instinct and the wrong one: the vector is `direction * damage`, so its magnitude runs
    // with the weapon, and any fixed-point scale that resolved a punch would saturate on something
    // heavier. Deaths are rare — a handful per tick at most, against the per-tick snapshot stream beside
    // them — so six bytes a corpse is not worth a range limit to save.
    // Ids alone: every corpse simply drops, which is what a kill with no direction behind it (a burn, a
    // despawn) does in the original too.
    public static byte[] WriteZombieKilled(byte bound, IReadOnlyList<ushort> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var kills = new List<(ushort, Vector3)>(ids.Count);
        foreach (ushort id in ids)
            kills.Add((id, Vector3.Zero));
        return WriteZombieKilled(bound, kills);
    }

    // Each corpse with the shove its killing blow gave it. One list of PAIRS rather than two collections
    // that a caller has to keep in step: a mismatch there would throw a body with another body's blow.
    public static List<byte[]> WriteZombieKilledChunks(byte bound,
        IReadOnlyList<(ushort Id, Vector3 Ragdoll)> kills) =>
        NetChunks.Split(kills, MaxKilledPerDatagram, chunk => WriteZombieKilled(bound, chunk));

    public static byte[] WriteZombieKilled(byte bound, IReadOnlyList<(ushort Id, Vector3 Ragdoll)> kills)
    {
        ArgumentNullException.ThrowIfNull(kills);
        // The count header is one byte, and a silent truncation here would be the worst possible
        // failure: a payload advertising zero ids that the reader believes, leaving every one of those
        // corpses standing forever. The MTU bound is the tighter of the two and fires first — fourteen
        // bytes a corpse means a region-clearing blast would otherwise have been three IP fragments on
        // a message whose whole job is to be delivered.
        if (kills.Count > MaxKilledPerDatagram)
            throw new ArgumentOutOfRangeException(nameof(kills),
                $"a ZombieKilled datagram carries at most {MaxKilledPerDatagram} ids, not {kills.Count}; "
                + "use WriteZombieKilledChunks, which splits.");
        var payload = new byte[RegionHeaderBytes + (kills.Count * KilledBytes)];
        payload[0] = (byte)ENetMessage.ZombieKilled;
        payload[1] = bound;
        payload[2] = (byte)kills.Count;
        for (int i = 0; i < kills.Count; i++)
        {
            Span<byte> entry = payload.AsSpan(RegionHeaderBytes + (i * KilledBytes));
            BinaryPrimitives.WriteUInt16LittleEndian(entry, kills[i].Id);
            BinaryPrimitives.WriteSingleLittleEndian(entry[2..], kills[i].Ragdoll.X);
            BinaryPrimitives.WriteSingleLittleEndian(entry[6..], kills[i].Ragdoll.Y);
            BinaryPrimitives.WriteSingleLittleEndian(entry[10..], kills[i].Ragdoll.Z);
        }

        return payload;
    }

    public static (byte Bound, List<ushort> Ids, List<Vector3> Ragdolls) ReadZombieKilled(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        byte bound = r.ReadByte();
        int count = r.ReadByte();
        var ids = new List<ushort>(count);
        var ragdolls = new List<Vector3>(count);
        for (int i = 0; i < count; i++)
        {
            ids.Add(r.ReadUInt16());
            ragdolls.Add(new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()));
        }

        return (bound, ids, ragdolls);
    }

    // ZombieManager.sendZombieStun: which zombies staggered, and which reel each plays.
    //
    // Reliable, like a kill, and for the same reason: a dropped stun leaves a body standing still on the
    // server while the client keeps walking it, and nothing later says otherwise. Unlike a kill, though,
    // a stun is not final — the next state snapshot will move the zombie again — so this is bounded by
    // the same one-byte count and addressed to the same region.
    public static List<byte[]> WriteZombieStunnedChunks(byte bound,
        IReadOnlyList<(ushort Id, byte Clip)> stuns) =>
        NetChunks.Split(stuns, MaxStunnedPerDatagram, chunk => WriteZombieStunned(bound, chunk));

    // Three bytes a zombie, so a full 255-strong region fits one datagram with room to spare and this
    // bound never fires in practice. Written through the same rule anyway: what makes the invariant
    // testable — "no writer can emit past the budget, for any legal input" — is that every writer is
    // subject to it, not that most of them happen not to need it.
    public static byte[] WriteZombieStunned(byte bound, IReadOnlyList<(ushort Id, byte Clip)> stuns)
    {
        ArgumentNullException.ThrowIfNull(stuns);
        if (stuns.Count > MaxStunnedPerDatagram)
            throw new ArgumentOutOfRangeException(nameof(stuns),
                $"a ZombieStunned datagram carries at most {MaxStunnedPerDatagram} zombies, not "
                + $"{stuns.Count}; use WriteZombieStunnedChunks, which splits.");

        var payload = new byte[RegionHeaderBytes + (stuns.Count * StunnedBytes)];
        payload[0] = (byte)ENetMessage.ZombieStunned;
        payload[1] = bound;
        payload[2] = (byte)stuns.Count;
        for (int i = 0; i < stuns.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                payload.AsSpan(RegionHeaderBytes + (i * StunnedBytes)), stuns[i].Id);
            payload[RegionHeaderBytes + 2 + (i * StunnedBytes)] = stuns[i].Clip;
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
