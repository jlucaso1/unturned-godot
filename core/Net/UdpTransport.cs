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
        public required NetTraffic Traffic { get; init; }

        // Counted inside the channel rather than here: a retransmission is real bandwidth the caller
        // never asked for, and the channel is the only place that sees one.
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
    // them is hundreds of megabytes retained before anyone has said Hello. Nothing this protocol writes
    // comes close to 16 KiB — every message that walks a collection is now split at NetChunks'
    // 1200-byte path budget — so anything past this is not ours.
    //
    // This is a ceiling on what the transport will ACCEPT, and deliberately not a budget anything spends.
    // It once read "the largest message the protocol writes is a full ZombieList at roughly 6 KiB", which
    // was wrong twice over: a ZombieList chunk is 1153 bytes, and the largest message was Welcome, whose
    // full roster was about 12.5 KB until it was chunked too.
    public const int MaxPayloadBytes = 16 * 1024;

    // And the queue is bounded by what it holds, not only by how many things it holds.
    public const int MaxQueuedBytes = 4 * 1024 * 1024;

    private int _queuedBytes;

    // Every connection's traffic, rolled up: what this listener has sent and received, split by message
    // type, plus the drop counters below.
    public NetTraffic Traffic { get; } = new();

    // See IServerTransport: answers a stranger's question without allocating a connection for them.
    public Func<byte[], byte[]?>? AnswerConnectionless { get; set; }

    // Datagrams dropped for exceeding MaxPayloadBytes, and only those. Reaching the queued-byte or
    // queued-event budget stops the pump instead: those datagrams are never read, so they stay in the
    // socket's receive buffer for the OS to discard and there is nothing here to count. Worth being
    // precise about — read as "all backpressure", this counter would sit at zero through exactly the
    // overload it looks like it measures.
    public long OversizedDropped => Traffic.OversizedDropped;

    // Read into this rather than letting the socket hand back a fresh array each time. UdpClient.Receive
    // allocates for whatever arrived — up to ~65 KiB — BEFORE the payload cap can reject it, and an
    // oversized datagram adds neither an event nor queued bytes, so neither queue guard trips and only
    // the read counter is left: 1024 reads of 64 KiB is ~64 MiB of allocation per Update, driven by a
    // sender who need only spend the bandwidth. The cap bounded what the transport RETAINS; it never
    // bounded what it allocates on the way there, which is the cost an attacker actually reaches.
    //
    // One byte over the cap, so "too big" is a length the buffer can still represent: a datagram that
    // fills it is by definition past MaxPayloadBytes.
    private readonly byte[] _readBuffer = new byte[MaxPayloadBytes + 1];

    // Live connections per source address, so the per-address cap does not need a scan.
    private readonly Dictionary<IPAddress, int> _perAddress = new();

    // A Connection is keyed by address AND port, and every new key allocates one plus its ReliableChannel.
    // A sender that varies its source port therefore grew this table without limit, from a single host,
    // before saying anything a server would recognise — no handshake, no protocol version, nothing to
    // authenticate against. These two caps are what make the table finite.
    //
    // Refusing means dropping the datagram: no connection object, no reply, no state kept about the
    // sender. Deliberately not a ban list — that is a feature with an unban path and a persistence story,
    // and it is not what stops the table growing.
    public const int MaxConnections = 256;

    // Generous for the legitimate shape (several players behind one NAT, a host plus its own loopback)
    // and still far below what one host needs to matter.
    public const int MaxConnectionsPerAddress = 8;

    // Datagrams dropped because they would have created a connection past one of the caps.
    public long RefusedConnections => Traffic.RefusedConnections;

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
        // Room for everything ONE datagram can produce, checked before it is read rather than after.
        // A datagram from an unknown peer enqueues two events, Connected and then Message, so testing
        // for a single free slot let the queue finish one past its cap; likewise a payload accepted at
        // MaxQueuedBytes - 1 could add a further MaxPayloadBytes. Both overshoots are small and bounded
        // — one event, and 16 KiB against 4 MiB — so nothing was ever at risk. But these constants are
        // what anyone sizing this thing's memory would read, and they should be true as written.
        while (reads-- > 0
            && _events.Count <= MaxQueuedEvents - 2
            && _queuedBytes <= MaxQueuedBytes - MaxPayloadBytes
            && _socket.Available > 0)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                received = _socket.Client.ReceiveFrom(_readBuffer, SocketFlags.None, ref from);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize)
            {
                // Windows reports an over-long datagram rather than truncating it. Either way it is
                // consumed and gone; Linux's truncation is caught by the length check below.
                Traffic.CountOversizedDropped();
                continue;
            }
            catch (SocketException)
            {
                return; // ICMP port-unreachable surfacing on Windows/loopback; nothing to read
            }

            if (received > MaxPayloadBytes)
            {
                Traffic.CountOversizedDropped();
                continue; // nothing this protocol writes is this big
            }

            var remote = (IPEndPoint)from;
            // Copied only once it is known to be within the cap, so the per-read allocation is bounded by
            // MaxPayloadBytes and is the same array the queue would hold anyway.
            byte[] datagram = _readBuffer[..received];

            string key = remote.ToString();
            if (!_connections.TryGetValue(key, out Connection? connection))
            {
                // Answered before anything is allocated, if the server recognises it as a question that
                // needs no connection. This is what stops the pre-handshake table being exhaustible:
                // the bulk of legitimate traffic from strangers is "which map are you running?", and
                // serving that out of a Connection meant every asker held one of 256 slots for 15 s.
                //
                // Only an UNRELIABLE frame qualifies. A reliable one wants an ack, and acking is
                // per-connection state by definition — which is the thing being avoided. ServerQuery
                // sends this unreliably and retries on its own schedule, which is what a query does.
                if (AnswerConnectionless is { } answer
                    && datagram.Length >= 2 && datagram[0] == ReliableChannel.ChannelUnreliable
                    && answer(datagram[1..]) is { } reply)
                {
                    // One datagram out for one datagram in, to the address that asked, and nothing
                    // retained. Framed by hand because there is no channel here to do it — that is the
                    // point. The read budget (MaxReadsPerPump) is what bounds how often this can happen.
                    var framed = new byte[reply.Length + 1];
                    framed[0] = ReliableChannel.ChannelUnreliable;
                    reply.CopyTo(framed, 1);
                    Traffic.RecordReceived(NetTraffic.TypeOf(datagram[1..]), received);
                    Traffic.RecordSent(NetTraffic.TypeOf(reply), framed.Length);
                    TrySendTo(framed, new IPEndPoint(remote.Address, remote.Port));
                    continue;
                }

                _perAddress.TryGetValue(remote.Address, out int fromAddress);
                if (_connections.Count >= MaxConnections || fromAddress >= MaxConnectionsPerAddress)
                {
                    Traffic.CountRefusedConnection();
                    continue; // drop it; an unauthenticated sender does not get to allocate
                }

                var endpoint = new IPEndPoint(remote.Address, remote.Port);
                var traffic = new NetTraffic(Traffic);
                connection = new Connection
                {
                    Endpoint = endpoint,
                    Owner = this,
                    Id = _nextId++,
                    Traffic = traffic,
                    Channel = null!,
                    // Born mid-drain: stamp the current clock, or the first reliable reply (Welcome) would
                    // carry time 0 and hit ReliableChannel's give-up deadline on the next Update.
                    SendNow = _now,
                    LastHeard = _now,
                };
                connection.Channel = new ReliableChannel(d => TrySendTo(d, endpoint), traffic);
                _connections[key] = connection;
                _perAddress[remote.Address] = fromAddress + 1;
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
            connection.Traffic.Update(now);
            if (now - connection.LastHeard > SilenceTimeout || connection.Channel.HasGivenUp)
                (dead ??= new List<Connection>()).Add(connection);
        }
        if (dead != null)
            foreach (Connection connection in dead)
                Drop(connection);
        Traffic.Update(now); // after the children, so this window closes over bytes they have just counted
    }

    private void Drop(Connection connection)
    {
        // The per-address count is only decremented when the removal actually happened. Dropping the same
        // connection twice is reachable (a timeout racing an explicit close), and decrementing on the
        // second one would leak allowance back to that address — the cap would then be a suggestion.
        if (!_connections.Remove(connection.Endpoint.ToString()))
            return;

        if (_perAddress.TryGetValue(connection.Endpoint.Address, out int live))
        {
            if (live <= 1)
                _perAddress.Remove(connection.Endpoint.Address);
            else
                _perAddress[connection.Endpoint.Address] = live - 1;
        }

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
    private int _queuedBytes;
    private double _now;

    // Same reusable buffer as the server's, for the same reason: a client's socket is just as reachable,
    // and anyone who can reach it can make it allocate per read otherwise.
    private readonly byte[] _readBuffer = new byte[UdpServerTransport.MaxPayloadBytes + 1];

    public bool IsConnected { get; private set; } = true;

    // This client's own view of its one link, on the same terms the server keeps per connection.
    public NetTraffic Traffic { get; } = new();

    public UdpClientTransport(string host, ushort port)
    {
        _socket = new UdpClient();
        _socket.Connect(host, port);
        _channel = new ReliableChannel(TrySend, Traffic);
    }

    public void Send(byte[] payload, ESendType sendType) => _channel.Send(payload, sendType, _now);

    public bool TryReceive(out byte[] payload)
    {
        if (_incoming.TryDequeue(out byte[]? dequeued))
        {
            _queuedBytes -= dequeued.Length;
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
        // Only the byte reservation is needed here. A client datagram yields at most one payload — there
        // is no Connected event on this side — so the count check is already exact at one free slot.
        while (reads-- > 0
            && _incoming.Count < UdpServerTransport.MaxQueuedEvents
            && _queuedBytes <= UdpServerTransport.MaxQueuedBytes - UdpServerTransport.MaxPayloadBytes
            && _socket.Available > 0)
        {
            int received;
            try
            {
                // Connected socket, so the source is known and Receive is enough.
                received = _socket.Client.Receive(_readBuffer, SocketFlags.None);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.MessageSize)
            {
                Traffic.CountOversizedDropped();
                continue; // over-long, and already consumed
            }
            catch (SocketException)
            {
                return;
            }
            if (received > UdpServerTransport.MaxPayloadBytes)
            {
                Traffic.CountOversizedDropped();
                continue;
            }
            if (_channel.HandleDatagram(_readBuffer[..received], out byte[] payload))
            {
                _incoming.Enqueue(payload);
                _queuedBytes += payload.Length;
            }
        }
    }

    public void Update(double now)
    {
        _now = now;
        PumpSocket();
        _channel.Update(now);
        Traffic.Update(now);
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
