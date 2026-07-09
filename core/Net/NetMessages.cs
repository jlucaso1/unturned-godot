using System;
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
    Hello,        // client -> server, reliable: I want to join (name + protocol version)
    Welcome,      // server -> client, reliable: your id + everyone already here
    PlayerJoined, // server -> all, reliable
    PlayerLeft,   // server -> all, reliable
    Input,        // client -> server, unreliable: one 12.5 Hz input frame (+ trusted position + stance)
    StateUpdate,  // server -> all, unreliable: every player's position, view angles and stance
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

    // Client-simulated position (Unturned's forceTrustClient shape): the real client resolves collision
    // against the full world (buildings, trees) that the server's heightfield solver can't; the server
    // validates the delta against a speed budget and adopts it. Absent (bots), the server simulates.
    public readonly bool HasPosition;
    public readonly Vector3 Position;

    public InputCommand(uint frame, sbyte inputX, sbyte inputY, bool jump, bool sprint, byte yaw, byte pitch,
        EPlayerStance stance = EPlayerStance.Stand)
    {
        Frame = frame;
        InputX = inputX;
        InputY = inputY;
        Jump = jump;
        Sprint = sprint;
        Yaw = yaw;
        Pitch = pitch;
        Stance = stance;
        HasPosition = false;
        Position = Vector3.Zero;
    }

    public InputCommand(uint frame, sbyte inputX, sbyte inputY, bool jump, bool sprint, byte yaw, byte pitch,
        EPlayerStance stance, Vector3 position)
        : this(frame, inputX, inputY, jump, sprint, yaw, pitch, stance)
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

    public PlayerSnapshotState(byte playerId, Vector3 position, byte pitch, byte yaw,
        EPlayerStance stance = EPlayerStance.Stand)
    {
        PlayerId = playerId;
        Position = position;
        Pitch = pitch;
        Yaw = yaw;
        Stance = stance;
    }
}

public sealed class PlayerListing
{
    public byte PlayerId;
    public string Name = string.Empty;
    public Vector3 Position;
    public byte Pitch;
    public byte Yaw;
    public EPlayerStance Stance;
}

// Encoders/decoders for each message. Little-endian BinaryWriter framing; the first byte is ENetMessage.
public static class NetMessages
{
    // Bump whenever a message layout changes; the server refuses mismatched clients at the handshake.
    public const byte ProtocolVersion = 2;

    public static ENetMessage TypeOf(byte[] payload) => (ENetMessage)payload[0];

    public static byte[] WriteHello(string name)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Hello);
        w.Write(ProtocolVersion);
        w.Write(name);
        return ms.ToArray();
    }

    public static (byte Version, string Name) ReadHello(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return (r.ReadByte(), r.ReadString());
    }

    public static byte[] WriteWelcome(byte playerId, uint tick, IReadOnlyList<PlayerListing> players)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Welcome);
        w.Write(playerId);
        w.Write(tick);
        w.Write((byte)players.Count);
        foreach (PlayerListing p in players)
            WriteListing(w, p);
        return ms.ToArray();
    }

    public static (byte PlayerId, uint Tick, List<PlayerListing> Players) ReadWelcome(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        byte id = r.ReadByte();
        uint tick = r.ReadUInt32();
        int count = r.ReadByte();
        var players = new List<PlayerListing>(count);
        for (int i = 0; i < count; i++)
            players.Add(ReadListing(r));
        return (id, tick, players);
    }

    public static byte[] WritePlayerJoined(PlayerListing player)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.PlayerJoined);
        WriteListing(w, player);
        return ms.ToArray();
    }

    public static PlayerListing ReadPlayerJoined(byte[] payload)
    {
        using BinaryReader r = Reader(payload);
        return ReadListing(r);
    }

    public static byte[] WritePlayerLeft(byte playerId)
    {
        return new[] { (byte)ENetMessage.PlayerLeft, playerId };
    }

    public static byte ReadPlayerLeft(byte[] payload) => payload[1];

    public static byte[] WriteInput(in InputCommand input)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.Input);
        w.Write(input.Frame);
        w.Write(input.InputX);
        w.Write(input.InputY);
        w.Write((byte)((input.Jump ? 1 : 0) | (input.Sprint ? 2 : 0) | (input.HasPosition ? 4 : 0)));
        w.Write(input.Yaw);
        w.Write(input.Pitch);
        w.Write((byte)input.Stance);
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
        if ((flags & 4) == 0)
            return new InputCommand(frame, x, y, jump, sprint, yaw, pitch, stance);
        var position = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new InputCommand(frame, x, y, jump, sprint, yaw, pitch, stance, position);
    }

    public static byte[] WriteStateUpdate(uint tick, IReadOnlyList<PlayerSnapshotState> states)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)ENetMessage.StateUpdate);
        w.Write(tick);
        w.Write((byte)states.Count);
        foreach (PlayerSnapshotState s in states)
        {
            w.Write(s.PlayerId);
            w.Write(s.Position.X);
            w.Write(s.Position.Y);
            w.Write(s.Position.Z);
            w.Write(s.Pitch);
            w.Write(s.Yaw);
            w.Write((byte)s.Stance);
        }
        return ms.ToArray();
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
            var stance = (EPlayerStance)r.ReadByte();
            states.Add(new PlayerSnapshotState(id, pos, pitch, yaw, stance));
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

    // Positions the reader just past the message-type byte.
    private static BinaryReader Reader(byte[] payload)
    {
        var r = new BinaryReader(new MemoryStream(payload));
        r.ReadByte();
        return r;
    }
}
