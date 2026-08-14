using System;

namespace UnturnedGodot.Net;

public enum EServerQueryState : byte
{
    Pending,  // still asking
    Answered, // Info holds what the server runs
    TimedOut, // nothing answered, or the transport gave up on the peer
}

// The pre-flight a client runs BEFORE building anything: "which level are you running?".
//
// Without it the client had to guess, and it guessed with whatever the map browser happened to have
// selected — which is how a player joined a PEI host and spawned into California 2. The join flow now
// asks first and builds the level it is told about, so the two ends agree by construction and the
// handshake's level check is left as the backstop it should be.
//
// Same polled shape as the rest of the stack: the owner drives it with Update(now) and reads State,
// so it is deterministic in tests and needs no threads.
public sealed class ServerQuery
{
    public const double RetryInterval = 1.0; // a lost UDP probe must not cost the whole timeout
    public const double Timeout = 8.0;

    private readonly IClientTransport _transport;
    private double _startedAt = double.NaN;
    private double _lastRequest = double.NegativeInfinity;

    public EServerQueryState State { get; private set; }
    public ServerInfo? Info { get; private set; }

    // Datagrams that reached a decoder and did not survive it: whatever answered a UDP probe is not
    // necessarily our server. See NetClient.MalformedPacketsDropped.
    public long MalformedPacketsDropped { get; private set; }

    public ServerQuery(IClientTransport transport) => _transport = transport;

    public void Update(double now)
    {
        if (State != EServerQueryState.Pending)
            return;

        _transport.Update(now); // give the transport the clock BEFORE any reliable send
        if (double.IsNaN(_startedAt))
            _startedAt = now;

        // Drained before the retry is due, not after: an answer already sitting in the inbox settles
        // the query, and sending one more probe on the way to reading it is pure noise.
        while (_transport.TryReceive(out byte[] payload))
        {
            if (!MalformedPacket.TryDecode(payload, ReadType, out ENetMessage type))
            {
                MalformedPacketsDropped++;
                continue;
            }
            if (type != ENetMessage.ServerInfo)
                continue; // a server mid-broadcast can reach us before it ever heard the request
            if (!MalformedPacket.TryDecode(payload, ReadServerInfo, out ServerInfo? info))
            {
                MalformedPacketsDropped++;
                continue;
            }

            Info = info;
            State = EServerQueryState.Answered;
            return;
        }

        if (now - _lastRequest >= RetryInterval)
        {
            _lastRequest = now;
            // UNRELIABLE, and the RetryInterval above is why nothing is lost by it.
            //
            // Sent reliably, this was the one message that made a server allocate on behalf of a
            // stranger: the frame is retained and retransmitted until acked, and the server had to hold
            // a Connection and a ReliableChannel just to ack it — so the cheapest thing anyone can send
            // was also the thing that took one of 256 connection slots for fifteen seconds. Answering it
            // connectionlessly means there is nothing on the far end to ack, and the reliability
            // belonged to the asker all along: this loop already re-asks every second until it is
            // answered or times out, which is what a query does.
            _transport.Send(NetMessages.WriteServerInfoRequest(), ESendType.Unreliable);
        }

        // The reliable channel exhausted its retries: nobody is on the other end of this socket.
        if (!_transport.IsConnected || now - _startedAt > Timeout)
            State = EServerQueryState.TimedOut;
    }

    // Cached so a method group does not allocate a delegate on every received message.
    private static readonly Func<byte[], ENetMessage> ReadType = NetMessages.TypeOf;
    private static readonly Func<byte[], ServerInfo> ReadServerInfo = NetMessages.ReadServerInfo;
}
