using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The reported bug: the host started PEI and opened it to LAN, the joining player had another map
// (California 2) selected in the browser, and the join SUCCEEDED — two players walking two different
// worlds, shooting at positions that meant nothing to each other. Nothing on the wire named the level,
// so nothing could notice.
//
// The fix pins the level down at both ends, and these tests are its specification:
//  - a server answers "which level are you running?" BEFORE anyone joins, so a client can build the
//    right world instead of guessing (ServerQuery);
//  - the Hello carries the level the client actually built, and a mismatch is refused with a reason
//    the UI can show, instead of being silently admitted onto the wrong map.
public class LevelHandshakeTests
{
    private const string ServerLevel = "PEI";
    private const string OtherLevel = "California 2";

    private sealed class FakeConnection : ITransportConnection
    {
        public readonly List<(byte[] Payload, ESendType SendType)> Sent = new();
        public bool Closed;
        public int Id => 7;
        public void Send(byte[] payload, ESendType sendType) => Sent.Add((payload, sendType));
        public void Close() => Closed = true;

        public IEnumerable<byte[]> Of(ENetMessage type) =>
            Sent.Select(m => m.Payload).Where(p => NetMessages.TypeOf(p) == type);
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

    private static (NetServer Server, FakeServerTransport Transport) Build(string level = ServerLevel)
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, level);
        return (server, transport);
    }

    // The bug itself: the host runs PEI, the joiner built California 2, and the join must not happen.
    [Fact]
    public void HelloFromAClientOnAnotherLevel_IsRefused_WithTheServersLevelInTheReason()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("Joiner", OtherLevel));
        server.Update(0);

        Assert.Equal(0, server.PlayerCount);
        Assert.Empty(conn.Of(ENetMessage.Welcome));

        byte[] reject = Assert.Single(conn.Of(ENetMessage.Reject));
        JoinRejection rejection = NetMessages.ReadReject(reject);
        Assert.Equal(EJoinRejection.LevelMismatch, rejection.Reason);
        Assert.Equal(ServerLevel, rejection.ServerLevel); // so the client can say WHICH map to load
        Assert.True(conn.Closed);
    }

    // The level is a folder name typed by humans and command lines; case and stray spaces are not a
    // different map.
    [Theory]
    [InlineData("PEI")]
    [InlineData("pei")]
    [InlineData(" PEI ")]
    public void HelloOnTheSameLevel_IsAdmitted(string clientLevel)
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("Joiner", clientLevel));
        server.Update(0);

        Assert.Equal(1, server.PlayerCount);
        Assert.Single(conn.Of(ENetMessage.Welcome));
        Assert.Empty(conn.Of(ENetMessage.Reject));
        Assert.False(conn.Closed);
    }

    // The level check runs before the rejoin branch: a re-Hello is only "the same player, still here"
    // when it is still the same world.
    [Fact]
    public void RejoinHelloOnAnotherLevel_IsRefused()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("Joiner", ServerLevel));
        transport.Message(conn, NetMessages.WriteHello("Joiner", OtherLevel));
        server.Update(0);

        Assert.Single(conn.Of(ENetMessage.Welcome)); // the admit, and nothing after it
        Assert.Equal(EJoinRejection.LevelMismatch,
            NetMessages.ReadReject(Assert.Single(conn.Of(ENetMessage.Reject))).Reason);
    }

    // An empty level is not a wildcard. A client that fails to name its world is exactly the client
    // this bug was about, so it is refused like any other mismatch.
    [Fact]
    public void HelloWithNoLevel_IsRefused()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("Joiner", ""));
        server.Update(0);

        Assert.Equal(0, server.PlayerCount);
        Assert.Equal(EJoinRejection.LevelMismatch,
            NetMessages.ReadReject(Assert.Single(conn.Of(ENetMessage.Reject))).Reason);
    }

    // The pre-flight: a connection that has not joined (and never will, if it turns out to hold the
    // wrong map) can ask what the server runs. This is what makes the client able to load the RIGHT
    // world instead of being told afterwards that it loaded the wrong one.
    [Fact]
    public void ServerInfoRequest_IsAnswered_WithoutJoining()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteServerInfoRequest());
        server.Update(0);

        ServerInfo info = NetMessages.ReadServerInfo(Assert.Single(conn.Of(ENetMessage.ServerInfo)));
        Assert.Equal(ServerLevel, info.Level);
        Assert.Equal(NetMessages.ProtocolVersion, info.ProtocolVersion);
        Assert.Equal(0, info.PlayerCount);
        Assert.True(info.FreeSlots > 0);
        Assert.Equal(0, server.PlayerCount); // asking is not joining
        Assert.False(conn.Closed);
    }

    [Fact]
    public void ServerInfo_ReportsThePopulation()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var player = new FakeConnection();
        var asker = new FakeConnection();
        transport.Connect(player);
        transport.Connect(asker);
        transport.Message(player, NetMessages.WriteHello("Joiner", ServerLevel));
        transport.Message(asker, NetMessages.WriteServerInfoRequest());
        server.Update(0);

        ServerInfo info = NetMessages.ReadServerInfo(Assert.Single(asker.Of(ENetMessage.ServerInfo)));
        Assert.Equal(1, info.PlayerCount);
    }

    // A build from another version cannot be told its map is wrong in words it understands, but the
    // refusal must still carry a reason for builds that do.
    //
    // Both shapes matter, and the second is the one that bites: everything after the version byte is
    // versioned, so a Hello from the protocol BEFORE the level field simply ends early. Decoding the
    // whole message before checking the version turns that into "malformed" — dropped silently, never
    // refused, never closed — and since the transport acks it regardless, the old client would sit
    // there re-Helloing forever with no idea why nothing happens.
    [Theory]
    [InlineData(true)]  // a build that carries a level field, on another version
    [InlineData(false)] // a genuine protocol-5 Hello: version + name, and nothing else
    public void MismatchedProtocolVersion_IsRefused_WithAReason(bool carriesLevel)
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);

        using var ms = new System.IO.MemoryStream();
        using (var w = new System.IO.BinaryWriter(ms))
        {
            w.Write((byte)ENetMessage.Hello);
            w.Write((byte)(NetMessages.ProtocolVersion - 1));
            w.Write("Old");
            if (carriesLevel)
                w.Write(ServerLevel);
        }
        transport.Message(conn, ms.ToArray());
        server.Update(0);

        Assert.Equal(0, server.PlayerCount);
        JoinRejection rejection = NetMessages.ReadReject(Assert.Single(conn.Of(ENetMessage.Reject)));
        Assert.Equal(EJoinRejection.ProtocolMismatch, rejection.Reason);
        Assert.Equal(NetMessages.ProtocolVersion, rejection.ServerProtocolVersion);
        Assert.True(conn.Closed);
        Assert.Equal(0, server.MalformedPacketsDropped); // an old build is not a bad packet
    }

    // ---- the client end -------------------------------------------------------------------------

    private sealed class FakeClientTransport : IClientTransport
    {
        private readonly Queue<byte[]> _inbox = new();
        public readonly List<byte[]> Sent = new();
        public bool IsConnected { get; private set; } = true;
        public void Deliver(byte[] payload) => _inbox.Enqueue(payload);
        public void Send(byte[] payload, ESendType sendType) => Sent.Add(payload);
        public bool TryReceive(out byte[] payload) => _inbox.TryDequeue(out payload!);
        public void Update(double now) { }
        public void Close() => IsConnected = false;

        public IEnumerable<byte[]> Of(ENetMessage type) =>
            Sent.Where(p => NetMessages.TypeOf(p) == type);
    }

    [Fact]
    public void ClientHello_CarriesTheLevelItBuilt()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Joiner", OtherLevel);
        client.Update(0);

        (byte version, string name, string level) =
            NetMessages.ReadHello(Assert.Single(transport.Of(ENetMessage.Hello)));
        Assert.Equal(NetMessages.ProtocolVersion, version);
        Assert.Equal("Joiner", name);
        Assert.Equal(OtherLevel, level);
        Assert.Equal(OtherLevel, client.Level);
    }

    // Being refused is a final answer, not a hiccup: the client surfaces it and stops hammering the
    // server with a Hello it has already been told will never be accepted.
    [Fact]
    public void RejectedClient_ReportsTheReason_AndStopsRetrying()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Joiner", OtherLevel);
        JoinRejection? seen = null;
        client.OnRejected += r => seen = r;

        client.Update(0);
        Assert.Single(transport.Of(ENetMessage.Hello));

        transport.Deliver(NetMessages.WriteReject(EJoinRejection.LevelMismatch, ServerLevel));
        client.Update(0.1);

        Assert.False(client.Joined);
        Assert.NotNull(client.Rejection);
        Assert.Equal(EJoinRejection.LevelMismatch, client.Rejection!.Value.Reason);
        Assert.Equal(ServerLevel, client.Rejection!.Value.ServerLevel);
        Assert.Equal(EJoinRejection.LevelMismatch, seen!.Value.Reason);

        client.Update(100.0); // well past the Hello retry interval
        Assert.Single(transport.Of(ENetMessage.Hello));
    }

    [Fact]
    public void ClientSurvivesATruncatedReject()
    {
        var transport = new FakeClientTransport();
        var client = new NetClient(transport, "Joiner", ServerLevel);
        transport.Deliver(new[] { (byte)ENetMessage.Reject });

        client.Update(0); // must not throw

        Assert.Null(client.Rejection);
        Assert.Equal(1, client.MalformedPacketsDropped);
    }

    // ---- the pre-flight query -------------------------------------------------------------------

    [Fact]
    public void Query_ReportsTheLevelTheServerRuns()
    {
        var serverTransport = new LoopbackServerTransport();
        var server = new NetServer(serverTransport,
            new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Vector3.Zero, ServerLevel);
        LoopbackClientTransport clientTransport = serverTransport.CreateClient();
        var query = new ServerQuery(clientTransport);

        double now = 5000.0;
        for (int i = 0; i < 4 && query.State == EServerQueryState.Pending; i++)
        {
            query.Update(now);
            server.Update(now);
            now += 0.1;
        }

        Assert.Equal(EServerQueryState.Answered, query.State);
        Assert.Equal(ServerLevel, query.Info!.Level);
        Assert.Equal(0, server.PlayerCount); // the query never joins
    }

    [Fact]
    public void Query_RetriesUntilSomethingAnswers()
    {
        var transport = new FakeClientTransport();
        var query = new ServerQuery(transport);

        query.Update(0);
        query.Update(ServerQuery.RetryInterval * 1.5);
        Assert.Equal(2, transport.Of(ENetMessage.ServerInfoRequest).Count());
        Assert.Equal(EServerQueryState.Pending, query.State);

        transport.Deliver(NetMessages.WriteServerInfo(ServerLevel, playerCount: 3, freeSlots: 9));
        query.Update(ServerQuery.RetryInterval * 2.5);

        Assert.Equal(EServerQueryState.Answered, query.State);
        Assert.Equal(ServerLevel, query.Info!.Level);
        Assert.Equal(3, query.Info!.PlayerCount);
        Assert.Equal(9, query.Info!.FreeSlots);
        Assert.Equal(2, transport.Of(ENetMessage.ServerInfoRequest).Count()); // answered: stop asking
    }

    // The owner polls this from a frame loop, so it keeps being ticked after it has settled: an
    // answered query must stay answered and stop touching the transport.
    [Fact]
    public void Query_IsInertOnceAnswered()
    {
        var transport = new FakeClientTransport();
        var query = new ServerQuery(transport);
        query.Update(0);
        transport.Deliver(NetMessages.WriteServerInfo(ServerLevel, playerCount: 0, freeSlots: 254));
        query.Update(0.1);
        Assert.Equal(EServerQueryState.Answered, query.State);

        query.Update(1000.0); // long past both the retry interval and the timeout

        Assert.Equal(EServerQueryState.Answered, query.State);
        Assert.Equal(ServerLevel, query.Info!.Level);
        Assert.Single(transport.Of(ENetMessage.ServerInfoRequest));
    }

    // Nothing at that address (or a firewall eating the datagrams): the join must fail with "no
    // answer" rather than build a world and sit on a loading screen forever.
    [Fact]
    public void Query_TimesOutWhenNothingAnswers()
    {
        var transport = new FakeClientTransport();
        var query = new ServerQuery(transport);

        query.Update(1000.0);
        Assert.Equal(EServerQueryState.Pending, query.State);
        query.Update(1000.0 + ServerQuery.Timeout + 0.1);

        Assert.Equal(EServerQueryState.TimedOut, query.State);
        Assert.Null(query.Info);
    }

    // The transport gave up on its reliable frames: the peer is unreachable, so there is nothing left
    // to wait for.
    [Fact]
    public void Query_FailsWhenTheTransportDisconnects()
    {
        var transport = new FakeClientTransport();
        var query = new ServerQuery(transport);
        query.Update(0);
        transport.Close();
        query.Update(0.1);

        Assert.Equal(EServerQueryState.TimedOut, query.State);
    }

    // Whatever answered a UDP probe is not necessarily our server.
    [Fact]
    public void Query_SurvivesGarbage()
    {
        var transport = new FakeClientTransport();
        var query = new ServerQuery(transport);
        transport.Deliver(Array.Empty<byte>());
        transport.Deliver(new[] { (byte)ENetMessage.ServerInfo }); // no body
        transport.Deliver(new[] { (byte)ENetMessage.StateUpdate, (byte)0 }); // not an answer at all

        query.Update(0); // must not throw

        Assert.Equal(EServerQueryState.Pending, query.State);
        Assert.Equal(2, query.MalformedPacketsDropped);
    }
}
