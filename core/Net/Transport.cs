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

    // What this one link has cost, split by message type. Per connection rather than only per server
    // because "the server is sending 12 MB/s" and "this player is being sent 12 MB/s" are different
    // findings with different fixes, and only the second one names the payload to look at.
    NetTraffic Traffic { get; }

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
    // Every connection's traffic, rolled up. The children report THROUGH this rather than the server
    // scanning them, so reading it costs nothing however many players are on.
    NetTraffic Traffic { get; }

    // Answers a datagram from a peer the transport has never heard from, WITHOUT allocating anything
    // for it: given the payload, return a reply to send straight back, or null to fall through to the
    // ordinary "make a connection" path.
    //
    // This exists because the connection table was exhaustible before any handshake. A Connection plus
    // its ReliableChannel was allocated on the first datagram from any endpoint, held for the 15 s
    // silence timeout, and capped at 256 — so roughly 18 datagrams a second from forged or many sources
    // held every slot permanently, with no handshake to authenticate against. The bulk of legitimate
    // pre-handshake traffic is one message, ServerInfoRequest ("which map are you running?"), and it
    // needs no connection at all: it is a question with a one-datagram answer.
    //
    // Set by the server, honoured by the transport, so what the answer IS stays in the protocol layer
    // and the socket layer only decides when to ask.
    Func<byte[], byte[]?>? AnswerConnectionless { get; set; }

    bool TryReceive(out ServerTransportEvent evt);
    void Update(double now); // drives retransmissions and timeouts; no-op for loopback
    void Close();
}

public interface IClientTransport
{
    bool IsConnected { get; }
    NetTraffic Traffic { get; }
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
    private readonly List<IServerTransport> _transports;

    // The listen server's own total, which is the sum of the loopback the host plays over and the UDP
    // listener the LAN joins through. Each child reports up into it, so "what is this server sending"
    // is one number whether the session is solo, listen or dedicated.
    public NetTraffic Traffic { get; } = new();

    // Forwarded to every child, and to any child added later: "open to LAN" attaches a UDP listener to
    // an already-running server, and that listener is precisely the one that needs it.
    public Func<byte[], byte[]?>? AnswerConnectionless
    {
        get => _answerConnectionless;
        set
        {
            _answerConnectionless = value;
            foreach (IServerTransport transport in _transports)
                transport.AnswerConnectionless = value;
        }
    }

    private Func<byte[], byte[]?>? _answerConnectionless;

    public CompositeServerTransport(params IServerTransport[] transports)
    {
        _transports = new List<IServerTransport>(transports);
        foreach (IServerTransport transport in _transports)
            transport.Traffic.Parent = Traffic;
    }

    // Attaches another listener to the LIVE server — how "open to LAN" works: the always-on
    // singleplayer loopback server simply gains a UDP transport, no restart, no second code path.
    public void Add(IServerTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        transport.Traffic.Parent = Traffic;
        transport.AnswerConnectionless = _answerConnectionless;
        _transports.Add(transport);
    }

    // Round-robin rather than always restarting at the first child. NetServer drains a bounded number of
    // events per Update, so a child that stays backlogged would otherwise consume every slot on every
    // Update and the ones after it would never be polled — on a listen server that is the host's loopback
    // starving the LAN transport's handshakes, inputs and disconnects indefinitely.
    private int _next;

    public bool TryReceive(out ServerTransportEvent evt)
    {
        for (int i = 0; i < _transports.Count; i++)
        {
            int at = (_next + i) % _transports.Count;
            if (_transports[at].TryReceive(out evt))
            {
                _next = (at + 1) % _transports.Count;
                return true;
            }
        }
        evt = default;
        return false;
    }

    public void Update(double now)
    {
        foreach (IServerTransport transport in _transports)
            transport.Update(now);
        Traffic.Update(now); // after the children, so this window closes over bytes they have just counted
    }

    public void Close()
    {
        foreach (IServerTransport transport in _transports)
            transport.Close();
    }
}
