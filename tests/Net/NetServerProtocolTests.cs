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

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static (NetServer, FakeServerTransport) Build()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero);
        return (server, transport);
    }

    [Fact]
    public void MessageFromUnknownConnection_IsIgnored()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var ghost = new FakeConnection();
        transport.Message(ghost, NetMessages.WriteHello("Ghost")); // no Connected event first
        server.Update(0);
        Assert.Equal(0, server.PlayerCount);
        Assert.Empty(ghost.Sent);
    }

    [Fact]
    public void SecondHello_IsIgnored()
    {
        (NetServer server, FakeServerTransport transport) = Build();
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("A"));
        transport.Message(conn, NetMessages.WriteHello("A-again"));
        server.Update(0);

        Assert.Equal(1, server.PlayerCount);
        // Exactly one Welcome (the same Update's tick may add a StateUpdate broadcast).
        Assert.Equal(1, conn.Sent.Count(m => NetMessages.TypeOf(m.Payload) == ENetMessage.Welcome));
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
        transport.Message(joiner, NetMessages.WriteHello("A"));
        server.Update(0);

        (byte _, uint _, List<PlayerListing> listed) = NetMessages.ReadWelcome(joiner.Sent[0].Payload);
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
