using System;
using System.Collections.Generic;
using Godot;

namespace UnturnedGodot.Net;

// A remote player as this client sees them: name plus the snapshot buffer their server states feed.
public sealed class RemotePlayer
{
    // tellState's LARGE_DISTANCE: a skip bigger than this resets interpolation instead of gliding.
    private const float LargeDistance = 16f;

    private readonly SnapshotBuffer _buffer = new();
    private Vector3 _lastUpdatePos;

    public string Name { get; }

    // Latest replicated stance and input-derived moving flag: discrete, so they snap (no interpolation).
    public UnturnedGodot.Player.EPlayerStance Stance { get; private set; }
    public bool Moving { get; private set; }
    public bool Grounded { get; private set; } = true;

    public RemotePlayer(string name, in PoseSnapshot initial, double now)
    {
        Name = name;
        _buffer.UpdateLastSnapshot(initial, now);
        _lastUpdatePos = initial.Position;
    }

    public void Push(in PoseSnapshot pose, UnturnedGodot.Player.EPlayerStance stance, bool moving,
        bool grounded, double now)
    {
        Stance = stance;
        Moving = moving;
        Grounded = grounded;
        bool largeDelta = (pose.Position - _lastUpdatePos).LengthSquared() > LargeDistance * LargeDistance;
        _lastUpdatePos = pose.Position;
        if (largeDelta)
            _buffer.UpdateLastSnapshot(pose, now);
        else
            _buffer.AddNewSnapshot(pose, now);
    }

    public PoseSnapshot Sample(double now) => _buffer.GetCurrentSnapshot(now);
}

// The client-side session over any IClientTransport: sends the Hello handshake and 12.5 Hz inputs,
// consumes Welcome/Joined/Left and the state broadcasts, and exposes interpolated remote players for
// rendering. The local player's own authoritative state is kept for future reconciliation.
public sealed class NetClient
{
    private readonly IClientTransport _transport;
    private readonly Dictionary<byte, RemotePlayer> _remotes = new();

    public byte PlayerId { get; private set; }

    // Datagrams that reached a decoder and did not survive it. See NetServer.MalformedPacketsDropped.
    public long MalformedPacketsDropped { get; private set; }

    // Extension seam: message types this client doesn't handle (future replicated systems) land here
    // instead of being dropped, so a feature module can subscribe without editing NetClient.
    public Action<byte[]>? OnUnhandledMessage;
    public bool Joined { get; private set; }
    public PlayerSnapshotState LocalServerState { get; private set; }
    public IReadOnlyDictionary<byte, RemotePlayer> Remotes => _remotes;

    // Self-healing join: while not admitted, the Hello re-sends on this cadence (covers "connected
    // before the server was up", lost handshakes and reliable channels that already gave up); once
    // joined, this long without any StateUpdate means the server dropped us silently — reset and rejoin.
    public const double HelloRetryInterval = 2.0;
    public const double StateTimeout = 10.0;

    private readonly string _name;
    private double _lastHello = double.NegativeInfinity;
    private double _lastStateAt;

    public NetClient(IClientTransport transport, string name)
    {
        _transport = transport;
        _name = name;
    }

    public void SendInput(in InputCommand input) =>
        _transport.Send(NetMessages.WriteInput(input), ESendType.Unreliable);

    public void Update(double now)
    {
        _transport.Update(now); // give the transport the clock BEFORE any reliable send

        if (!Joined && now - _lastHello >= HelloRetryInterval)
        {
            // Deferred from the constructor: a reliable frame stamped before the transport ever saw the
            // real clock would look GiveUpAfter-seconds old on the next Update and kill the channel.
            _lastHello = now;
            _transport.Send(NetMessages.WriteHello(_name), ESendType.Reliable);
        }

        while (_transport.TryReceive(out byte[] payload))
        {
            // A client trusts its server no further than a server trusts its clients: the bytes still
            // arrive over UDP from whatever address answered, and this loop is on the frame thread.
            try
            {
                Handle(payload, now);
            }
            catch (Exception e) when (MalformedPacket.IsDecodeFailure(e))
            {
                MalformedPacketsDropped++;
            }
        }

        if (Joined && now - _lastStateAt > StateTimeout)
        {
            // The server stopped talking to us (session dropped, host restarted): rejoin from scratch.
            Joined = false;
            _remotes.Clear();
            _lastHello = double.NegativeInfinity;
        }
    }

    private void Handle(byte[] payload, double now)
    {
        switch (NetMessages.TypeOf(payload))
        {
            default:
                OnUnhandledMessage?.Invoke(payload);
                break;
            case ENetMessage.Welcome:
                {
                    (byte id, _, List<PlayerListing> players) = NetMessages.ReadWelcome(payload);
                    PlayerId = id;
                    Joined = true;
                    _lastStateAt = now;
                    foreach (PlayerListing p in players)
                        _remotes[p.PlayerId] = SpawnRemote(p, now);
                    break;
                }
            case ENetMessage.PlayerJoined:
                {
                    PlayerListing p = NetMessages.ReadPlayerJoined(payload);
                    if (p.PlayerId != PlayerId)
                        _remotes[p.PlayerId] = SpawnRemote(p, now);
                    break;
                }
            case ENetMessage.PlayerLeft:
                _remotes.Remove(NetMessages.ReadPlayerLeft(payload));
                break;
            case ENetMessage.StateUpdate:
                {
                    (_, List<PlayerSnapshotState> states) = NetMessages.ReadStateUpdate(payload);
                    _lastStateAt = now;
                    foreach (PlayerSnapshotState s in states)
                    {
                        if (s.PlayerId == PlayerId)
                            LocalServerState = s;
                        else if (_remotes.TryGetValue(s.PlayerId, out RemotePlayer? remote))
                            remote.Push(Pose(s.Position, s.Pitch, s.Yaw), s.Stance, s.Moving, s.Grounded, now);
                    }
                    break;
                }
        }
    }

    private static RemotePlayer SpawnRemote(PlayerListing p, double now)
    {
        var remote = new RemotePlayer(p.Name, Pose(p.Position, p.Pitch, p.Yaw), now);
        remote.Push(Pose(p.Position, p.Pitch, p.Yaw), p.Stance, moving: false, grounded: true, now);
        return remote;
    }

    private static PoseSnapshot Pose(Vector3 position, byte pitch, byte yaw) =>
        new(position, NetAngles.DequantizePitch(pitch), NetAngles.DequantizeYaw(yaw));
}
