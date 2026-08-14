using System;
using System.Collections.Generic;

namespace UnturnedGodot.Net;

// Minimal reliability over an unreliable datagram pipe, for the handful of messages that need it
// (handshake, join/leave) — movement traffic stays raw. Every datagram gets a one-byte channel prefix:
// unreliable payloads pass through, reliable ones carry a sequence number and are retransmitted until
// the peer acknowledges, and the receiver acks + dedups so a retransmitted join never applies twice.
// Time is an explicit parameter; the whole class is deterministic and driven by its owner's Update.
public sealed class ReliableChannel
{
    public const byte ChannelUnreliable = 0;
    public const byte ChannelReliable = 1;
    public const byte ChannelAck = 2;

    public const double ResendInterval = 0.25; // seconds between retransmissions
    public const double GiveUpAfter = 10.0;    // stop retrying; the connection is considered dead

    private const int DedupWindow = 1024;

    // Outstanding unacked reliable frames. Every one is retained until acked or GiveUpAfter, and Update
    // scans and retransmits the whole set, so this is both a memory bound and a per-Update work bound.
    //
    // It matters most before a peer has authenticated: a flood of ServerInfoRequest — answerable, by
    // design, on a connection that has said nothing else — produces one reliable reply each, and without
    // this the set grows as fast as the requests arrive.
    //
    // Sized ABOVE the largest burst the protocol itself can aim at one healthy connection, because
    // reaching it now ends the connection rather than the message. A server filling in a single Update —
    // everyone reconnecting after a restart — sends the first-admitted player its Welcome plus a
    // PlayerJoined for each of the other 253, which is 254 frames before a single ack can come back:
    // the transport pumps once per Update, so nothing is acked mid-burst. A region entered on that same
    // tick then adds ceil(zombies / ZombieNetMessages.ListChunkSize) more. At 256 the third of those
    // chunks would have disconnected a player whose only mistake was joining first.
    //
    // 1024 leaves roughly four times that burst. Detecting a peer that has stopped reading is not this
    // constant's job — GiveUpAfter already does it, on evidence (ten seconds without an ack) rather than
    // on a queue depth a legitimate roster can reach.
    public const int MaxPending = 1024;

    // How many bytes this channel puts on the wire around a payload, by send type. The traffic counters
    // are meant to report what crossed the link rather than what the protocol wrote, and this is the
    // difference between the two — one byte of channel prefix unreliable, plus two of sequence reliable.
    public static int FrameBytes(ESendType sendType) => sendType == ESendType.Reliable ? 3 : 1;

    // Reliable sends refused because MaxPending was already reached. Stored on the traffic object rather
    // than here, so a caller reading "what has gone wrong on this link" reads one place; this stays as
    // the name everything already uses.
    public long RefusedSends => _traffic.RefusedSends;

    private readonly NetTraffic _traffic;
    private readonly Action<byte[]> _rawSend;
    private readonly Dictionary<ushort, (byte[] Datagram, double FirstSent, double LastSent)> _pending = new();
    private readonly HashSet<ushort> _seen = new();
    private readonly Queue<ushort> _seenOrder = new();
    private ushort _nextSequence;

    // True once a reliable send exhausted GiveUpAfter without an ack — the peer is unreachable.
    public bool HasGivenUp { get; private set; }

    // The traffic object is optional so the channel's own tests can build one without an owner; a
    // channel that reports nowhere still counts, it just counts into an instance nobody reads.
    public ReliableChannel(Action<byte[]> rawSend, NetTraffic? traffic = null)
    {
        _rawSend = rawSend;
        _traffic = traffic ?? new NetTraffic();
    }

    public void Send(byte[] payload, ESendType sendType, double now)
    {
        if (sendType == ESendType.Unreliable)
        {
            var datagram = new byte[payload.Length + 1];
            datagram[0] = ChannelUnreliable;
            payload.CopyTo(datagram, 1);
            _traffic.RecordSent(NetTraffic.TypeOf(payload), datagram.Length);
            _rawSend(datagram);
            return;
        }

        if (_pending.Count >= MaxPending)
        {
            // Give up on the peer rather than drop the frame. Callers send reliably precisely because they
            // then mutate state as though it arrived — ZombieHost marks a region loaded once it has pushed
            // its chunks — so silently discarding one leaves the two sides disagreeing with nothing to
            // notice it, and nothing to retry it. A peer holding this many frames unacked is not reading,
            // which is the same conclusion the GiveUpAfter deadline reaches by a slower route, so this
            // takes the same exit: the owner drops the connection on its next Update.
            //
            // The frame is still not sent — it cannot be, the set is full — but the connection dies with
            // it, so "reliable" keeps meaning delivered-or-disconnected rather than silently-maybe.
            HasGivenUp = true;
            _traffic.CountRefusedSend();
            return;
        }

        ushort seq = _nextSequence++;
        var reliable = new byte[payload.Length + 3];
        reliable[0] = ChannelReliable;
        reliable[1] = (byte)(seq & 0xFF);
        reliable[2] = (byte)(seq >> 8);
        payload.CopyTo(reliable, 3);
        _pending[seq] = (reliable, now, now);
        _traffic.RecordSent(NetTraffic.TypeOf(payload), reliable.Length);
        _rawSend(reliable);
    }

    // Processes one incoming datagram; true when it carries a payload the owner should deliver.
    public bool HandleDatagram(byte[] datagram, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (datagram.Length == 0)
            return false;

        // Counted before anything decides whether it is worth delivering. An ack, a duplicate and a
        // frame in an unknown channel all cost the same bandwidth as one that carries a message, and a
        // reading that only counted the useful ones would go quiet during exactly the retransmission
        // storm it exists to make visible. Only a reliable frame long enough to hold a payload can be
        // attributed to a message type; everything else is Other, which is what it is.
        _traffic.RecordReceived(
            datagram[0] == ChannelUnreliable && datagram.Length >= 2 ? Classify(datagram[1])
            : datagram[0] == ChannelReliable && datagram.Length >= 4 ? Classify(datagram[3])
            : NetTraffic.OtherType,
            datagram.Length);

        switch (datagram[0])
        {
            case ChannelUnreliable:
                // A payload has to carry at least its message-type byte. A bare channel prefix decodes
                // to an empty payload, which every reader indexes at [0] — drop it here rather than
                // making each of them defend itself.
                if (datagram.Length < 2)
                    return false;
                payload = datagram[1..];
                return true;

            case ChannelReliable:
                {
                    if (datagram.Length < 3)
                        return false;
                    ushort seq = (ushort)(datagram[1] | (datagram[2] << 8));
                    // Always ack, even duplicates — and count the ack, because a peer being flooded with
                    // reliable frames answers every one of them and that return traffic is a real share
                    // of a saturated uplink.
                    var ack = new[] { ChannelAck, datagram[1], datagram[2] };
                    _traffic.RecordSent(NetTraffic.OtherType, ack.Length);
                    _rawSend(ack);
                    // Acked above so the sender stops retrying, but an empty payload carries no message
                    // type and must not reach a reader. Same rule as the unreliable channel. Checked
                    // before the dedup set is touched: a bare frame must not burn a sequence number and
                    // suppress a later, complete frame that carries the same one.
                    if (datagram.Length < 4)
                        return false;
                    if (!_seen.Add(seq))
                        return false; // retransmission of something already delivered
                    _seenOrder.Enqueue(seq);
                    if (_seenOrder.Count > DedupWindow)
                        _seen.Remove(_seenOrder.Dequeue());
                    payload = datagram[3..];
                    return true;
                }

            case ChannelAck:
                {
                    if (datagram.Length >= 3)
                        _pending.Remove((ushort)(datagram[1] | (datagram[2] << 8)));
                    return false;
                }

            default:
                return false; // unknown channel: drop
        }
    }

    // A message-type byte lifted straight out of a framed datagram, bucketed the same way a payload's
    // first byte is. Kept next to the framing that produces the offsets, so the two cannot drift.
    private static int Classify(byte typeByte) =>
        typeByte < NetTraffic.TypeCount ? typeByte : NetTraffic.OtherType;

    // Retransmits unacked reliable datagrams; marks the channel dead after GiveUpAfter.
    public void Update(double now)
    {
        List<ushort>? expired = null;
        foreach ((ushort seq, (byte[] datagram, double firstSent, double lastSent)) in _pending)
        {
            if (now - firstSent > GiveUpAfter)
            {
                (expired ??= new List<ushort>()).Add(seq);
                HasGivenUp = true;
            }
            else if (now - lastSent >= ResendInterval)
            {
                _pending[seq] = (datagram, firstSent, now);
                // A resend is bandwidth the caller never asked for, and it is the whole cost of a big
                // reliable message on a lossy link — a full Welcome goes out again every quarter second
                // until it is acked. Counting it is what makes that visible instead of theoretical.
                _traffic.RecordSent(datagram.Length >= 4 ? Classify(datagram[3]) : NetTraffic.OtherType,
                    datagram.Length);
                _rawSend(datagram);
            }
        }
        if (expired != null)
            foreach (ushort seq in expired)
                _pending.Remove(seq);
    }
}
