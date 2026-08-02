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

    // Strafing right instead of forward, so which end of the queue survives is observable: Forward moves
    // along -Z and this along +X. Frame alone is not enough — it is unused for commands without a
    // position, so a queue of Forward(i) is entirely indistinguishable from itself.
    private static InputCommand StrafeRight(uint frame) => new(frame, 1, 0, false, false, 0, 90);

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
        for (uint i = 0; i < last; i++)
            sim.QueueInput(1, Forward(i));
        sim.QueueInput(1, StrafeRight(last)); // the newest, and the only one that moves along +X

        // Drain the whole queue. The last command applied must be the newest sent, not an evicted-from-
        // the-wrong-end survivor: the surviving window is the tail, so the strafe is the final step.
        for (int i = 0; i < ServerSimulation.MaxQueuedInputsPerPlayer; i++)
            sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.True(state.Position.X > 0f,
            $"the newest input (a strafe) should have been the last applied; X = {state.Position.X}");
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

        // Counted through OnTick rather than sim.Tick: the clock deliberately jumps over skipped time
        // (so the trusted-position budget stays honest), so only the callback tracks actual steps.
        int stepped = 0;
        server.OnTick += _ => stepped++;

        server.Update(0.0);
        stepped = 0;

        server.Update(3600.0); // an hour of debt: 45,000 ticks if replayed

        Assert.True(stepped <= NetServer.MaxCatchUpTicks, $"stepped {stepped} times in one Update");
        Assert.True(server.SkippedTicks > 0);
    }

    [Fact]
    public void TheClockResynchronisesAfterAStall_RatherThanStayingInDebt()
    {
        var transport = new FakeServerTransport();
        ServerSimulation sim = FlatSim();
        var server = new NetServer(transport, sim, Vector3.Zero, "PEI");

        int stepped = 0;
        server.OnTick += _ => stepped++;

        server.Update(0.0);
        server.Update(3600.0);
        stepped = 0;

        // One tick's worth of time later, exactly one more step is due — not another catch-up burst.
        server.Update(3600.0 + (ServerSimulation.TickRate * 2));

        Assert.True(stepped <= 2, $"stepped {stepped} times after resync");
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

    // Skipping ticks must still advance the clock. ApplyTrustedPosition's speed budget is measured in
    // ticks, so dropping the stalled time would price a player's real movement through the stall against
    // the handful of ticks that did run, reject it, and rubber-band them.
    [Fact]
    public void SkippedTicksStillAdvanceTheClock()
    {
        var transport = new FakeServerTransport();
        ServerSimulation sim = FlatSim();
        var server = new NetServer(transport, sim, Vector3.Zero, "PEI");

        server.Update(0.0);
        uint before = sim.Tick;

        const double stall = 10.0;
        server.Update(stall);

        // The clock covers the whole stall, not just the ticks that were simulated.
        uint advanced = sim.Tick - before;
        Assert.True(advanced >= (uint)(stall / ServerSimulation.TickRate) - 1,
            $"clock advanced only {advanced} ticks over a {stall}s stall");
    }

    // Advancing the clock without dropping the queue would leave a backlog that never drains: Step
    // consumes one input per tick while the client produces one per tick, so every replicated position
    // would stay that many ticks stale for the rest of the session.
    [Fact]
    public void SkippingTicksDropsTheInputsThatBelongedToThem()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);
        for (uint i = 0; i < ServerSimulation.MaxQueuedInputsPerPlayer; i++)
            sim.QueueInput(1, Forward(i));

        sim.SkipTicks(100);

        // Nothing is left to replay: the very next Step starves rather than working through a backlog.
        Assert.True(sim.TryGetState(1, out PlayerMoveState before));
        sim.Step();
        Assert.True(sim.TryGetState(1, out PlayerMoveState after));
        Assert.Equal(before.Position.Z, after.Position.Z, 3);
    }

    // A stall is not a client misbehaving, so it must not show up in the counter that means one is.
    [Fact]
    public void StallClearsAreCountedApartFromFloodDrops()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);
        for (uint i = 0; i < 4; i++)
            sim.QueueInput(1, Forward(i));

        sim.SkipTicks(100);

        Assert.Equal(0, sim.DroppedInputs);
        Assert.Equal(4, sim.InputsClearedBySkip);
    }

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
