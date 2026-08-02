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

    // Datagrams read from the socket per Update, whatever they turn out to be. Bounds the loop even when
    // nothing being sent produces an event.
    public const int MaxReadsPerPump = 1024;

    // Counting events is not a memory bound: an unreliable datagram can carry ~65 KiB, so a full queue of
    // them is hundreds of megabytes retained before anyone has said Hello. The largest message the
    // protocol writes is a full ZombieList at roughly 6 KiB, so anything past this is not ours.
    public const int MaxPayloadBytes = 16 * 1024;

    // And the queue is bounded by what it holds, not only by how many things it holds.
    public const int MaxQueuedBytes = 4 * 1024 * 1024;

    private int _queuedBytes;

    // Datagrams dropped for exceeding MaxPayloadBytes or the queued-byte budget.
    public long OversizedDropped { get; private set; }

    private readonly Queue<ServerTransportEvent> _events = new();
    private int _nextId = 1;
    private double _now;

    public UdpServerTransport(ushort port)
    {
        _socket = new UdpClient(new IPEndPoint(IPAddress.Any, port));
    }

    // Dequeue only. Pumping happens once per Update, not per dequeue: NetServer drains up to
    // MaxEventsPerUpdate events, and a per-pump read budget reset on each of those would have allowed
    // MaxEventsPerUpdate * MaxReadsPerPump socket reads in one frame — half a million — which is not a
    // bound on frame work at all. One pump per Update is the bound.
    public bool TryReceive(out ServerTransportEvent evt)
    {
        bool got = _events.TryDequeue(out evt);
        if (got)
            _queuedBytes -= evt.Payload.Length;
        return got;
    }

    // Two separate bounds, because they stop different things.
    //
    // The queue cap keeps managed memory finite: past it, datagrams are left in the socket's receive
    // buffer for the OS to discard, which is the right backpressure for UDP.
    //
    // The read cap bounds the loop itself, and it cannot be expressed as the queue cap. Plenty of
    // datagrams produce no event at all — an ack, a duplicate reliable frame, an unknown channel prefix,
    // an empty payload — so a sender that only ever sends those leaves _events.Count untouched and the
    // queue guard never trips. Counting reads is what makes the loop terminate regardless of what arrives.
    private void PumpSocket()
    {
        int reads = MaxReadsPerPump;
        while (reads-- > 0
            && _events.Count < MaxQueuedEvents
            && _queuedBytes < MaxQueuedBytes
            && _socket.Available > 0)
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

            if (datagram.Length > MaxPayloadBytes)
            {
                OversizedDropped++;
                continue; // nothing this protocol writes is this big
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
            {
                _events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, connection, payload));
                _queuedBytes += payload.Length;
            }
        }
    }

    public void Update(double now)
    {
        _now = now;
        PumpSocket();

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
        if (_incoming.TryDequeue(out byte[]? dequeued))
        {
            payload = dequeued;
            return true;
        }
        payload = Array.Empty<byte>();
        return false;
    }

    // Same two bounds as the server's, for the same reasons — a client's socket is just as reachable, and
    // an ack or a duplicate produces no payload here either.
    private void PumpSocket()
    {
        int reads = UdpServerTransport.MaxReadsPerPump;
        while (reads-- > 0
            && _incoming.Count < UdpServerTransport.MaxQueuedEvents
            && _socket.Available > 0)
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
            if (datagram.Length > UdpServerTransport.MaxPayloadBytes)
                continue;
            if (_channel.HandleDatagram(datagram, out byte[] payload))
                _incoming.Enqueue(payload);
        }
    }

    public void Update(double now)
    {
        _now = now;
        PumpSocket();
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
