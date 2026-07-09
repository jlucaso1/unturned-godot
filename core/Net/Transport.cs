using System;
using System.Collections.Generic;

namespace UnturnedGodot.Net;

// The pluggable transport seam, mirroring Unturned's SDG.NetTransport (IServerTransport /
// IClientTransport / ITransportConnection, with Loopback / SystemSockets / SteamNetworking
// implementations). Everything above this layer — handshake, simulation, snapshots — only sees these
// interfaces, so the same server runs over an in-memory loopback (tests, and the host player's own
// connection in a listen server), raw UDP (LAN / direct IP), and later P2P or Steam sockets.
//
// The model is polled, not evented: the owner drains TryReceive each frame and drives time-based work
// (reliable resends) through Update(now). Time is always an explicit parameter, so tests are deterministic.
public enum ESendType : byte
{
    Unreliable, // movement inputs and state snapshots: loss-tolerant by design, never retransmitted
    Reliable,   // handshake and join/leave events: retransmitted until acknowledged
}

public enum ETransportEvent : byte { Connected, Message, Disconnected }

public interface ITransportConnection
{
    // Stable within a server run; used for logs. Identity comparisons use the object reference.
    int Id { get; }
    void Send(byte[] payload, ESendType sendType);
    void Close();
}

public readonly struct ServerTransportEvent
{
    public readonly ETransportEvent Type;
    public readonly ITransportConnection Connection;
    public readonly byte[] Payload; // empty except for Message

    public ServerTransportEvent(ETransportEvent type, ITransportConnection connection, byte[] payload)
    {
        Type = type;
        Connection = connection;
        Payload = payload;
    }
}

public interface IServerTransport
{
    bool TryReceive(out ServerTransportEvent evt);
    void Update(double now); // drives retransmissions and timeouts; no-op for loopback
    void Close();
}

public interface IClientTransport
{
    bool IsConnected { get; }
    void Send(byte[] payload, ESendType sendType);
    bool TryReceive(out byte[] payload);
    void Update(double now);
    void Close();
}

// Merges several listening transports into one server — the listen-server ("open to LAN") shape: the
// host player connects over loopback while remote players come in over UDP, all indistinguishable to
// NetServer. A dedicated server is just this with a single UDP transport (or the UDP transport directly).
public sealed class CompositeServerTransport : IServerTransport
{
    private readonly IReadOnlyList<IServerTransport> _transports;

    public CompositeServerTransport(params IServerTransport[] transports) => _transports = transports;

    public bool TryReceive(out ServerTransportEvent evt)
    {
        foreach (IServerTransport transport in _transports)
            if (transport.TryReceive(out evt))
                return true;
        evt = default;
        return false;
    }

    public void Update(double now)
    {
        foreach (IServerTransport transport in _transports)
            transport.Update(now);
    }

    public void Close()
    {
        foreach (IServerTransport transport in _transports)
            transport.Close();
    }
}
