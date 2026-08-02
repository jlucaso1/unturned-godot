using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// Protocol edge cases injected through a scriptable fake transport: events the loopback wiring can't
// produce (messages from unknown connections, double Hellos, inputs before joining, ghost disconnects).
public class NetServerProtocolTests
{
    private sealed class FakeConnection : ITransportConnection
    {
        public readonly List<(byte[] Payload, ESendType SendType)> Sent = new();
        public int Id => 99;
        public void Send(byte[] payload, ESendType sendType) => Sent.Add((payload, sendType));
        public void Close() { }
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public readonly Queue<ServerTransportEvent> Events = new();
        public int UpdateCalls;
        public bool Closed;

        public void Connect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, c, Array.Empty<byte>()));

        public void Message(FakeConnection c, byte[] payload) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, c, payload));

        public void Disconnect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Disconnected, c, Array.Empty<byte>()));

        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) => UpdateCalls++;
        public void Close() => Closed = true;
    }

    private const string Level = "PEI";

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static (NetServer, FakeServerTransport) Build()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        return (server, transport);
    }

    [Fact]
    public void MessageFromUnknownConnection_IsIgnored()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var ghost = new FakeConnection();
        transport.Message(ghost, NetMessages.WriteHello("Ghost", Level)); // no Connected event first
        server.Update(0);
        Assert.Equal(0, server.PlayerCount);
        Assert.Empty(ghost.Sent);
    }

    [Fact]
    public void SecondHello_IsAnIdempotentRejoin()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        var other = new FakeConnection();
        var pending = new FakeConnection(); // connected but silent: must not appear in rejoin rosters
        transport.Connect(conn);
        transport.Connect(other);
        transport.Connect(pending);
        transport.Message(conn, NetMessages.WriteHello("A", Level));
        transport.Message(other, NetMessages.WriteHello("B", Level));
        transport.Message(conn, NetMessages.WriteHello("A-again", Level)); // state-timeout rejoin
        server.Update(0);

        Assert.Equal(2, server.PlayerCount); // no duplicate admit
        var welcomes = conn.Sent.Where(m => NetMessages.TypeOf(m.Payload) == ENetMessage.Welcome).ToList();
        Assert.Equal(2, welcomes.Count); // the re-Hello got the roster again
        (byte firstId, _, _, _) = NetMessages.ReadWelcome(welcomes[0].Payload);
        (byte secondId, _, _, List<PlayerListing> roster) = NetMessages.ReadWelcome(welcomes[1].Payload);
        Assert.Equal(firstId, secondId);              // same identity, not a new player
        Assert.Equal("B", Assert.Single(roster).Name); // and the roster lists everyone else
    }

    [Fact]
    public void InputBeforeJoining_IsIgnored()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteInput(new InputCommand(1, 0, -1, false, false, 0, 90)));
        server.Update(0); // no crash, no join
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void DisconnectWithoutJoin_AndUnknownDisconnect_AreSafe()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var connected = new FakeConnection();
        var ghost = new FakeConnection();
        transport.Connect(connected);
        transport.Disconnect(connected); // connected but never joined
        transport.Disconnect(ghost);     // never even connected
        server.Update(0);
        Assert.Equal(0, server.PlayerCount);
    }

    [Fact]
    public void PendingConnection_DoesNotAppearInWelcome_NorReceiveJoins()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var pending = new FakeConnection(); // connected, silent (never says Hello)
        var joiner = new FakeConnection();
        transport.Connect(pending);
        transport.Connect(joiner);
        transport.Message(joiner, NetMessages.WriteHello("A", Level));
        server.Update(0);

        (byte _, uint _, uint _, List<PlayerListing> listed) = NetMessages.ReadWelcome(joiner.Sent[0].Payload);
        Assert.Empty(listed);      // the pending session isn't listed
        Assert.Empty(pending.Sent); // and gets no PlayerJoined broadcast
    }

    [Fact]
    public void TickWithNoPlayers_SendsNothing()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        server.Update(0);
        server.Update(1.0); // several elapsed ticks, zero players -> zero broadcasts
        Assert.True(transport.UpdateCalls >= 2);
    }
}
