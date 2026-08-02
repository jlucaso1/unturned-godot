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

        // MERGE NOTE: #36 adds a roster version to this signature, so whichever of the two lands second
        // needs this one call updated to WriteWelcome(1, 0, 0, roster). Nothing else here collides — the
        // rest of the merge is clean, verified by merging the two branches and building the result. It
        // has to go through WriteWelcome because WriteListing is private, and measuring the real encoded
        // datagram is the whole point: the assertion is that the clamp keeps a full roster deliverable.
        byte[] welcome = NetMessages.WriteWelcome(1, 0, roster);

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

    [Fact]
    public void ClampNameLeavesAnOrdinaryNameAlone()
    {
        Assert.Equal("player", NetMessages.ClampName("player"));
        Assert.Equal(string.Empty, NetMessages.ClampName(""));
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
