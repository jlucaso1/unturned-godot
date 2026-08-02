using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace UnturnedGodot.Net;

// Raw-UDP transport — the counterpart of Unturned's NetTransport_SystemSockets (LAN / direct IP, no
// Steam). A thin socket adapter: framing, retransmission and dedup live in the fully tested
// ReliableChannel; connections are keyed by remote endpoint, appear on their first datagram and are
// dropped after a silence timeout. Excluded from coverage as IO glue; the UDP end-to-end tests still
// exercise it over localhost sockets.
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class UdpServerTransport : IServerTransport
{
    private const double SilenceTimeout = 15.0; // seconds without any datagram = disconnected

    private sealed class Connection : ITransportConnection
    {
        public required IPEndPoint Endpoint;
        public required ReliableChannel Channel;
        public required UdpServerTransport Owner;
        public double LastHeard;
        public double SendNow; // the transport's current clock, so Send can frame reliably

        public int Id { get; init; }

        public void Send(byte[] payload, ESendType sendType) => Channel.Send(payload, sendType, SendNow);

        public void Close() => Owner.Drop(this);
    }

    private readonly UdpClient _socket;
    private readonly Dictionary<string, Connection> _connections = new();
    // How many undelivered events either transport will hold. Well past a full server's own cadence, so
    // it bounds a flood without shaping normal traffic.
    public const int MaxQueuedEvents = 4096;

    private readonly Queue<ServerTransportEvent> _events = new();
    private int _nextId = 1;
    private double _now;

    public UdpServerTransport(ushort port)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    public bool TryReceive(out ServerTransportEvent evt)
    {
        PumpSocket();
        return _events.TryDequeue(out evt);
    }

    // Datagrams are drained into _events only while there is room. Past the cap they are left in the
    // socket's receive buffer for the OS to discard, which is the right backpressure for UDP: an
    // unauthenticated flood then costs kernel buffer rather than unbounded managed memory. NetServer's
    // per-Update event budget bounds how much of this queue is *handled*; this bounds how large it gets.
    private void PumpSocket()
    {
        while (_events.Count < MaxQueuedEvents && _socket.Available > 0)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            byte[] datagram;
            try
            {
                datagram = _socket.Receive(ref remote);
            }
            catch (SocketException)
            {
                return; // ICMP port-unreachable surfacing on Windows/loopback; nothing to read
            }

            string key = remote.ToString();
            if (!_connections.TryGetValue(key, out Connection? connection))
            {
                var endpoint = new IPEndPoint(remote.Address, remote.Port);
                connection = new Connection
                {
                    Endpoint = endpoint,
                    Owner = this,
                    Id = _nextId++,
                    Channel = null!,
                    // Born mid-drain: stamp the current clock, or the first reliable reply (Welcome) would
                    // carry time 0 and hit ReliableChannel's give-up deadline on the next Update.
                    SendNow = _now,
                    LastHeard = _now,
                };
                connection.Channel = new ReliableChannel(d => TrySendTo(d, endpoint));
                _connections[key] = connection;
                _events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, connection, Array.Empty<byte>()));
            }

            connection.LastHeard = _now;
            if (connection.Channel.HandleDatagram(datagram, out byte[] payload))
                _events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, connection, payload));
        }
    }

    public void Update(double now)
    {
        _now = now;
        List<Connection>? dead = null;
        foreach (Connection connection in _connections.Values)
        {
            connection.SendNow = now;
            connection.Channel.Update(now);
            if (now - connection.LastHeard > SilenceTimeout || connection.Channel.HasGivenUp)
                (dead ??= new List<Connection>()).Add(connection);
        }
        if (dead != null)
            foreach (Connection connection in dead)
                Drop(connection);
    }

    private void Drop(Connection connection)
    {
        if (_connections.Remove(connection.Endpoint.ToString()))
            _events.Enqueue(new ServerTransportEvent(ETransportEvent.Disconnected, connection, Array.Empty<byte>()));
    }

    private void TrySendTo(byte[] datagram, IPEndPoint endpoint)
    {
        try
        {
            _socket.Send(datagram, datagram.Length, endpoint);
        }
        catch (SocketException) { /* transient; reliable frames retry, unreliable ones are loss-tolerant */ }
    }

    public void Close() => _socket.Dispose();
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class UdpClientTransport : IClientTransport
{
    private readonly UdpClient _socket;
    private readonly ReliableChannel _channel;
    private readonly Queue<byte[]> _incoming = new();
    private double _now;

    public bool IsConnected { get; private set; } = true;

    public UdpClientTransport(string host, ushort port)
    {
        _socket = new UdpClient();
        _socket.Connect(host, port);
        _channel = new ReliableChannel(TrySend);
    }

    public void Send(byte[] payload, ESendType sendType) => _channel.Send(payload, sendType, _now);

    public bool TryReceive(out byte[] payload)
    {
        PumpSocket();
        if (_incoming.TryDequeue(out byte[]? dequeued))
        {
            payload = dequeued;
            return true;
        }
        payload = Array.Empty<byte>();
        return false;
    }

    // Same bound as the server's, for the same reason — a client's socket is just as reachable.
    private void PumpSocket()
    {
        while (_incoming.Count < UdpServerTransport.MaxQueuedEvents && _socket.Available > 0)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            byte[] datagram;
            try
            {
                datagram = _socket.Receive(ref remote);
            }
            catch (SocketException)
            {
                return;
            }
            if (_channel.HandleDatagram(datagram, out byte[] payload))
                _incoming.Enqueue(payload);
        }
    }

    public void Update(double now)
    {
        _now = now;
        _channel.Update(now);
        if (_channel.HasGivenUp)
            IsConnected = false;
    }

    private void TrySend(byte[] datagram)
    {
        try
        {
            _socket.Send(datagram, datagram.Length);
        }
        catch (SocketException) { /* transient; reliable frames retry */ }
    }

    public void Close() => _socket.Dispose();
}
