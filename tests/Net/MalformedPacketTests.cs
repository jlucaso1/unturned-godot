using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// Everything a socket hands the server is attacker-controlled. These drive the decoders with the
// datagrams a hostile or broken peer actually sends — empty, truncated, half a field short — and
// assert the server survives them, because the receive loop runs inside _PhysicsProcess where an
// escaping exception is a process death rather than a dropped packet.
public class MalformedPacketTests
{
    private sealed class FakeConnection : ITransportConnection
    {
        public readonly List<(byte[] Payload, ESendType SendType)> Sent = new();
        public bool Closed;
        public int Id => 7;
        public void Send(byte[] payload, ESendType sendType) => Sent.Add((payload, sendType));
        public void Close() => Closed = true;
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

    private const string Level = "PEI";

    private static (NetServer Server, FakeServerTransport Transport) Build()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        return (server, transport);
    }

    // The datagram shapes a peer can send that no writer produces. Each is a payload as the transport
    // would deliver it, i.e. already stripped of the channel prefix.
    public static TheoryData<string, byte[]> HostilePayloads() => new()
    {
        { "empty", Array.Empty<byte>() },
        { "unknown message type", new byte[] { 0xFF } },
        { "Hello with no version", new[] { (byte)ENetMessage.Hello } },
        { "Hello with no name", new[] { (byte)ENetMessage.Hello, NetMessages.ProtocolVersion } },
        // Name present, level missing: a v6 header truncated exactly where the new field starts.
        {
            "Hello with no level",
            new[] { (byte)ENetMessage.Hello, NetMessages.ProtocolVersion, (byte)1, (byte)'A' }
        },
        // A 7-bit-encoded string length that promises far more bytes than the datagram carries.
        {
            "Hello with a lying name length",
            new[] { (byte)ENetMessage.Hello, NetMessages.ProtocolVersion, (byte)0x7F }
        },
        { "PlayerLeft with no id", new[] { (byte)ENetMessage.PlayerLeft } },
        { "Input with no body", new[] { (byte)ENetMessage.Input } },
        { "Input truncated mid-frame", new[] { (byte)ENetMessage.Input, (byte)1, (byte)2 } },
        // HasPosition set (bit 2) but the three position floats never arrive.
        {
            "Input claiming a position it does not carry",
            new[] { (byte)ENetMessage.Input, (byte)0, (byte)0, (byte)0, (byte)0, (byte)4, (byte)0, (byte)0, (byte)0 }
        },
    };

    [Theory]
    [MemberData(nameof(HostilePayloads))]
    public void HostileDatagram_IsDropped_AndTheServerKeepsRunning(string _, byte[] payload)
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var attacker = new FakeConnection();
        transport.Connect(attacker);
        transport.Message(attacker, payload);

        server.Update(0.0); // must not throw

        Assert.Equal(0, server.PlayerCount);

        // And the server still admits a legitimate client afterwards.
        var honest = new FakeConnection();
        transport.Connect(honest);
        transport.Message(honest, NetMessages.WriteHello("player", Level));
        server.Update(0.1);

        Assert.Equal(1, server.PlayerCount);
    }

    [Fact]
    public void MalformedDatagrams_AreCounted()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var attacker = new FakeConnection();
        transport.Connect(attacker);
        transport.Message(attacker, Array.Empty<byte>());
        transport.Message(attacker, new[] { (byte)ENetMessage.Hello });

        server.Update(0.0);

        Assert.Equal(2, server.MalformedPacketsDropped);
    }

    [Fact]
    public void WellFormedTraffic_IsNotCountedAsMalformed()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var client = new FakeConnection();
        transport.Connect(client);
        transport.Message(client, NetMessages.WriteHello("player", Level));
        transport.Message(client, NetMessages.WriteInput(new InputCommand(0, 0, -1, false, false, 0, 90)));

        server.Update(0.0);

        Assert.Equal(1, server.PlayerCount);
        Assert.Equal(0, server.MalformedPacketsDropped);
    }

    // The transport layer, below the decoders: a bare channel prefix decodes to an empty payload, and
    // an empty payload has no message-type byte for any reader to index.
    [Fact]
    public void BareUnreliablePrefix_IsNotDeliveredAsAPayload()
    {
        var channel = new ReliableChannel(_ => { });

        Assert.False(channel.HandleDatagram(new[] { ReliableChannel.ChannelUnreliable }, out byte[] payload));
        Assert.Empty(payload);
    }

    [Fact]
    public void BareReliableFrame_IsAckedButNotDelivered()
    {
        var sent = new List<byte[]>();
        var channel = new ReliableChannel(sent.Add);

        bool delivered = channel.HandleDatagram(
            new[] { ReliableChannel.ChannelReliable, (byte)3, (byte)0 }, out byte[] payload);

        Assert.False(delivered);
        Assert.Empty(payload);
        // Still acked: the sender must stop retransmitting rather than retry for GiveUpAfter seconds.
        Assert.Single(sent);
        Assert.Equal(new[] { ReliableChannel.ChannelAck, (byte)3, (byte)0 }, sent[0]);
    }

    // A bare frame is acked but delivers nothing, so it must not consume its sequence number: the peer's
    // retransmission of the real frame carries the same one, and the dedup set would swallow it.
    [Fact]
    public void BareReliableFrame_DoesNotSuppressACompleteFrameWithTheSameSequence()
    {
        var channel = new ReliableChannel(_ => { });
        byte[] bare = { ReliableChannel.ChannelReliable, 9, 0 };
        byte[] complete = { ReliableChannel.ChannelReliable, 9, 0, (byte)ENetMessage.PlayerLeft, 3 };

        Assert.False(channel.HandleDatagram(bare, out _));

        Assert.True(channel.HandleDatagram(complete, out byte[] payload));
        Assert.Equal(new byte[] { (byte)ENetMessage.PlayerLeft, 3 }, payload);
    }

    [Fact]
    public void UnknownChannelPrefix_IsDropped()
    {
        var channel = new ReliableChannel(_ => { });

        Assert.False(channel.HandleDatagram(new byte[] { 0x7E, 1, 2, 3 }, out byte[] payload));
        Assert.Empty(payload);
    }

    [Fact]
    public void TypeOf_RejectsAnEmptyPayload_InsteadOfIndexingPastTheEnd()
    {
        Assert.Throws<System.IO.InvalidDataException>(() => NetMessages.TypeOf(Array.Empty<byte>()));
    }

    // The filter is the line between "this datagram is nonsense" and "this code has a bug", so both
    // sides of it are worth pinning: every shape a decoder can throw on bad bytes is absorbed, and
    // anything that can only come from a defect is not.
    public static TheoryData<Exception> DecodeFailures() => new()
    {
        new System.IO.EndOfStreamException(),           // a field the datagram stopped short of
        new System.IO.IOException(),                    // BinaryReader's bad 7-bit-encoded length
        new System.IO.InvalidDataException(),           // our own "empty payload" guard
        new ArgumentException(),                        // a name that is not valid UTF-8
        new ArgumentOutOfRangeException(),              // a length that fails a range check
        new IndexOutOfRangeException(),                 // a fixed-offset read past the end
        new OverflowException(),
        new FormatException(),
    };

    [Theory]
    [MemberData(nameof(DecodeFailures))]
    public void DecodeFailures_AreAbsorbed(Exception e) => Assert.True(MalformedPacket.IsDecodeFailure(e));

    public static TheoryData<Exception> Defects() => new()
    {
        new NullReferenceException(),
        new InvalidCastException(),
        new KeyNotFoundException(),
        new InvalidOperationException(),
    };

    [Theory]
    [MemberData(nameof(Defects))]
    public void Defects_AreNotSwallowed(Exception e) => Assert.False(MalformedPacket.IsDecodeFailure(e));

    // The client trusts its server no further: the bytes still arrive over UDP from whatever answered.
    [Fact]
    public void ClientSurvivesAMalformedServerMessage()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "player", Level);
        transport.Deliver(Array.Empty<byte>());
        transport.Deliver(new[] { (byte)ENetMessage.Welcome }); // roster header missing

        client.Update(0.0); // must not throw

        Assert.False(client.Joined);
        Assert.Equal(2, client.MalformedPacketsDropped);
    }

    // Every message the client decodes, truncated where its body begins. A joined client keeps its
    // roster and its session: a torn datagram is dropped and counted, never acted on halfway.
    public static TheoryData<string, byte[]> HostileServerPayloads() => new()
    {
        { "PlayerJoined with no listing", new[] { (byte)ENetMessage.PlayerJoined } },
        { "PlayerLeft with no id", new[] { (byte)ENetMessage.PlayerLeft } },
        { "StateUpdate with no tick", new[] { (byte)ENetMessage.StateUpdate } },
        // A snapshot count the payload cannot back up.
        {
            "StateUpdate promising states it does not carry",
            new[] { (byte)ENetMessage.StateUpdate, (byte)0, (byte)0, (byte)0, (byte)0, (byte)4 }
        },
        { "Reject with no reason", new[] { (byte)ENetMessage.Reject } },
        // ServerInfo is deliberately absent: NetClient does not decode it (the pre-join ServerQuery
        // does, and guards it there), so it reaches OnUnhandledMessage rather than a reader here.
    };

    [Theory]
    [MemberData(nameof(HostileServerPayloads))]
    public void AHostileServerDatagram_IsDropped_AndTheSessionSurvives(string _, byte[] payload)
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "player", Level);
        transport.Deliver(NetMessages.WriteWelcome(1, 0,
            new[] { new PlayerListing { PlayerId = 2, Name = "A" } }));
        transport.Deliver(payload);

        client.Update(0.0); // must not throw

        Assert.True(client.Joined);
        Assert.Single(client.Remotes); // the roster it already had is untouched
        Assert.Equal(1, client.MalformedPacketsDropped);
        Assert.Null(client.Rejection);
    }

    // Inputs are the one message a JOINED client sends, so their decode guard only runs on a session
    // the server already admitted.
    [Fact]
    public void ATruncatedInputFromAJoinedPlayer_IsDropped_AndTheSessionSurvives()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("player", Level));
        transport.Message(conn, new[] { (byte)ENetMessage.Input, (byte)1 }); // ends mid-frame
        server.Update(0.0);

        Assert.Equal(1, server.PlayerCount);
        Assert.Equal(1, server.MalformedPacketsDropped);
        Assert.Equal(Level, server.LevelName);
    }

    // The guard is scoped to decoding on purpose. Everything past it is our own code, so a fault there
    // is a defect: counting it as someone else's bad packet would bury exactly the bug worth seeing.
    [Fact]
    public void AFaultInOnPlayerAdmitted_Propagates_AndIsNotCountedAsAMalformedPacket()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        server.OnPlayerAdmitted += (_, _) => throw new ArgumentException("defect in a replicated system");
        var client = new FakeConnection();
        transport.Connect(client);
        transport.Message(client, NetMessages.WriteHello("player", Level));

        Assert.Throws<ArgumentException>(() => server.Update(0.0));
        Assert.Equal(0, server.MalformedPacketsDropped);
    }

    [Fact]
    public void AFaultInOnUnhandledMessage_Propagates_AndIsNotCountedAsAMalformedPacket()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "player", Level);
        client.OnUnhandledMessage += _ => throw new ArgumentException("defect in a subscriber");
        transport.Deliver(new[] { (byte)ENetMessage.ZombieList, (byte)0, (byte)0 });

        Assert.Throws<ArgumentException>(() => client.Update(0.0));
        Assert.Equal(0, client.MalformedPacketsDropped);
    }

    private sealed class FakeClientTransport : IClientTransport
    {
        private readonly Queue<byte[]> _inbox = new();
        public bool IsConnected => true;
        public void Deliver(byte[] payload) => _inbox.Enqueue(payload);
        public void Send(byte[] payload, ESendType sendType) { }
        public bool TryReceive(out byte[] payload) => _inbox.TryDequeue(out payload!);
        public void Update(double now) { }
        public void Close() { }
    }
}
