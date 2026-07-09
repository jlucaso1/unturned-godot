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
    private int _nextConnectionId = 1;

    // Creates the client end of a new connection; the server sees Connected on its next drain.
    public LoopbackClientTransport CreateClient()
    {
        var connection = new LoopbackConnection(_nextConnectionId++, this);
        var client = new LoopbackClientTransport(connection);
        connection.Client = client;
        _events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, connection, Array.Empty<byte>()));
        return client;
    }

    internal void Enqueue(ServerTransportEvent evt) => _events.Enqueue(evt);

    public bool TryReceive(out ServerTransportEvent evt) => _events.TryDequeue(out evt);

    public void Update(double now) { } // nothing time-based: delivery is immediate

    public void Close() => _events.Clear();
}

public sealed class LoopbackConnection : ITransportConnection
{
    private readonly LoopbackServerTransport _server;
    internal LoopbackClientTransport Client = null!; // set by CreateClient before any event flows

    public int Id { get; }

    internal LoopbackConnection(int id, LoopbackServerTransport server)
    {
        Id = id;
        _server = server;
    }

    // Server -> client.
    public void Send(byte[] payload, ESendType sendType) => Client.EnqueueFromServer(payload);

    // Client -> server.
    internal void SendToServer(byte[] payload) =>
        _server.Enqueue(new ServerTransportEvent(ETransportEvent.Message, this, payload));

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

    internal void EnqueueFromServer(byte[] payload) => _incoming.Enqueue(payload);

    internal void CloseFromServer() => IsConnected = false;

    public void Send(byte[] payload, ESendType sendType)
    {
        if (IsConnected)
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

    public void Update(double now) { }

    public void Close()
    {
        if (!IsConnected)
            return;
        IsConnected = false;
        _connection.NotifyDisconnected();
    }
}
