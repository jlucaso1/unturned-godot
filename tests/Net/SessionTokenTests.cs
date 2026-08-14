using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// Two ways in that needed no secret at all.
//
// A connection is keyed by (address, port) and nothing else, so anyone who guessed a client's ephemeral
// port could inject Input frames AS that player — walk them around inside the speed budget, spend their
// punch allowance — with nothing to forge. And the connection table itself was exhaustible before any
// handshake: a Connection plus its ReliableChannel was allocated on the first datagram from any
// endpoint, held for the 15 s silence timeout, capped at 256, with nothing to authenticate against.
public class SessionTokenTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private const string Level = "PEI";

    private sealed class FakeConnection : ITransportConnection
    {
        private static int NextId;
        public readonly List<byte[]> Sent = new();
        public int Id { get; } = ++NextId;
        public NetTraffic Traffic { get; } = new();
        public void Send(byte[] payload, ESendType sendType) => Sent.Add(payload);
        public void Close() { }

        public uint SessionToken() =>
            NetMessages.ReadWelcome(Sent.First(p => NetMessages.TypeOf(p) == ENetMessage.Welcome))
                .SessionToken;
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public readonly Queue<ServerTransportEvent> Events = new();
        public NetTraffic Traffic { get; } = new();
        public Func<byte[], byte[]?>? AnswerConnectionless { get; set; }

        public void Connect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, c, Array.Empty<byte>()));

        public void Message(FakeConnection c, byte[] payload) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, c, payload));

        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) { }
        public void Close() { }
    }

    private static (FakeServerTransport Transport, NetServer Server, FakeConnection Conn) Joined()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("A", Level));
        server.Update(1000.0);
        return (transport, server, conn);
    }

    private static InputCommand Claim(uint frame, Vector3 position) =>
        new(frame, 0, 0, false, false, 0, 90, EPlayerStance.Stand, position);

    [Fact]
    public void AdmissionMintsANonZeroToken()
    {
        (_, _, FakeConnection conn) = Joined();

        // Never zero, so "this session has no token yet" stays distinguishable from a token that
        // happens to be zero — and a frame written by a client that never received a Welcome cannot
        // pass by default.
        Assert.NotEqual(0u, conn.SessionToken());
    }

    [Fact]
    public void AnInputCarryingTheTokenIsAccepted()
    {
        (FakeServerTransport transport, NetServer server, FakeConnection conn) = Joined();

        var moved = new Vector3(0.2f, 0, 0);
        transport.Message(conn, NetMessages.WriteInput(Claim(1, moved), conn.SessionToken()));
        server.Update(1000.0 + ServerSimulation.TickRate);

        Assert.True(server.TryGetPlayerState(1, out PlayerMoveState state));
        Assert.Equal(moved, state.Position);
        Assert.Equal(0, server.UnauthenticatedInputsDropped);
    }

    // The whole point: same endpoint, wrong secret, nothing happens.
    [Fact]
    public void AnInputWithTheWrongTokenMovesNobody()
    {
        (FakeServerTransport transport, NetServer server, FakeConnection conn) = Joined();

        transport.Message(conn, NetMessages.WriteInput(Claim(1, new Vector3(0.2f, 0, 0)),
            conn.SessionToken() ^ 0xDEADu));
        server.Update(1000.0 + ServerSimulation.TickRate);

        Assert.True(server.TryGetPlayerState(1, out PlayerMoveState state));
        Assert.Equal(Vector3.Zero, state.Position);
        Assert.Equal(1, server.UnauthenticatedInputsDropped);
        // And it is NOT counted as malformed: the frame decodes perfectly, it simply is not ours. The
        // two counters mean different things and a session where only one of them moves says so.
        Assert.Equal(0, server.MalformedPacketsDropped);
    }

    // Zero is what a client that never received a Welcome writes, so it must not pass.
    [Fact]
    public void AnInputWithNoTokenIsRefused()
    {
        (FakeServerTransport transport, NetServer server, FakeConnection conn) = Joined();

        transport.Message(conn, NetMessages.WriteInput(Claim(1, new Vector3(0.2f, 0, 0))));
        server.Update(1000.0 + ServerSimulation.TickRate);

        Assert.True(server.TryGetPlayerState(1, out PlayerMoveState state));
        Assert.Equal(Vector3.Zero, state.Position);
        Assert.Equal(1, server.UnauthenticatedInputsDropped);
    }

    // A frame too short to hold a token is malformed rather than unauthenticated: there is nothing to
    // compare, and the reader must not index past the array to find that out.
    [Fact]
    public void AFrameTooShortToHoldATokenIsMalformed()
    {
        (FakeServerTransport transport, NetServer server, FakeConnection conn) = Joined();

        transport.Message(conn, new byte[] { (byte)ENetMessage.Input, 1, 2 });
        server.Update(1000.0 + ServerSimulation.TickRate);

        Assert.Equal(1, server.MalformedPacketsDropped);
        Assert.Equal(0, server.UnauthenticatedInputsDropped);
        Assert.Throws<System.IO.InvalidDataException>(() =>
            NetMessages.ReadInputSessionToken(new byte[] { (byte)ENetMessage.Input }));
    }

    // Two sessions get two tokens, so one player's cannot be replayed as another's.
    [Fact]
    public void EverySessionGetsItsOwnToken()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        var a = new FakeConnection();
        var b = new FakeConnection();
        transport.Connect(a);
        transport.Connect(b);
        transport.Message(a, NetMessages.WriteHello("A", Level));
        transport.Message(b, NetMessages.WriteHello("B", Level));
        server.Update(1000.0);

        Assert.NotEqual(a.SessionToken(), b.SessionToken());
    }

    [Fact]
    public void TheTokenRoundTripsThroughTheInputEncoding()
    {
        var input = new InputCommand(7, 1, -1, jump: true, sprint: true, yaw: 40, pitch: 90,
            EPlayerStance.Crouch, new Vector3(1.5f, 2.5f, -3.5f), grounded: false,
            hasSwing: true, swingSequence: 9, swingFist: EPlayerPunch.Right);

        byte[] payload = NetMessages.WriteInput(input, 0xC0FFEE);

        Assert.Equal(0xC0FFEEu, NetMessages.ReadInputSessionToken(payload));
        // And every other field still comes back untouched: the token was inserted, not overlaid.
        InputCommand read = NetMessages.ReadInput(payload);
        Assert.Equal(7u, read.Frame);
        Assert.Equal(1, read.InputX);
        Assert.Equal(-1, read.InputY);
        Assert.True(read.Jump);
        Assert.True(read.Sprint);
        Assert.False(read.Grounded);
        Assert.Equal(40, read.Yaw);
        Assert.Equal(90, read.Pitch);
        Assert.Equal(EPlayerStance.Crouch, read.Stance);
        Assert.True(read.HasPosition);
        Assert.Equal(new Vector3(1.5f, 2.5f, -3.5f), read.Position);
        Assert.True(read.HasSwing);
        Assert.Equal(9, read.SwingSequence);
        Assert.Equal(EPlayerPunch.Right, read.SwingFist);
    }

    // A real client learns its token from the Welcome and uses it without being told, which is the only
    // way any of this is usable.
    [Fact]
    public void ARealClientPicksUpItsTokenAndKeepsMoving()
    {
        var transport = new LoopbackServerTransport();
        var server = new NetServer(transport,
            new ServerSimulation(new HeightfieldMoveSolver(FlatGround)), Vector3.Zero, Level);
        var client = new NetClient(transport.CreateClient(), "Ana", Level);

        double now = 5000.0;
        for (int i = 0; i < 8; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
            client.Update(now);
            if (client.Joined)
                client.SendInput(Claim((uint)i + 1, new Vector3(i * 0.2f, 0, 0)), now);
        }

        Assert.True(client.Joined);
        Assert.Equal(0, server.UnauthenticatedInputsDropped);
        Assert.True(server.TryGetPlayerState(client.PlayerId, out PlayerMoveState state));
        Assert.True(state.Position.X > 0, "the client's own claims have to be accepted");
    }

    // The pre-handshake question, answered without a connection existing.
    [Fact]
    public void AServerInfoRequestIsAnsweredWithoutAConnection()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        server.Update(1000.0);

        // The transport hands the payload to whoever the server registered, and sends back what it gets.
        Assert.NotNull(transport.AnswerConnectionless);
        byte[]? reply = transport.AnswerConnectionless!(NetMessages.WriteServerInfoRequest());

        Assert.NotNull(reply);
        ServerInfo info = NetMessages.ReadServerInfo(reply!);
        Assert.Equal(Level, info.Level);
        Assert.Equal(NetMessages.ProtocolVersion, info.ProtocolVersion);
    }

    // And nothing else is. A widened connectionless path would be a second, unauthenticated way into the
    // server; this one exists because the question it answers is the bulk of what strangers legitimately
    // send and needs no state at all.
    [Theory]
    [InlineData(ENetMessage.Hello)]
    [InlineData(ENetMessage.Input)]
    [InlineData(ENetMessage.StateUpdate)]
    public void NothingElseIsAnsweredConnectionlessly(ENetMessage type)
    {
        var transport = new FakeServerTransport();
        _ = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);

        Assert.Null(transport.AnswerConnectionless!(new[] { (byte)type }));
        Assert.Null(transport.AnswerConnectionless!(Array.Empty<byte>()));
    }

    // The query is sent unreliably, which is what makes the connectionless answer possible: a reliable
    // frame wants an ack, and acking is per-connection state by definition — the very thing being
    // avoided. Nothing is lost, because ServerQuery already re-asks every second until it is answered.
    [Fact]
    public void TheQueryIsSentUnreliably()
    {
        var transport = new RecordingClientTransport();
        var query = new ServerQuery(transport);

        query.Update(0.0);

        Assert.Equal(ESendType.Unreliable, Assert.Single(transport.Sent).SendType);
        Assert.Equal(ENetMessage.ServerInfoRequest,
            NetMessages.TypeOf(transport.Sent[0].Payload));
    }

    private sealed class RecordingClientTransport : IClientTransport
    {
        public readonly List<(byte[] Payload, ESendType SendType)> Sent = new();
        public bool IsConnected => true;
        public NetTraffic Traffic { get; } = new();
        public void Send(byte[] payload, ESendType sendType) => Sent.Add((payload, sendType));
        public bool TryReceive(out byte[] payload)
        {
            payload = Array.Empty<byte>();
            return false;
        }

        public void Update(double now) { }
        public void Close() { }
    }
}
