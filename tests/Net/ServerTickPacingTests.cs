using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The tick loop caught up on missed time without a ceiling: every 0.08 s the server was late by became
// a full simulation step, all of them inside one Update, each broadcasting a state update to every
// client. A host stall is ordinary — the world streamer finishing, a navmesh reconcile, a laptop lid —
// and after one the server would replay the whole gap at once: hundreds of datagrams per client in a
// single frame, zombies jumping a minute of pathing, and the frame that has to send it all making the
// stall worse.
//
// Catching up is right for the jitter it was written for; catching up forever is not. So the loop
// spends a bounded budget of ticks and re-anchors its clock to now — the missed time is gone, which is
// exactly what it is.
public class ServerTickPacingTests
{
    private sealed class FakeConnection : ITransportConnection
    {
        public readonly List<byte[]> Sent = new();
        public int Id => 1;
        public NetTraffic Traffic { get; } = new();
        public void Send(byte[] payload, ESendType sendType) => Sent.Add(payload);
        public void Close() { }

        public int Count(ENetMessage type) => Sent.Count(p => NetMessages.TypeOf(p) == type);

        // The token this connection was admitted under, read back off its own Welcome. An Input that
        // does not carry it is refused before it is decoded, which is the point of it.
        public uint SessionToken() =>
            NetMessages.ReadWelcome(Sent.First(p => NetMessages.TypeOf(p) == ENetMessage.Welcome))
                .SessionToken;
    }

    private sealed class FakeServerTransport : IServerTransport
    {
        public readonly Queue<ServerTransportEvent> Events = new();

        public void Connect(FakeConnection c) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Connected, c, Array.Empty<byte>()));

        public void Message(FakeConnection c, byte[] payload) =>
            Events.Enqueue(new ServerTransportEvent(ETransportEvent.Message, c, payload));

        public NetTraffic Traffic { get; } = new();
        public System.Func<byte[], byte[]?>? AnswerConnectionless { get; set; }
        public bool TryReceive(out ServerTransportEvent evt) => Events.TryDequeue(out evt);
        public void Update(double now) { }
        public void Close() { }
    }

    private const string Level = "PEI";

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    // A server with one joined player, its clock anchored at `now`.
    //
    // These tests count TICKS rather than state-update datagrams. They used to count datagrams, which
    // was the same number back when every tick broadcast every player unconditionally. It is not any
    // more: the snapshot stream skips players whose quantized state is byte-identical to what their
    // region was last told, so a motionless player produces one datagram and then silence — which is
    // the point of the filter, and would make these read as "the loop stopped ticking".
    private static (NetServer Server, FakeConnection Player) Joined(double now)
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        var conn = new FakeConnection();
        transport.Connect(conn);
        transport.Message(conn, NetMessages.WriteHello("A", Level));
        server.Update(now);
        conn.Sent.Clear(); // drop the Welcome; from here we count broadcasts
        return (server, conn);
    }

    [Fact]
    public void ALongStall_DoesNotReplayEveryMissedTickAtOnce()
    {
        (NetServer server, FakeConnection player) = Joined(1000.0);

        uint before = server.Tick;
        server.Update(1060.0); // 60 s gone: 750 ticks' worth of "missed" time

        Assert.True(server.Tick - before <= NetServer.MaxCatchUpTicks,
            $"ran {server.Tick - before} ticks in one Update");
        Assert.True(player.Count(ENetMessage.StateUpdate) <= NetServer.MaxCatchUpTicks,
            $"broadcast {player.Count(ENetMessage.StateUpdate)} state updates in one Update");
    }

    // And the clock comes back to the present. Left behind, the loop would burn its whole budget again
    // on every subsequent frame, so a single stall would keep the server flooding forever.
    [Fact]
    public void AfterAStall_TheClockReanchors_AndTicksResumeAtTheNormalRate()
    {
        (NetServer server, FakeConnection player) = Joined(1000.0);
        server.Update(1060.0);
        player.Sent.Clear();
        uint before = server.Tick;

        server.Update(1060.0 + ServerSimulation.TickRate);

        Assert.Equal(1u, server.Tick - before);
    }

    // The catch-up itself stays: a few ticks of ordinary jitter are still made up, or the simulation
    // would run slow on any machine that misses a frame.
    [Fact]
    public void OrdinaryJitter_IsStillCaughtUp()
    {
        (NetServer server, FakeConnection player) = Joined(1000.0);
        uint before = server.Tick;

        server.Update(1000.0 + (ServerSimulation.TickRate * 3)); // three ticks late

        Assert.Equal(3u, server.Tick - before);
    }

    [Fact]
    public void SteadyPacing_TicksExactlyOncePerTickRate()
    {
        (NetServer server, FakeConnection player) = Joined(1000.0);
        uint before = server.Tick;

        double now = 1000.0;
        for (int i = 0; i < 10; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
        }

        Assert.Equal(10u, server.Tick - before);
    }

    // Catch-up steps are separate instants, not the same one repeated. The trusted-position budget is
    // "how far could you have moved since your last accepted claim", so handing every step of a slow
    // frame the same clock reading pays each of them a fresh minimum tick of movement on top of the one
    // real gap: four claims inside a 0.4 s frame could bank 0.4 s + three more ticks of sprint, well past
    // the 1.5x limit. On a server that is persistently late that is a standing speed bonus.
    [Fact]
    public void ASlowFrame_DoesNotHandOutMoreMovementThanTheTimeItCovered()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        var conn = new FakeConnection();
        transport.Connect(conn);

        const double start = 1000.0;
        // Admitted first, because an Input has to carry the session token the Welcome mints — the
        // server checks it before decoding anything else about the frame.
        transport.Message(conn, NetMessages.WriteHello("A", Level));
        server.Update(start - ServerSimulation.TickRate);
        uint token = conn.SessionToken();
        transport.Message(conn, NetMessages.WriteInput(new InputCommand(0, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, Vector3.Zero), token));
        server.Update(start); // baseline claim accepted

        // Four claims — the whole jitter buffer. The first spends nearly the entire stall allowance;
        // each of the rest asks for another tick's worth on top, which is only affordable if every step
        // of this frame is treated as a fresh instant.
        float tickBudget = PlayerConfig.SpeedSprint * ServerSimulation.TickRate * 1.5f;
        for (uint frame = 1; frame <= NetServer.MaxCatchUpTicks - 1; frame++)
        {
            float metres = tickBudget * (3.9f + (0.95f * (frame - 1)));
            transport.Message(conn, NetMessages.WriteInput(new InputCommand(frame, 0, -1, false, true, 0, 90,
                EPlayerStance.Sprint, new Vector3(0, 0, -metres)), token));
        }

        double elapsed = ServerSimulation.TickRate * (NetServer.MaxCatchUpTicks - 1);
        server.Update(start + elapsed); // one late frame covering all of it

        (_, List<PlayerSnapshotState> states) = NetMessages.ReadStateUpdate(
            conn.Sent.Last(p => NetMessages.TypeOf(p) == ENetMessage.StateUpdate));
        float travelled = states[0].Position.Length();
        float allowed = (float)(PlayerConfig.SpeedSprint * elapsed * 1.5);
        Assert.True(travelled <= allowed + 0.001f, $"moved {travelled} m in {elapsed} s, budget {allowed} m");
    }

    // The time a stall ate has to be dropped BEFORE the surviving claims are judged, not after. The
    // steps that resume a five-second stall used to carry the instants the stall began at — a third of
    // a second of budget between them — so the claims still in the buffer, which describe where the
    // player got to during those five seconds, were all refused. The avatar then sat at its pre-stall
    // position until the next frame arrived to be judged against a clock that had finally caught up.
    [Fact]
    public void AfterALongStall_TheClaimsStillInTheBufferAreJudgedAgainstTheStall()
    {
        var transport = new FakeServerTransport();
        var server = new NetServer(transport, new ServerSimulation(new HeightfieldMoveSolver(FlatGround)),
            Vector3.Zero, Level);
        var conn = new FakeConnection();
        transport.Connect(conn);

        const double start = 1000.0;
        transport.Message(conn, NetMessages.WriteHello("A", Level));
        server.Update(start - ServerSimulation.TickRate); // admitted, so the session token exists
        uint token = conn.SessionToken();
        transport.Message(conn, NetMessages.WriteInput(new InputCommand(0, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, Vector3.Zero), token));
        server.Update(start);

        // Five seconds of host stall. The client sprinted straight through it; what reaches the server
        // is the newest handful of its frames, the last of them five seconds' worth of ground away.
        const double stall = 5.0;
        for (uint frame = 1; frame <= ServerSimulation.MaxQueuedInputs; frame++)
        {
            float seconds = (float)stall - ((ServerSimulation.MaxQueuedInputs - frame) * ServerSimulation.TickRate);
            transport.Message(conn, NetMessages.WriteInput(new InputCommand(frame, 0, -1, false, true, 0, 90,
                EPlayerStance.Sprint, new Vector3(0, 0, -PlayerConfig.SpeedSprint * seconds)), token));
        }

        server.Update(start + stall);

        (_, List<PlayerSnapshotState> states) = NetMessages.ReadStateUpdate(
            conn.Sent.Last(p => NetMessages.TypeOf(p) == ENetMessage.StateUpdate));
        Assert.Equal(-PlayerConfig.SpeedSprint * (float)stall, states[0].Position.Z, 2);
    }

    // The simulation must not fast-forward through the gap either: a zombie that pathed for a minute of
    // simulated time in one frame arrives somewhere no client ever saw it walk to.
    [Fact]
    public void ALongStall_DoesNotAdvanceTheSimulationThroughTheWholeGap()
    {
        var transport = new FakeServerTransport();
        var simulation = new ServerSimulation(new HeightfieldMoveSolver(FlatGround));
        var server = new NetServer(transport, simulation, Vector3.Zero, Level);
        var ticks = new List<uint>();
        server.OnTick += ticks.Add;

        server.Update(1000.0);
        server.Update(1060.0);

        Assert.True(ticks.Count <= NetServer.MaxCatchUpTicks + 1, $"{ticks.Count} ticks in two Updates");
        Assert.Equal(simulation.Tick, ticks[^1]); // and the seam left the tick counter consistent
    }
}
