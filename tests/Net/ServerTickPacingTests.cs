using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using UnturnedGodot.Net;
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
        public void Send(byte[] payload, ESendType sendType) => Sent.Add(payload);
        public void Close() { }

        public int Count(ENetMessage type) => Sent.Count(p => NetMessages.TypeOf(p) == type);
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

    private const string Level = "PEI";

    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    // A server with one joined player, its clock anchored at `now`.
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

        server.Update(1060.0); // 60 s gone: 750 ticks' worth of "missed" time

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

        server.Update(1060.0 + ServerSimulation.TickRate);

        Assert.Equal(1, player.Count(ENetMessage.StateUpdate));
    }

    // The catch-up itself stays: a few ticks of ordinary jitter are still made up, or the simulation
    // would run slow on any machine that misses a frame.
    [Fact]
    public void OrdinaryJitter_IsStillCaughtUp()
    {
        (NetServer server, FakeConnection player) = Joined(1000.0);

        server.Update(1000.0 + (ServerSimulation.TickRate * 3)); // three ticks late

        Assert.Equal(3, player.Count(ENetMessage.StateUpdate));
    }

    [Fact]
    public void SteadyPacing_TicksExactlyOncePerTickRate()
    {
        (NetServer server, FakeConnection player) = Joined(1000.0);

        double now = 1000.0;
        for (int i = 0; i < 10; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
        }

        Assert.Equal(10, player.Count(ENetMessage.StateUpdate));
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
