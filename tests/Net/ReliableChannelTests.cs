using System.Collections.Generic;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

public class ReliableChannelTests
{
    private sealed class Wire
    {
        public readonly List<byte[]> Sent = new();
        public void Send(byte[] d) => Sent.Add(d);
    }

    [Fact]
    public void Unreliable_PassesThroughWithChannelPrefix()
    {
        var wire = new Wire();
        var channel = new ReliableChannel(wire.Send);
        channel.Send(new byte[] { 7, 8 }, ESendType.Unreliable, now: 0);

        Assert.Single(wire.Sent);
        Assert.Equal(ReliableChannel.ChannelUnreliable, wire.Sent[0][0]);

        Assert.True(channel.HandleDatagram(wire.Sent[0], out byte[] payload));
        Assert.Equal(new byte[] { 7, 8 }, payload);
    }

    [Fact]
    public void Reliable_DeliversOnce_AcksAlways_DedupsRetransmissions()
    {
        var senderWire = new Wire();
        var receiverWire = new Wire();
        var sender = new ReliableChannel(senderWire.Send);
        var receiver = new ReliableChannel(receiverWire.Send);

        sender.Send(new byte[] { 42 }, ESendType.Reliable, now: 0);
        byte[] datagram = senderWire.Sent[0];

        Assert.True(receiver.HandleDatagram(datagram, out byte[] payload));
        Assert.Equal(new byte[] { 42 }, payload);
        Assert.Single(receiverWire.Sent); // one ack
        Assert.Equal(ReliableChannel.ChannelAck, receiverWire.Sent[0][0]);

        // A retransmitted copy is acked again but NOT delivered again.
        Assert.False(receiver.HandleDatagram(datagram, out _));
        Assert.Equal(2, receiverWire.Sent.Count);
    }

    [Fact]
    public void UnackedReliable_ResendsOnInterval_AckStopsIt()
    {
        var wire = new Wire();
        var channel = new ReliableChannel(wire.Send);
        channel.Send(new byte[] { 1 }, ESendType.Reliable, now: 0);
        Assert.Single(wire.Sent);

        channel.Update(0.1); // before the interval: nothing
        Assert.Single(wire.Sent);
        channel.Update(0.3); // past 0.25: retransmit
        Assert.Equal(2, wire.Sent.Count);

        // Ack (as produced by a receiver) stops retransmission.
        var ack = new[] { ReliableChannel.ChannelAck, wire.Sent[0][1], wire.Sent[0][2] };
        Assert.False(channel.HandleDatagram(ack, out _));
        channel.Update(1.0);
        Assert.Equal(2, wire.Sent.Count);
        Assert.False(channel.HasGivenUp);
    }

    [Fact]
    public void NeverAcked_GivesUpAfterTheDeadline()
    {
        var wire = new Wire();
        var channel = new ReliableChannel(wire.Send);
        channel.Send(new byte[] { 1 }, ESendType.Reliable, now: 0);

        for (double t = 0.25; t < 11; t += 0.25)
            channel.Update(t);

        Assert.True(channel.HasGivenUp);
        int resends = wire.Sent.Count;
        channel.Update(20); // nothing pending anymore
        Assert.Equal(resends, wire.Sent.Count);
    }

    [Fact]
    public void MalformedDatagrams_AreDropped()
    {
        var channel = new ReliableChannel(_ => { });
        Assert.False(channel.HandleDatagram(System.Array.Empty<byte>(), out _));
        Assert.False(channel.HandleDatagram(new byte[] { ReliableChannel.ChannelReliable, 1 }, out _)); // short
        Assert.False(channel.HandleDatagram(new byte[] { ReliableChannel.ChannelAck }, out _));         // short ack
        Assert.False(channel.HandleDatagram(new byte[] { 9, 1, 2 }, out _));                            // unknown
    }

    [Fact]
    public void DedupWindow_ForgetsOldSequences()
    {
        var senderWire = new Wire();
        var sender = new ReliableChannel(senderWire.Send);
        var receiver = new ReliableChannel(_ => { });

        // Push more than the window of distinct sequences through; every fresh one delivers.
        int delivered = 0;
        for (int i = 0; i < 1100; i++)
        {
            sender.Send(new byte[] { (byte)i }, ESendType.Reliable, now: 0);
            if (receiver.HandleDatagram(senderWire.Sent[^1], out _))
                delivered++;
        }
        Assert.Equal(1100, delivered);
    }
}
