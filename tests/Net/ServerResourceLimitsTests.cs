using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// How much a peer could make the server hold, and how much of one frame it could occupy, was decided
// entirely by how much that peer chose to send. These pin the ceilings that changed it: the per-Update
// event budget, the outstanding-reliable-send cap, and the player-name clamp the payload cap made
// load-bearing.
//
// The input backlog and the tick catch-up are bounded in #36 instead, which arrived at both from the
// wrong-map work with a better design than the one this PR originally carried — a 4-frame jitter buffer
// rather than 32, and a wall-clock speed budget. Those live there now; nothing here duplicates them.
public class ServerResourceLimitsTests
{
    private sealed class FakeConnection : ITransportConnection
    {
        private static int NextId;
        private readonly int _id = ++NextId;
        public int Id => _id;
        public void Send(byte[] payload, ESendType sendType) { }
        public void Close() { }
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public readonly Queue<ServerTransportEvent> Events = new();

        public void Connect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, c, Array.Empty<byte>()));

        public void Message(FakeConnection c, byte[] payload) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, c, payload));

        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) { }
        public void Close() { }
    }

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static ServerSimulation FlatSim() => new(new HeightfieldMoveSolver(FlatGround));

    private static InputCommand Forward(uint frame) => new(frame, 0, -1, false, false, 0, 90);

    // A reliable send is retained until acked or GiveUpAfter, and Update retransmits the whole set. A peer
    // that never acks — a flood of pre-join ServerInfoRequests, each answered reliably — would otherwise
    // grow that set as fast as it could ask.
    [Fact]
    public void OutstandingReliableSendsAreBounded()
    {
        var sent = new List<byte[]>();
        var channel = new ReliableChannel(sent.Add);

        for (int i = 0; i < ReliableChannel.MaxPending * 4; i++)
            channel.Send(new byte[] { (byte)ENetMessage.PlayerLeft, 1 }, ESendType.Reliable, now: 0);

        Assert.Equal(ReliableChannel.MaxPending, sent.Count);
        Assert.True(channel.RefusedSends > 0);

        // And the connection is given up rather than the frames silently vanishing. Callers send
        // reliably because they then treat the message as delivered — ZombieHost marks a region loaded
        // once it has pushed its chunks — so a dropped frame with a live connection leaves the two sides
        // disagreeing with nothing to notice or retry it.
        Assert.True(channel.HasGivenUp, "a full pending set must end the connection, not the message");
    }

    // The burst the protocol itself aims at one healthy connection, which must not be mistaken for a peer
    // that has stopped reading. A server filling in a single Update sends the first-admitted player a
    // Welcome plus a PlayerJoined for each of the other 253 — 254 frames with no chance to ack, since the
    // transport pumps once per Update — and a region entered on that tick adds its chunks on top.
    //
    // This is the case the give-up-on-full change made worse: at MaxPending = 256 those chunks ended a
    // connection whose only distinction was having joined first.
    [Fact]
    public void AFullRosterAdmittedInOneUpdateDoesNotEndTheConnection()
    {
        var sent = new List<byte[]>();
        var channel = new ReliableChannel(sent.Add);

        channel.Send(new byte[] { (byte)ENetMessage.Welcome }, ESendType.Reliable, now: 0);
        for (int i = 1; i < PlayerIdPool.Capacity; i++)
            channel.Send(new byte[] { (byte)ENetMessage.PlayerJoined, (byte)i }, ESendType.Reliable, now: 0);

        // Plus a region's worth of zombie chunks on the same tick.
        const int chunks = 6;
        for (int i = 0; i < chunks; i++)
            channel.Send(new byte[] { (byte)ENetMessage.StateUpdate }, ESendType.Reliable, now: 0);

        Assert.Equal(PlayerIdPool.Capacity + chunks, sent.Count);
        Assert.Equal(0, channel.RefusedSends);
        Assert.False(channel.HasGivenUp, "a full-roster admission is legitimate traffic, not a dead peer");
    }

    [Fact]
    public void APeerThatAcksKeepsItsConnection()
    {
        var sent = new List<byte[]>();
        ReliableChannel? channel = null;
        // The receiver acks straight back, as a reachable peer does.
        var receiver = new ReliableChannel(ack => channel!.HandleDatagram(ack, out _));
        channel = new ReliableChannel(frame =>
        {
            sent.Add(frame);
            receiver.HandleDatagram(frame, out _);
        });

        for (int i = 0; i < ReliableChannel.MaxPending * 4; i++)
            channel.Send(new byte[] { (byte)ENetMessage.PlayerLeft, 1 }, ESendType.Reliable, now: 0);

        Assert.Equal(ReliableChannel.MaxPending * 4, sent.Count);
        Assert.Equal(0, channel.RefusedSends);
        Assert.False(channel.HasGivenUp);
    }

    [Fact]
    public void UnreliableSendsAreNotBoundedByThePendingCap()
    {
        var sent = new List<byte[]>();
        var channel = new ReliableChannel(sent.Add);

        for (int i = 0; i < ReliableChannel.MaxPending * 4; i++)
            channel.Send(new byte[] { (byte)ENetMessage.StateUpdate }, ESendType.Unreliable, now: 0);

        // Nothing is retained for an unreliable frame, so there is nothing to bound.
        Assert.Equal(ReliableChannel.MaxPending * 4, sent.Count);
        Assert.Equal(0, channel.RefusedSends);
    }

    // A name is the only unbounded string a peer puts into server state, and Welcome names every joined
    // player. Left unbounded, one oversized name makes the Welcome sent to everyone who joins afterwards
    // exceed the transport's payload cap — those clients are admitted and then time out without joining.
    [Fact]
    public void AFullRosterOfMaximumNamesStillFitsInOneDatagram()
    {
        var roster = new List<PlayerListing>();
        string longest = new string('W', NetMessages.MaxNameBytes * 4); // clamped down on admission
        for (int i = 0; i < PlayerIdPool.Capacity; i++)
        {
            roster.Add(new PlayerListing
            {
                PlayerId = (byte)(PlayerIdPool.First + i),
                Name = NetMessages.ClampName(longest),
                Position = new Vector3(1f, 2f, 3f),
            });
        }

        // Through WriteWelcome because WriteListing is private, and measuring the real encoded datagram
        // is the point: the assertion is that the clamp keeps a full roster deliverable.
        byte[] welcome = NetMessages.WriteWelcome(1, tick: 0, rosterVersion: 0, roster);

        Assert.True(welcome.Length < UdpServerTransport.MaxPayloadBytes,
            $"a full roster is {welcome.Length} bytes, past the " +
            $"{UdpServerTransport.MaxPayloadBytes}-byte transport cap");
    }

    [Fact]
    public void ClampNameCutsOnACharacterBoundary()
    {
        // Four bytes each in UTF-8, so the cap does not land on a whole number of them.
        string emoji = string.Concat(System.Linq.Enumerable.Repeat("\U0001F600", 20));
        string clamped = NetMessages.ClampName(emoji);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(clamped) <= NetMessages.MaxNameBytes);
        // Round-tripping proves nothing was cut through a multi-byte sequence.
        Assert.Equal(clamped, System.Text.Encoding.UTF8.GetString(
            System.Text.Encoding.UTF8.GetBytes(clamped)));
        Assert.DoesNotContain('\uFFFD', clamped);
    }

    // The emoji case above does not actually pin this. U+1F600 is one scalar, so a clamp written over
    // runes rather than text elements passes it \u2014 valid UTF-8, no replacement character, nothing cut
    // through a multi-byte sequence. A combining mark is where the two part company: "a" plus U+0301 is
    // two scalars and one grapheme, and cutting between them leaves a bare "a" where the name had "\u00E1".
    [Fact]
    public void ClampNameKeepsACombiningMarkWithItsBase()
    {
        const string acute = "a\u0301"; // 1 + 2 = 3 UTF-8 bytes, one text element
        string name = string.Concat(System.Linq.Enumerable.Repeat(acute, 20));
        string clamped = NetMessages.ClampName(name);

        // 32 / 3 = 10 whole clusters; an 11th would need 33 bytes, so 30 is the honest ceiling here.
        Assert.Equal(30, System.Text.Encoding.UTF8.GetByteCount(clamped));
        Assert.Equal(string.Concat(System.Linq.Enumerable.Repeat(acute, 10)), clamped);

        // Stated as a property too, so the intent survives the arithmetic changing with MaxNameBytes.
        // The name ends ON the combining mark: a clamp that cut a cluster would leave the bare base
        // instead, which is the exact failure this test exists for.
        Assert.Equal('\u0301', clamped[^1]);
        Assert.Equal(10, new System.Globalization.StringInfo(clamped).LengthInTextElements);
    }

    [Fact]
    public void ClampNameLeavesAnOrdinaryNameAlone()
    {
        Assert.Equal("player", NetMessages.ClampName("player"));
        Assert.Equal(string.Empty, NetMessages.ClampName(""));
    }

    // Over real sockets, because what changed is how the socket is read: into a fixed buffer one byte
    // past the cap, rather than letting UdpClient.Receive allocate for whatever arrived. Linux truncates
    // an over-long datagram into that buffer and Windows raises MessageSize, so this asserts on the
    // counter rather than on which of the two happened.
    //
    // Being straight about its reach: this pins that oversize is still REJECTED, and that the pump
    // survives one and keeps delivering — the parts the rewrite could plausibly break. It does not pin
    // the allocation bound that motivated the rewrite, which the old code would also have passed.
    // Measuring that means asserting on GC bytes across a loopback burst the OS is free to drop, which
    // is a flaky test dressed up as a strict one.
    [Fact]
    public void AnOversizedDatagramIsDroppedAndTheTransportStaysHealthy()
    {
        ushort port;
        using (var probe = new System.Net.Sockets.UdpClient(
            new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
        {
            port = (ushort)((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var transport = new UdpServerTransport(port);
        try
        {
            using var sender = new System.Net.Sockets.UdpClient();
            sender.Connect("127.0.0.1", port);
            var oversized = new byte[UdpServerTransport.MaxPayloadBytes + 1024];
            oversized[0] = (byte)ENetMessage.Input;

            // macOS caps an outgoing datagram at net.inet.udp.maxdgram — 9216 bytes by default, well
            // under what this sends — and checks it against the send buffer's high-water mark, so the
            // buffer has to be raised before the send rather than after it fails.
            sender.Client.SendBufferSize = Math.Max(sender.Client.SendBufferSize, oversized.Length * 2);

            bool emitted = true;
            try
            {
                sender.Send(oversized, oversized.Length);
            }
            catch (System.Net.Sockets.SocketException e)
                when (e.SocketErrorCode == System.Net.Sockets.SocketError.MessageSize)
            {
                // This host refuses to PUT a datagram that big on the wire even with the buffer raised.
                // That is a limit on this test's sender, not on the transport: the receive side has no
                // such cap, so a remote peer on a host without one still reaches the path. Recorded
                // rather than swallowed — the health assertion below still has to hold either way, and
                // the counter is only claimed when a datagram was actually sent.
                emitted = false;
            }

            // A legitimate datagram behind it must still be seen: the drop skips one read, it does not
            // abandon the pump or the connection.
            byte[] good = new byte[] { ReliableChannel.ChannelUnreliable, (byte)ENetMessage.Input, 0 };
            sender.Send(good, good.Length);

            ServerTransportEvent evt = default;
            bool sawMessage = false;
            for (int i = 0; i < 50 && !sawMessage; i++)
            {
                transport.Update(i * 0.01);
                while (transport.TryReceive(out evt))
                    if (evt.Type == ETransportEvent.Message)
                        sawMessage = true;
                System.Threading.Thread.Sleep(10);
            }

            if (emitted)
                Assert.True(transport.OversizedDropped > 0, "the oversized datagram was not counted");
            Assert.True(sawMessage, "the good datagram behind the oversized one never arrived");
        }
        finally
        {
            transport.Close();
        }
    }

    // A composite transport is how a listen server works: the host's loopback plus a LAN UDP transport.
    // Restarting at the first child on every TryReceive meant a backlogged host could consume the whole
    // per-Update budget forever and the LAN side would never be polled.
    [Fact]
    public void ACompositeTransportPollsEveryChild_EvenWhenTheFirstIsBacklogged()
    {
        var busy = new FakeServerTransport();
        var quiet = new FakeServerTransport();
        var composite = new CompositeServerTransport(busy, quiet);

        var noisy = new FakeConnection();
        for (int i = 0; i < 1000; i++)
            busy.Message(noisy, NetMessages.WriteInput(Forward(0)));

        var lan = new FakeConnection();
        quiet.Connect(lan);

        // Drain a handful of events: the second transport's single event must come out among them.
        bool sawQuiet = false;
        for (int i = 0; i < 8 && composite.TryReceive(out ServerTransportEvent evt); i++)
            if (evt.Connection == lan)
                sawQuiet = true;

        Assert.True(sawQuiet, "the second transport was never polled");
    }

    // The receive loop ran until the transport was empty, so a peer decided how long the frame was. The
    // remainder stays queued and drains next Update rather than being dropped.
    [Fact]
    public void AFloodOfEventsIsSpreadAcrossUpdatesRatherThanHandledInOne()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, FlatSim(), Vector3.Zero, "PEI");

        var flooder = new FakeConnection();
        transport.Connect(flooder);
        for (int i = 0; i < NetServer.MaxEventsPerUpdate * 3; i++)
            transport.Message(flooder, NetMessages.WriteInput(Forward(0)));

        int queued = transport.Events.Count;
        server.Update(0.0);

        Assert.Equal(queued - NetServer.MaxEventsPerUpdate, transport.Events.Count);

        server.Update(ServerSimulation.TickRate);
        Assert.Equal(queued - (NetServer.MaxEventsPerUpdate * 2), transport.Events.Count);
    }
}
