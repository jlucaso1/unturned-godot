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

    private readonly Action<byte[]> _rawSend;
    private readonly Dictionary<ushort, (byte[] Datagram, double FirstSent, double LastSent)> _pending = new();
    private readonly HashSet<ushort> _seen = new();
    private readonly Queue<ushort> _seenOrder = new();
    private ushort _nextSequence;

    // True once a reliable send exhausted GiveUpAfter without an ack — the peer is unreachable.
    public bool HasGivenUp { get; private set; }

    public ReliableChannel(Action<byte[]> rawSend) => _rawSend = rawSend;

    public void Send(byte[] payload, ESendType sendType, double now)
    {
        if (sendType == ESendType.Unreliable)
        {
            var datagram = new byte[payload.Length + 1];
            datagram[0] = ChannelUnreliable;
            payload.CopyTo(datagram, 1);
            _rawSend(datagram);
            return;
        }

        ushort seq = _nextSequence++;
        var reliable = new byte[payload.Length + 3];
        reliable[0] = ChannelReliable;
        reliable[1] = (byte)(seq & 0xFF);
        reliable[2] = (byte)(seq >> 8);
        payload.CopyTo(reliable, 3);
        _pending[seq] = (reliable, now, now);
        _rawSend(reliable);
    }

    // Processes one incoming datagram; true when it carries a payload the owner should deliver.
    public bool HandleDatagram(byte[] datagram, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        if (datagram.Length == 0)
            return false;

        switch (datagram[0])
        {
            case ChannelUnreliable:
                payload = datagram[1..];
                return true;

            case ChannelReliable:
                {
                    if (datagram.Length < 3)
                        return false;
                    ushort seq = (ushort)(datagram[1] | (datagram[2] << 8));
                    _rawSend(new[] { ChannelAck, datagram[1], datagram[2] }); // always ack, even duplicates
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
                _rawSend(datagram);
            }
        }
        if (expired != null)
            foreach (ushort seq in expired)
                _pending.Remove(seq);
    }
}
