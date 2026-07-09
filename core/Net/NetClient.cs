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

    public RemotePlayer(string name, in PoseSnapshot initial, double now)
    {
        Name = name;
        _buffer.UpdateLastSnapshot(initial, now);
        _lastUpdatePos = initial.Position;
    }

    public void Push(in PoseSnapshot pose, double now)
    {
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
    public bool Joined { get; private set; }
    public PlayerSnapshotState LocalServerState { get; private set; }
    public IReadOnlyDictionary<byte, RemotePlayer> Remotes => _remotes;

    private readonly string _name;
    private bool _helloSent;

    public NetClient(IClientTransport transport, string name)
    {
        _transport = transport;
        _name = name;
    }

    public void SendInput(in InputCommand input) =>
        _transport.Send(NetMessages.WriteInput(input), ESendType.Unreliable);

    public void Update(double now)
    {
        _transport.Update(now); // give the transport the clock BEFORE the first reliable send
        if (!_helloSent)
        {
            // Deferred from the constructor: a reliable frame stamped before the transport ever saw the
            // real clock would look GiveUpAfter-seconds old on the next Update and kill the channel.
            _helloSent = true;
            _transport.Send(NetMessages.WriteHello(_name), ESendType.Reliable);
        }
        while (_transport.TryReceive(out byte[] payload))
            Handle(payload, now);
    }

    private void Handle(byte[] payload, double now)
    {
        switch (NetMessages.TypeOf(payload))
        {
            case ENetMessage.Welcome:
                {
                    (byte id, _, List<PlayerListing> players) = NetMessages.ReadWelcome(payload);
                    PlayerId = id;
                    Joined = true;
                    foreach (PlayerListing p in players)
                        _remotes[p.PlayerId] = new RemotePlayer(p.Name, Pose(p.Position, p.Pitch, p.Yaw), now);
                    break;
                }
            case ENetMessage.PlayerJoined:
                {
                    PlayerListing p = NetMessages.ReadPlayerJoined(payload);
                    if (p.PlayerId != PlayerId)
                        _remotes[p.PlayerId] = new RemotePlayer(p.Name, Pose(p.Position, p.Pitch, p.Yaw), now);
                    break;
                }
            case ENetMessage.PlayerLeft:
                _remotes.Remove(NetMessages.ReadPlayerLeft(payload));
                break;
            case ENetMessage.StateUpdate:
                {
                    (_, List<PlayerSnapshotState> states) = NetMessages.ReadStateUpdate(payload);
                    foreach (PlayerSnapshotState s in states)
                    {
                        if (s.PlayerId == PlayerId)
                            LocalServerState = s;
                        else if (_remotes.TryGetValue(s.PlayerId, out RemotePlayer? remote))
                            remote.Push(Pose(s.Position, s.Pitch, s.Yaw), now);
                    }
                    break;
                }
        }
    }

    private static PoseSnapshot Pose(Vector3 position, byte pitch, byte yaw) =>
        new(position, NetAngles.DequantizePitch(pitch), NetAngles.DequantizeYaw(yaw));
}
