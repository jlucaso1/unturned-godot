using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Godot;
using UnturnedGodot.Player;

namespace UnturnedGodot.Net;

// The wire protocol for movement multiplayer. Cadence and quantization mirror Unturned: clients send
// inputs at 12.5 Hz (PlayerInput.RATE), the server rebroadcasts every player's state at the same rate
// (Provider.UPDATE_TIME), and angles travel as single bytes — pitch in whole degrees, yaw halved
// (PlayerStateUpdate's byte angle / byte rot, un-halved by tellState's "newYaw * 2").
// Extending the protocol = adding a message type here plus its writer/reader below: reliable for
// discrete events (doors, resource fell, chat), unreliable for continuous streams (movement, aim).
// ProtocolVersion gates the handshake, so incompatible builds are refused cleanly instead of
// mis-parsing each other's frames.
public enum ENetMessage : byte
{
    Hello,        // client -> server, reliable: I want to join (name + protocol version + level)
    Welcome,      // server -> client, reliable: your id + everyone already here
    PlayerJoined, // server -> all, reliable
    PlayerLeft,   // server -> all, reliable
    Input,        // client -> server, unreliable: one 12.5 Hz input frame (+ trusted position + stance)
    StateUpdate,  // server -> all, unreliable: every player's position, view angles and stance
    ZombieList,   // server -> client, reliable: a chunk of the zombie population (sent on admission)
    ZombieStates, // server -> all, unreliable: the zombies that moved or changed state this tick

    // Pre-join, before the client has built anything: "what are you running?". This is what lets a
    // client load the server's level instead of guessing — see ServerQuery.
    ServerInfoRequest, // client -> server, reliable
    ServerInfo,        // server -> client, reliable: protocol version, level, population
    Reject,            // server -> client, reliable: why this Hello will never be admitted

    // NEW MESSAGE TYPES GO HERE, at the end. The three above run BEFORE the protocol handshake gates
    // anything — ServerQuery asks an unknown server what it is running — so their byte values are the
    // one part of this enum two different builds must still agree on. Inserting a type above them
    // renumbers them, and a version query between mismatched builds then decodes as some other message
    // and times out instead of reporting the mismatch it exists to report.
    PlayerGesture, // server -> all but the owner, reliable: a one-shot hand animation (a punch today)
    ZombieKilled,  // server -> a region's clients, reliable: zombies that died this tick
    Impact,        // server -> all, unreliable: a hit's point, normal and surface (its sound and mark)
    ZombieStunned, // server -> a region's clients, reliable: zombies staggered this tick, and which reel
}

// Why a Hello was refused. Sent before the connection is closed so the player is told what happened
// instead of watching a join silently fail (or, worse, succeed onto the wrong world).
public enum EJoinRejection : byte
{
    ProtocolMismatch, // incompatible build
    LevelMismatch,    // the client built another map than the one this server runs
    ServerFull,       // no player id left to hand out
}

public readonly struct JoinRejection
{
    public readonly EJoinRejection Reason;
    public readonly byte ServerProtocolVersion;
    public readonly string ServerLevel;

    public JoinRejection(EJoinRejection reason, byte serverProtocolVersion, string serverLevel)
    {
        Reason = reason;
        ServerProtocolVersion = serverProtocolVersion;
        ServerLevel = serverLevel;
    }
}

// What a server answers a pre-join query with: enough to build the right world and to show the player
// what they are about to join.
public sealed class ServerInfo
{
    public required byte ProtocolVersion { get; init; }
    public required string Level { get; init; }
    public byte PlayerCount { get; init; }
    public byte FreeSlots { get; init; }
}

public static class NetAngles
{
    // PlayerStateUpdate.rot: yaw stored as degrees / 2 so 0..360 fits a byte (1.4° steps).
    public static byte QuantizeYaw(float degrees) =>
        (byte)(Mathf.PosMod(degrees, 360f) / 2f + 0.5f);

    public static float DequantizeYaw(byte value) => value * 2f;

    // PlayerStateUpdate.angle: pitch in whole degrees (Unturned's look pitch spans 0..180).
    public static byte QuantizePitch(float degrees) =>
        (byte)(Mathf.Clamp(degrees, 0f, 180f) + 0.5f);

    public static float DequantizePitch(byte value) => value;
}

public readonly struct InputCommand
{
    public readonly uint Frame;
    public readonly sbyte InputX;  // -1, 0, 1 (strafe)
    public readonly sbyte InputY;  // -1, 0, 1 (forward/back; -1 = forward, as in our controller input)
    public readonly bool Jump;
    public readonly bool Sprint;
    public readonly byte Yaw;      // quantized view yaw the movement is relative to
    public readonly byte Pitch;
    public readonly EPlayerStance Stance;

    // The client's real IsOnFloor: replicated (not inferred) so every client derives landings from the
    // same state the owner saw — interpolation stalls can't fake touchdowns.
    public readonly bool Grounded;

    // Client-simulated position (Unturned's forceTrustClient shape): the real client resolves collision
    // against the full world (buildings, trees) that the server's heightfield solver can't; the server
    // validates the delta against a speed budget and adopts it. Absent (bots), the server simulates.
    public readonly bool HasPosition;
    public readonly Vector3 Position;

    // The swing this frame announces, if any: which fist, and the number the OWNER gave it when its own
    // hands accepted it.
    //
    // An IDENTITY rather than a button edge, and that distinction is the whole design. Input is
    // unreliable, so a frame carrying a swing is repeated (PlayerController.AttackEdgeRepeats) — and a
    // repeated EDGE is indistinguishable from a second press. The server then judges it again, against
    // whatever stance and whatever moment the repeat happened to land in: punch standing, go prone, lose
    // the first datagram, and the repeat is refused for a stance the swing was never thrown in. Every
    // copy of one swing carries one number instead, so the server recognises the repeats as the swing it
    // has already answered and answers nothing twice.
    public readonly bool HasSwing;

    // Wraps, and is compared as a wrap-safe seven-bit ORDER against the last swing played: the same
    // number is the repeat of a swing already answered, and a lower one is a swing that reached the
    // server behind a newer swing and must not be answered a second time. Seven bits is far more than
    // the two frames a repeat spans.
    public readonly byte SwingSequence;
    public readonly EPlayerPunch SwingFist;

    // The aim the swing was thrown WITH, which is not necessarily this frame's.
    //
    // Input is unreliable, so the frame carrying a swing is repeated for a couple of ticks. If the first
    // copy is lost, the copy that reaches the server first belongs to a LATER frame with a later yaw,
    // pitch and stance — and judging the punch by that frame's aim would raycast from where the player is
    // looking now rather than where they were looking when they swung. The swing therefore carries its
    // own aim, pinned when the owner's hands accepted it, while the frame's own yaw/pitch/stance stay
    // live because movement still needs them. On the frame that threw the swing the two are equal.
    public readonly byte SwingYaw;
    public readonly byte SwingPitch;
    public readonly EPlayerStance SwingStance;

    public InputCommand(uint frame, sbyte inputX, sbyte inputY, bool jump, bool sprint, byte yaw, byte pitch,
        EPlayerStance stance = EPlayerStance.Stand, bool grounded = true,
        bool hasSwing = false, byte swingSequence = 0, EPlayerPunch swingFist = EPlayerPunch.Left,
        byte? swingYaw = null, byte? swingPitch = null, EPlayerStance? swingStance = null)
    {
        Frame = frame;
        InputX = inputX;
        InputY = inputY;
        Jump = jump;
        Sprint = sprint;
        Yaw = yaw;
        Pitch = pitch;
        Stance = stance;
        Grounded = grounded;
        HasPosition = false;
        Position = Vector3.Zero;
        HasSwing = hasSwing;
        SwingSequence = swingSequence;
        SwingFist = swingFist;
        // A caller that does not pin one means "this frame is the swing frame", which is the truth on the
        // frame the swing was minted and the honest default everywhere else.
        SwingYaw = swingYaw ?? yaw;
        SwingPitch = swingPitch ?? pitch;
        SwingStance = swingStance ?? stance;
    }

    public InputCommand(uint frame, sbyte inputX, sbyte inputY, bool jump, bool sprint, byte yaw, byte pitch,
        EPlayerStance stance, Vector3 position, bool grounded = true,
        bool hasSwing = false, byte swingSequence = 0, EPlayerPunch swingFist = EPlayerPunch.Left,
        byte? swingYaw = null, byte? swingPitch = null, EPlayerStance? swingStance = null)
        : this(frame, inputX, inputY, jump, sprint, yaw, pitch, stance, grounded, hasSwing, swingSequence,
            swingFist, swingYaw, swingPitch, swingStance)
    {
        HasPosition = true;
        Position = position;
    }
}

public readonly struct PlayerSnapshotState
{
    public readonly byte PlayerId;
    public readonly Vector3 Position;
    public readonly byte Pitch;
    public readonly byte Yaw;
    public readonly EPlayerStance Stance;

    // Input-derived, replicated so remote animation matches what the owner's controller does: a player
    // jumping in place keeps Moving=false and holds Idle — inferring it from position deltas made the
    // vertical motion flicker the walk state on and off, restarting the crossfade mid-air.
    public readonly bool Moving;

    // The owner's real grounded state, for remote landing sounds (see InputCommand.Grounded).
    public readonly bool Grounded;

    public PlayerSnapshotState(byte playerId, Vector3 position, byte pitch, byte yaw,
        EPlayerStance stance = EPlayerStance.Stand, bool moving = false, bool grounded = true)
    {
        PlayerId = playerId;
        Position = position;
        Pitch = pitch;
        Yaw = yaw;
        Stance = stance;
        Moving = moving;
        Grounded = grounded;
    }
}

public sealed class PlayerListing
{
    public byte PlayerId;
    public string Name = string.Empty;
    public Vector3 Position;
    public byte Pitch;
    public byte Yaw;

    // Spelled out because default(EPlayerStance) is 0, which is Climb in the game's own numbering: a
    // listing nobody filled in must read as a standing player, not as one clinging to a ladder.
    public EPlayerStance Stance = EPlayerStance.Stand;
}

// Encoders/decoders for each message. Little-endian BinaryWriter framing; the first byte is ENetMessage.
public static class NetMessages
{
    // Bump whenever a message layout changes; the server refuses mismatched clients at the handshake.
    // Version 9 does not move a byte: it adds the value 0 (Climb) to the stance a build can send, which
    // a version 8 peer would read as a stance it has no state for. Refusing the mix is the cheap answer.
    // Version 10 adds ZombieKilled, which a version 9 client would drop as an unknown type — leaving
    // the corpse of every punched zombie standing on its screen forever.
    // Version 11 widens the Input swing block by the three bytes of the swing's own aim, so a version 10
    // client's swing frames would be read three bytes short and every field after them would be garbage.
    // Version 12 adds Impact, which a version 11 client would drop as an unknown type — every punch on
    // the world would be silent and unmarked on its screen, including its own.
    // Version 13 adds ZombieStunned the same way: a version 12 client would keep animating a zombie the
    // server has frozen, so the body would slide through its stagger instead of playing it.
    public const byte ProtocolVersion = 13;

    // Two level names denote the same world. The name on the wire is the map's FOLDER name — the one
    // identity that survives the trip between two machines (paths and workshop ids do not) — and it
    // reaches us from menus and command lines, so case and stray spaces are not a different map.
    // Empty never matches anything, including another empty: a client that cannot name its world is
    // exactly the client this check exists for.
    public static bool LevelsMatch(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    // The transport drops empty payloads, so this is the second line of defence rather than the first —
    // but it is the one every reader funnels through, and reading [0] off an empty array is the
    // cheapest way to lose a server to a one-byte datagram.
    // The longest player name, in UTF-8 bytes, that the roster may carry.
    //
    // Welcome names every joined player, so one oversized name inflates the Welcome sent to everyone who
    // joins afterwards. With the transport now refusing datagrams past MaxPayloadBytes, an unbounded name
    // does not merely bloat the roster — it makes a full server's Welcome undeliverable, and the clients
    // it was meant for are admitted and then time out without ever joining.
    //
    // Bounded in bytes rather than characters because that is what the datagram is measured in: 32
    // characters of CJK is 96 bytes and of astral-plane emoji is 128. At 32 bytes, a full 254-player
    // roster is about 12.5 KB, comfortably inside the transport's 16 KiB ceiling.
    public const int MaxNameBytes = 32;

    // Truncates on a character boundary, so a clamped name is never cut through a multi-byte sequence.
    public static string ClampName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;
        if (System.Text.Encoding.UTF8.GetByteCount(name) <= MaxNameBytes)
            return name;

        int bytes = 0;
        var element = System.Globalization.StringInfo.GetTextElementEnumerator(name);
        var kept = new System.Text.StringBuilder();
        while (element.MoveNext())
        {
            var text = (string)element.Current;
            int size = System.Text.Encoding.UTF8.GetByteCount(text);
            if (bytes + size > MaxNameBytes)
                break;
            bytes += size;
            kept.Append(text);
        }
        return kept.ToString();
    }

    public static ENetMessage TypeOf(byte[] payload) =>
        payload.Length == 0
            ? throw new InvalidDataException("Empty net payload carries no message type.")
            : (ENetMessage)payload[0];

    // The level travels with the join request, not as an afterthought: the server admits a player onto
    // its world only if that is the world the client actually built.
    // Clamped HERE rather than only on admission. The server already shortens the name it stores, but a
    // Hello carrying an unclamped one has to survive the trip first: past MaxPayloadBytes the transport
    // drops the datagram before any of that runs, so a player with a very long PLAYER_NAME retried a
    // Hello that could never be read and timed out instead of joining under a 32-byte name. Clamping at
    // the point the bytes are produced makes the two ends agree by construction.
    public static byte[] WriteHello(string name, string level)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Hello);
        w.Write(ProtocolVersion);
        w.Write(ClampName(name));
        w.Write(level);
        return ms.ToArray();
    }

    public static (byte Version, string Name, string Level) ReadHello(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return (r.ReadByte(), r.ReadString(), r.ReadString());
    }

    // The version alone, read WITHOUT touching the rest of the message. Everything after it is
    // versioned — the level field only exists from 6 on — so decoding the whole Hello first turns an
    // older client's shorter one into a malformed packet, and it never hears the refusal it is owed.
    // The version byte is the one field the format promises never to move.
    public static byte ReadHelloVersion(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return r.ReadByte();
    }

    public static byte[] WriteServerInfoRequest() => new[] { (byte)ENetMessage.ServerInfoRequest };

    public static byte[] WriteServerInfo(string level, int playerCount, int freeSlots)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.ServerInfo);
        w.Write(ProtocolVersion);
        w.Write(level);
        w.Write((byte)Math.Clamp(playerCount, 0, byte.MaxValue));
        w.Write((byte)Math.Clamp(freeSlots, 0, byte.MaxValue));
        return ms.ToArray();
    }

    public static ServerInfo ReadServerInfo(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return new ServerInfo
        {
            ProtocolVersion = r.ReadByte(),
            Level = r.ReadString(),
            PlayerCount = r.ReadByte(),
            FreeSlots = r.ReadByte(),
        };
    }

    public static byte[] WriteReject(EJoinRejection reason, string serverLevel)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Reject);
        w.Write((byte)reason);
        w.Write(ProtocolVersion);
        w.Write(serverLevel);
        return ms.ToArray();
    }

    public static JoinRejection ReadReject(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return new JoinRejection((EJoinRejection)r.ReadByte(), r.ReadByte(), r.ReadString());
    }

    // ROSTER VERSION: a counter the server bumps on every membership event — one admission, one
    // departure, one step of the counter — carried by Welcome, PlayerJoined and PlayerLeft alike.
    //
    // It exists because these three race. Reliable delivery retransmits, it does not order, so a client
    // can see a roster after the join or the leave that supersedes it, and then has to decide which
    // describes the newer world. The simulation tick cannot answer that: several membership events
    // happen inside ONE server frame (a joined client re-Hellos while a new player is admitted), so
    // they share a tick and the tie goes to whichever arrives last. A per-event counter has no ties.
    public static byte[] WriteWelcome(byte playerId, uint tick, uint rosterVersion,
        IReadOnlyList<PlayerListing> players)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Welcome);
        w.Write(playerId);
        w.Write(tick);
        w.Write(rosterVersion);
        w.Write((byte)players.Count);
        foreach (PlayerListing p in players)
            WriteListing(w, p);
        return ms.ToArray();
    }

    public static (byte PlayerId, uint Tick, uint RosterVersion, List<PlayerListing> Players) ReadWelcome(
        byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        byte id = r.ReadByte();
        uint tick = r.ReadUInt32();
        uint rosterVersion = r.ReadUInt32();
        int count = r.ReadByte();
        var players = new List<PlayerListing>(count);
        for (int i = 0; i < count; i++)
            players.Add(ReadListing(r));
        return (id, tick, rosterVersion, players);
    }

    // `tick` is the simulation tick this player was admitted on. It travels with the roster entry because
    // it is what dates the avatar: a gesture names only a player id, ids are recycled, and nothing that
    // happened before this player arrived belongs to them. Carrying it here rather than having the client
    // guess from the newest tick it happens to have heard also keeps it in the SERVER's own tick space,
    // so a restarted host counting from zero again needs no special case.
    public static byte[] WritePlayerJoined(uint rosterVersion, uint tick, PlayerListing player)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.PlayerJoined);
        w.Write(rosterVersion);
        w.Write(tick);
        WriteListing(w, player);
        return ms.ToArray();
    }

    public static (uint RosterVersion, uint Tick, PlayerListing Player) ReadPlayerJoined(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return (r.ReadUInt32(), r.ReadUInt32(), ReadListing(r));
    }

    // The leave is versioned too, so a roster older than it cannot put the player back on the map.
    public static byte[] WritePlayerLeft(uint rosterVersion, byte playerId)
    {
        var payload = new byte[6];
        payload[0] = (byte)ENetMessage.PlayerLeft;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), rosterVersion);
        payload[5] = playerId;
        return payload;
    }

    public static (uint RosterVersion, byte PlayerId) ReadPlayerLeft(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return (r.ReadUInt32(), r.ReadByte());
    }

    public static byte[] WriteInput(in InputCommand input)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Input);
        w.Write(input.Frame);
        w.Write(input.InputX);
        w.Write(input.InputY);
        // A swing is announced by a flag bit and paid for by one extra byte, the same shape the trusted
        // position already uses: the frames that carry one are a handful per session, and every other
        // input frame stays exactly the width it was.
        w.Write((byte)((input.Jump ? 1 : 0) | (input.Sprint ? 2 : 0) | (input.HasPosition ? 4 : 0)
            | (input.Grounded ? 8 : 0) | (input.HasSwing ? 16 : 0)));
        w.Write(input.Yaw);
        w.Write(input.Pitch);
        w.Write((byte)input.Stance);
        if (input.HasSwing)
        {
            w.Write((byte)(((input.SwingSequence & 0x7F) << 1) | (input.SwingFist == EPlayerPunch.Right ? 1 : 0)));
            // The swing's own aim, so a repeat that arrives first is still judged by the frame that
            // threw it. Three bytes on the handful of frames a session's punches occupy.
            w.Write(input.SwingYaw);
            w.Write(input.SwingPitch);
            w.Write((byte)input.SwingStance);
        }
        if (input.HasPosition)
        {
            w.Write(input.Position.X);
            w.Write(input.Position.Y);
            w.Write(input.Position.Z);
        }
        return ms.ToArray();
    }

    public static InputCommand ReadInput(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        uint frame = r.ReadUInt32();
        sbyte x = r.ReadSByte();
        sbyte y = r.ReadSByte();
        byte flags = r.ReadByte();
        byte yaw = r.ReadByte();
        byte pitch = r.ReadByte();
        var stance = (EPlayerStance)r.ReadByte();
        bool jump = (flags & 1) != 0;
        bool sprint = (flags & 2) != 0;
        bool grounded = (flags & 8) != 0;
        bool hasSwing = (flags & 16) != 0;
        byte swingSequence = 0;
        EPlayerPunch swingFist = EPlayerPunch.Left;
        byte? swingYaw = null;
        byte? swingPitch = null;
        EPlayerStance? swingStance = null;
        if (hasSwing)
        {
            byte swing = r.ReadByte();
            swingSequence = (byte)(swing >> 1);
            swingFist = (swing & 1) != 0 ? EPlayerPunch.Right : EPlayerPunch.Left;
            swingYaw = r.ReadByte();
            swingPitch = r.ReadByte();
            swingStance = (EPlayerStance)r.ReadByte();
        }
        if ((flags & 4) == 0)
            return new InputCommand(frame, x, y, jump, sprint, yaw, pitch, stance, grounded, hasSwing,
                swingSequence, swingFist, swingYaw, swingPitch, swingStance);
        var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new InputCommand(frame, x, y, jump, sprint, yaw, pitch, stance, position, grounded, hasSwing,
            swingSequence, swingFist, swingYaw, swingPitch, swingStance);
    }

    // A one-shot hand animation somebody else performed. Reliable and event-shaped rather than a bit in
    // the state stream: a punch is a moment, and the 12.5 Hz snapshot that carried it as a flag would
    // either be missed by a client that dropped that datagram or be replayed by one that got it twice.
    //
    // The server tick rides along because reliable delivery here retransmits but does not ORDER: a lost
    // datagram can be re-sent late enough to arrive behind a gesture that came after it, and without a
    // sequence the older swing would replace the newer one on the avatar. Same reasoning as the roster
    // version above, and the same shape.
    public static byte[] WritePlayerGesture(byte playerId, uint tick, EPlayerGesture gesture)
    {
        var payload = new byte[7];
        payload[0] = (byte)ENetMessage.PlayerGesture;
        payload[1] = playerId;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(2), tick);
        payload[6] = (byte)gesture;
        return payload;
    }

    public static (byte PlayerId, uint Tick, EPlayerGesture Gesture) ReadPlayerGesture(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return (r.ReadByte(), r.ReadUInt32(), (EPlayerGesture)r.ReadByte());
    }

    private const int SnapshotBytes = 16; // id 1 + position 12 + pitch 1 + yaw 1 + stance/flags 1

    // Rebuilt every server tick for every client, so it writes straight into an exact-sized array
    // (identical little-endian bytes) instead of growing a MemoryStream through a BinaryWriter —
    // and iterates by index, since foreach over the IReadOnlyList interface boxes an enumerator.
    public static byte[] WriteStateUpdate(uint tick, IReadOnlyList<PlayerSnapshotState> states)
    {
        var payload = new byte[6 + (states.Count * SnapshotBytes)];
        payload[0] = (byte)ENetMessage.StateUpdate;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), tick);
        payload[5] = (byte)states.Count;
        int o = 6;
        for (int i = 0; i < states.Count; i++)
        {
            PlayerSnapshotState s = states[i];
            payload[o] = s.PlayerId;
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 1), s.Position.X);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 5), s.Position.Y);
            BinaryPrimitives.WriteSingleLittleEndian(payload.AsSpan(o + 9), s.Position.Z);
            payload[o + 13] = s.Pitch;
            payload[o + 14] = s.Yaw;
            payload[o + 15] = PackStanceFlags(s.Stance, s.Moving, s.Grounded);
            o += SnapshotBytes;
        }
        return payload;
    }

    public static (uint Tick, List<PlayerSnapshotState> States) ReadStateUpdate(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        uint tick = r.ReadUInt32();
        int count = r.ReadByte();
        var states = new List<PlayerSnapshotState>(count);
        for (int i = 0; i < count; i++)
        {
            byte id = r.ReadByte();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
            byte pitch = r.ReadByte();
            byte yaw = r.ReadByte();
            (EPlayerStance stance, bool moving, bool grounded) = UnpackStanceFlags(r.ReadByte());
            states.Add(new PlayerSnapshotState(id, pos, pitch, yaw, stance, moving, grounded));
        }
        return (tick, states);
    }

    private static void WriteListing(BinaryWriter w, PlayerListing p)
    {
        w.Write(p.PlayerId);
        w.Write(p.Name);
        w.Write(p.Position.X);
        w.Write(p.Position.Y);
        w.Write(p.Position.Z);
        w.Write(p.Pitch);
        w.Write(p.Yaw);
        w.Write((byte)p.Stance);
    }

    private static PlayerListing ReadListing(BinaryReader r) => new()
    {
        PlayerId = r.ReadByte(),
        Name = r.ReadString(),
        Position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
        Pitch = r.ReadByte(),
        Yaw = r.ReadByte(),
        Stance = (EPlayerStance)r.ReadByte(),
    };

    // Stance fits in the low bits; bit 6 carries the owner's Grounded and bit 7 the Moving flag.
    private static byte PackStanceFlags(EPlayerStance stance, bool moving, bool grounded) =>
        (byte)((byte)stance | (grounded ? 0x40 : 0) | (moving ? 0x80 : 0));

    private static (EPlayerStance Stance, bool Moving, bool Grounded) UnpackStanceFlags(byte value) =>
        ((EPlayerStance)(value & 0x3F), (value & 0x80) != 0, (value & 0x40) != 0);

    // Positions the reader just past the message-type byte.
    private static BinaryReader Reader(byte[] payload)
    {
        var r = new BinaryReader(new MemoryStream(payload));
        r.ReadByte();
        return r;
    }
}
