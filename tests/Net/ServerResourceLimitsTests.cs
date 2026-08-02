using System;
using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// How much work the server does per Update was decided entirely by how much its peers chose to send, and
// by how far behind its own clock had fallen. These pin the three ceilings that changed that.
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

    // Step consumes one input per tick, so a client sending faster than TickRate grew this queue without
    // limit — a joined client can send Input datagrams at line rate, and nothing else bounded it.
    [Fact]
    public void AFloodedInputQueueIsBounded()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        for (uint i = 0; i < ServerSimulation.MaxQueuedInputsPerPlayer * 20; i++)
            sim.QueueInput(1, Forward(i));

        Assert.True(sim.DroppedInputs > 0);

        // The queue holds at most the cap: draining that many ticks exhausts it, and the next tick starves.
        for (int i = 0; i < ServerSimulation.MaxQueuedInputsPerPlayer; i++)
            sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState before));
        sim.Step(); // starved: the player repeats "stand still", so only gravity moves them
        Assert.True(sim.TryGetState(1, out PlayerMoveState after));
        Assert.Equal(before.Position.X, after.Position.X, 3);
        Assert.Equal(before.Position.Z, after.Position.Z, 3);
    }

    // Dropping the oldest rather than refusing the newest is the point: the freshest input is the one that
    // matters, and it bounds how stale the input the player is being simulated from can be.
    [Fact]
    public void TheNewestInputSurvivesAFlood()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        const uint last = (ServerSimulation.MaxQueuedInputsPerPlayer * 3) - 1;
        for (uint i = 0; i <= last; i++)
            sim.QueueInput(1, Forward(i));

        // Drain to the final queued input; it must be the one most recently sent.
        for (int i = 0; i < ServerSimulation.MaxQueuedInputsPerPlayer - 1; i++)
            sim.Step();
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.True(state.Position.Z < 0f, "the player should have moved forward on the surviving inputs");
    }

    [Fact]
    public void AnUnflodedQueueDropsNothing()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        for (uint i = 0; i < ServerSimulation.MaxQueuedInputsPerPlayer; i++)
        {
            sim.QueueInput(1, Forward(i));
            sim.Step();
        }

        Assert.Equal(0, sim.DroppedInputs);
    }

    // A stall leaves `now` arbitrarily far past the tick clock. Stepping all of it in one Update means that
    // many simulation steps and that many broadcasts inside one frame, which lengthens the frame, which
    // deepens the debt.
    [Fact]
    public void ALongStallDoesNotStepTheWholeGapInOneUpdate()
    {
        var transport = new FakeServerTransport();
        ServerSimulation sim = FlatSim();
        var server = new NetServer(transport, sim, Vector3.Zero, "PEI");

        server.Update(0.0);
        uint before = sim.Tick;

        server.Update(3600.0); // an hour of debt: 45,000 ticks if replayed

        Assert.True(sim.Tick - before <= NetServer.MaxCatchUpTicks,
            $"stepped {sim.Tick - before} ticks in one Update");
        Assert.True(server.SkippedTicks > 0);
    }

    [Fact]
    public void TheClockResynchronisesAfterAStall_RatherThanStayingInDebt()
    {
        var transport = new FakeServerTransport();
        ServerSimulation sim = FlatSim();
        var server = new NetServer(transport, sim, Vector3.Zero, "PEI");

        server.Update(0.0);
        server.Update(3600.0);
        uint afterStall = sim.Tick;

        // One tick's worth of time later, exactly one more step is due — not another catch-up burst.
        server.Update(3600.0 + (ServerSimulation.TickRate * 2));

        Assert.True(sim.Tick - afterStall <= 2, $"stepped {sim.Tick - afterStall} ticks after resync");
    }

    [Fact]
    public void NormalCadenceIsUnaffectedByTheCatchUpCap()
    {
        var transport = new FakeServerTransport();
        ServerSimulation sim = FlatSim();
        var server = new NetServer(transport, sim, Vector3.Zero, "PEI");

        double now = 0.0;
        server.Update(now);
        uint before = sim.Tick;
        for (int i = 0; i < 50; i++)
        {
            now += ServerSimulation.TickRate;
            server.Update(now);
        }

        Assert.Equal(50u, sim.Tick - before);
        Assert.Equal(0, server.SkippedTicks);
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
