using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// The server consumes exactly ONE input per 12.5 Hz tick, and queued whatever arrived. Nothing bounded
// that queue, so any client that ever sends faster than the server ticks buys itself a backlog the
// server can never work off — and a backlog is not just memory, it is TIME: the avatar everyone else
// sees keeps replaying inputs from seconds ago, and every burst pushes it further into the past.
//
// A burst is not exotic. The client drains its own send timer once per FRAME, so a hitch (a streaming
// stall, an alt-tab, a slow first load) is followed by a flurry of frames at 60 Hz that all send. A
// hostile client simply sends as fast as it likes, and the queue grows without bound.
//
// So the queue is a jitter buffer with a ceiling, and the frames that fall off it are the STALE ones:
// nobody needs to know where a player was two seconds ago when a fresher frame is already in hand.
public class InputBacklogTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static ServerSimulation FlatSim() => new(new HeightfieldMoveSolver(FlatGround));

    private static readonly Vector3 Spawn = new(0, 10f, 0);

    private static InputCommand Walk(uint frame, byte yaw = 0) =>
        new(frame, 0, -1, jump: false, sprint: false, yaw: yaw, pitch: 90);

    private static Vector3 PositionOf(ServerSimulation sim)
    {
        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        return state.Position;
    }

    // The symptom, in the form a player reports it: someone lets go of the keys and their avatar keeps
    // walking for several seconds. That is the server working through a backlog of frames the player
    // sent long ago.
    [Fact]
    public void AFloodOfInputs_DoesNotKeepDrivingTheAvatarAfterThePlayerStopped()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        // 100 frames = 8 s of walking, handed over in one burst and never followed up.
        for (uint i = 0; i < 100; i++)
            sim.QueueInput(1, Walk(i));

        for (int i = 0; i < ServerSimulation.MaxQueuedInputs + 2; i++)
            sim.Step(); // long enough to spend a full jitter buffer

        Vector3 settled = PositionOf(sim);
        for (int i = 0; i < 30; i++)
            sim.Step(); // 2.4 s later the avatar must be standing exactly where it stopped

        Assert.Equal(settled, PositionOf(sim));
    }

    // The buffer still absorbs ordinary jitter: a couple of frames arriving together are all played,
    // which is the whole point of queueing at all. Measured against the same walk delivered one frame
    // per tick, so it asserts the queueing and not a movement constant.
    [Fact]
    public void OrdinaryJitter_IsAbsorbed_NotDropped()
    {
        const int burst = 3; // within the buffer

        ServerSimulation paced = FlatSim();
        paced.AddPlayer(1, Spawn);
        for (uint i = 0; i < burst; i++)
        {
            paced.QueueInput(1, Walk(i));
            paced.Step();
        }

        ServerSimulation bursty = FlatSim();
        bursty.AddPlayer(1, Spawn);
        for (uint i = 0; i < burst; i++)
            bursty.QueueInput(1, Walk(i));
        for (int i = 0; i < burst; i++)
            bursty.Step();

        Assert.Equal(PositionOf(paced), PositionOf(bursty));
        Assert.True(Spawn.DistanceTo(PositionOf(bursty)) > 0f); // and it really did walk
    }

    // What survives a flood is the NEWEST frames. Dropping the freshest instead would pin the avatar
    // ever further behind the player it belongs to; dropping the stalest keeps it as current as the
    // wire allows.
    [Fact]
    public void WhenTheBufferOverflows_TheFreshestFramesAreTheOnesKept()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        // Each frame carries a different view yaw, so the state says which one was played.
        for (uint i = 0; i < 40; i++)
            sim.QueueInput(1, Walk(i, yaw: (byte)i));

        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        byte oldestKept = (byte)(40 - ServerSimulation.MaxQueuedInputs);
        Assert.Equal(NetAngles.DequantizeYaw(oldestKept), state.Yaw);
    }

    // The ceiling is per player: one client flooding must not cost another client their buffer.
    [Fact]
    public void OneFloodingClient_DoesNotDisturbAnother()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.AddPlayer(2, Spawn);

        for (uint i = 0; i < 200; i++)
            sim.QueueInput(1, Walk(i));
        sim.QueueInput(2, Walk(0));
        sim.Step();

        // The quiet client's single frame played on time: exactly one tick of walking, same as it would
        // have moved with nobody else on the server.
        ServerSimulation alone = FlatSim();
        alone.AddPlayer(1, Spawn);
        alone.QueueInput(1, Walk(0));
        alone.Step();

        Assert.True(sim.TryGetState(2, out PlayerMoveState quiet));
        Assert.Equal(PositionOf(alone), quiet.Position);
    }

    // The budget that gates a trusted position is elapsed TIME, and time is the wall clock, not the tick
    // counter. The two only agree while the server is keeping up: a host that stalls five seconds runs a
    // handful of catch-up ticks and drops the rest (NetServer.MaxCatchUpTicks), so a tick-counted budget
    // says a fraction of a second passed while the player really did sprint for five. Every claim then
    // sits outside a window that grows slower than the player moves, and the avatar stands frozen for
    // ten seconds before snapping — with the frames that would have walked it there trimmed away as
    // stale, which is exactly what the jitter buffer is supposed to do with them.
    [Fact]
    public void AfterAHostStall_TheFirstClaimIsJudgedAgainstTheTimeThatReallyPassed()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        double now = 1000.0;
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90, EPlayerStance.Stand, Spawn));
        sim.Step(now); // baseline claim

        // Five seconds of wall clock gone, of which the server only got to simulate a few ticks.
        for (int i = 0; i < NetServer.MaxCatchUpTicks; i++)
            sim.Step(now += ServerSimulation.TickRate);
        now += 5.0;

        // The client sprinted through all of it and says where it ended up.
        var sprinted = new Vector3(0, 10f, -5f * PlayerConfig.SpeedSprint);
        sim.QueueInput(1, new InputCommand(100, 0, -1, false, true, 0, 90, EPlayerStance.Sprint, sprinted));
        sim.Step(now);

        Assert.Equal(sprinted, PositionOf(sim));
    }

    // The same clock cuts both ways: no amount of tick churn buys distance that no time was available
    // for, so a client cannot teleport by waiting for the server to run ticks on its behalf.
    [Fact]
    public void WithinOneTickOfWallClock_ATeleportIsStillRefused()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        double now = 1000.0;
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90, EPlayerStance.Stand, Spawn));
        sim.Step(now);

        sim.QueueInput(1, new InputCommand(1, 0, -1, false, true, 0, 90, EPlayerStance.Sprint,
            new Vector3(0, 10f, -400f)));
        sim.Step(now + ServerSimulation.TickRate);

        Assert.Equal(Spawn, PositionOf(sim));
    }

    // Trusted-position frames are bounded the same way, and the speed budget still governs how fast the
    // authoritative position may converge on the survivor — a flood cannot buy a teleport by burying the
    // frames in between.
    [Fact]
    public void AFloodOfTrustedPositions_CannotOutrunTheSpeedBudget()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90, EPlayerStance.Stand, Spawn));
        sim.Step(); // baseline claim

        // 200 frames marching 1 m each: legitimate only if 200 ticks of time had passed, and they did not.
        for (uint i = 1; i <= 200; i++)
        {
            sim.QueueInput(1, new InputCommand(i, 0, -1, false, true, 0, 90, EPlayerStance.Sprint,
                new Vector3(0, 10f, -i)));
        }

        sim.Step();

        float budget = PlayerConfig.SpeedSprint * ServerSimulation.TickRate * 1.5f;
        Assert.True(Spawn.DistanceTo(PositionOf(sim)) <= budget + 0.001f,
            $"moved {Spawn.DistanceTo(PositionOf(sim))} m on one tick, budget {budget} m");
    }
}
