using System;
using System.Collections.Generic;

namespace UnturnedGodot.Net;

// In-memory transport, the counterpart of Unturned's NetTransport_Loopback: singleplayer and the host
// player of a listen server talk to their own server through paired queues, and the end-to-end tests
// connect N clients deterministically with no sockets or threads. Delivery is perfect and ordered;
// reliability semantics are exercised by the reliable channel's own tests instead.
public sealed class LoopbackServerTransport : IServerTransport
{
    private readonly Queue<ServerTransportEvent> _events = new();
    private readonly List<LoopbackConnection> _connections = new();
    private int _nextConnectionId = 1;

    public NetTraffic Traffic { get; } = new();

    // Creates the client end of a new connection; the server sees Connected on its next drain.
    public LoopbackClientTransport CreateClient()
    {
        var connection = new LoopbackConnection(_nextConnectionId++, this, new NetTraffic(Traffic));
        var client = new LoopbackClientTransport(connection);
        connection.Client = client;
        _connections.Add(connection);
        _events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, connection, Array.Empty<byte>()));
        return client;
    }

    internal void Enqueue(ServerTransportEvent evt) => _events.Enqueue(evt);

    public bool TryReceive(out ServerTransportEvent evt) => _events.TryDequeue(out evt);

    // Delivery is still immediate — nothing here retransmits — but the rate windows are time-based, and
    // a loopback session is exactly where the byte counts have to keep working: singleplayer runs this
    // transport, so an instrument that went blank on it would be blind on the shape most people play.
    public void Update(double now)
    {
        foreach (LoopbackConnection connection in _connections)
            connection.Traffic.Update(now);
        Traffic.Update(now);
    }

    public void Close()
    {
        _events.Clear();
        _connections.Clear();
    }
}

public sealed class LoopbackConnection : ITransportConnection
{
    private readonly LoopbackServerTransport _server;
    internal LoopbackClientTransport Client = null!; // set by CreateClient before any event flows

    public int Id { get; }

    // Loopback frames nothing, so the payload IS the datagram and wire bytes are its length. That makes
    // a solo session's reading slightly lower than the same traffic over UDP, which is the truth: the
    // three bytes of reliable framing are a cost of the socket, not of the protocol.
    public NetTraffic Traffic { get; }

    internal LoopbackConnection(int id, LoopbackServerTransport server, NetTraffic traffic)
    {
        Id = id;
        _server = server;
        Traffic = traffic;
    }

    // Server -> client.
    public void Send(byte[] payload, ESendType sendType)
    {
        Traffic.RecordSent(payload, payload.Length);
        Client.EnqueueFromServer(payload);
    }

    // Client -> server.
    internal void SendToServer(byte[] payload)
    {
        Traffic.RecordReceived(payload, payload.Length);
        _server.Enqueue(new ServerTransportEvent(ETransportEvent.Message, this, payload));
    }

    internal void NotifyDisconnected() =>
        _server.Enqueue(new ServerTransportEvent(ETransportEvent.Disconnected, this, Array.Empty<byte>()));

    // Server-initiated kick.
    public void Close()
    {
        Client.CloseFromServer();
        NotifyDisconnected();
    }
}

public sealed class LoopbackClientTransport : IClientTransport
{
    private readonly LoopbackConnection _connection;
    private readonly Queue<byte[]> _incoming = new();

    internal LoopbackClientTransport(LoopbackConnection connection) => _connection = connection;

    public bool IsConnected { get; private set; } = true;

    // The CLIENT's own view of the same link, kept separate from the connection's rather than shared:
    // "sent" means the opposite thing at each end, and a host that plays its own listen server would
    // otherwise read one object whose directions cancel out.
    public NetTraffic Traffic { get; } = new();

    internal void EnqueueFromServer(byte[] payload)
    {
        Traffic.RecordReceived(payload, payload.Length);
        _incoming.Enqueue(payload);
    }

    internal void CloseFromServer() => IsConnected = false;

    public void Send(byte[] payload, ESendType sendType)
    {
        if (!IsConnected)
            return;
        Traffic.RecordSent(payload, payload.Length);
        _connection.SendToServer(payload);
    }

    public bool TryReceive(out byte[] payload)
    {
        if (_incoming.TryDequeue(out byte[]? dequeued))
        {
            payload = dequeued;
            return true;
        }
        payload = Array.Empty<byte>();
        return false;
    }

    public void Update(double now) => Traffic.Update(now);

    public void Close()
    {
        if (!IsConnected)
            return;
        IsConnected = false;
        _connection.NotifyDisconnected();
    }
}
