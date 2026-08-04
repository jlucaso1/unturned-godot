using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// A punch is the port's first hand interaction, and the first thing that has to travel as an EVENT
// rather than as a value in the state stream. These cover the two halves of that: the server deciding
// the swing from the replicated input, and the swing reaching everyone but the player who threw it.
public class PunchReplicationTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static readonly Vector3 Spawn = new(0, 10f, 0);
    private const string Level = "PEI";

    private static ServerSimulation FlatSim() => new(new HeightfieldMoveSolver(FlatGround));

    private uint _frame;
    private byte _swing;

    // A frame announcing a NEW swing: a fresh number is what makes it one.
    private InputCommand Attack(EPlayerStance stance = EPlayerStance.Stand,
        EPlayerPunch fist = EPlayerPunch.Left) =>
        new(_frame++, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, stance, grounded: true,
            hasSwing: true, swingSequence: ++_swing, swingFist: fist);

    // A frame announcing the swing already announced — what a retransmission looks like on the wire.
    private InputCommand RepeatSwing(EPlayerStance stance = EPlayerStance.Stand) =>
        new(_frame++, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, stance, grounded: true,
            hasSwing: true, swingSequence: _swing, swingFist: EPlayerPunch.Left);

    private InputCommand Idle(EPlayerStance stance = EPlayerStance.Stand) =>
        new(_frame++, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, stance, grounded: true);

    [Fact]
    public void Server_TurnsASwingIntoAGesture()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Attack());

        sim.Step();

        PlayerGestureEvent gesture = Assert.Single(sim.Gestures);
        Assert.Equal(1, gesture.PlayerId);
        Assert.Equal(EPlayerGesture.PunchLeft, gesture.Gesture);
    }

    [Fact]
    public void Server_CarriesTheFistTheOwnerThrew()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Attack(fist: EPlayerPunch.Right));

        sim.Step();

        Assert.Equal(EPlayerGesture.PunchRight, Assert.Single(sim.Gestures).Gesture);
    }

    [Fact]
    public void Server_ClearsGesturesEveryTick()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Attack());
        sim.Step();
        Assert.Single(sim.Gestures);

        // The list is the tick's output, not a log: a caller that broadcasts it once must not send the
        // same swing again on the next tick.
        sim.QueueInput(1, Idle());
        sim.Step();
        Assert.Empty(sim.Gestures);
    }

    // The point of the number. Every copy of one swing carries it, so the server answers the swing once
    // however many of its frames survive the trip.
    [Fact]
    public void Server_AnswersOneSwingOnce_HoweverManyCopiesArrive()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack());
        sim.QueueInput(1, RepeatSwing());
        sim.QueueInput(1, RepeatSwing());

        int swings = 0;
        for (int i = 0; i < 4; i++)
        {
            sim.Step();
            swings += sim.Gestures.Count;
        }

        Assert.Equal(1, swings);
    }

    // And the other half of it: which copy arrives does not matter, so losing the first costs nothing.
    [Fact]
    public void Server_PlaysASwingWhoseFirstFrameWasLost()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        _ = Attack();                  // thrown, and its datagram never arrives
        sim.QueueInput(1, RepeatSwing()); // only the retransmission does

        sim.Step();

        Assert.Equal(EPlayerGesture.PunchLeft, Assert.Single(sim.Gestures).Gesture);
    }

    // A repeat outliving the frame it was thrown on is exactly why the stance is not re-judged here: the
    // swing was legal when it happened, and the client's stance is the client's word either way.
    [Fact]
    public void Server_DoesNotRejudgeARepeatAgainstALaterStance()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        _ = Attack();                                   // thrown standing; datagram lost
        sim.QueueInput(1, RepeatSwing(EPlayerStance.Prone)); // arrives after the player dropped prone

        sim.Step();

        Assert.Single(sim.Gestures);
    }

    // The cooldown is the server's, on the server's clock, and no number the client writes moves it.
    [Fact]
    public void Server_HoldsTheCooldownOnItsOwnClock()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        int swings = 0;
        // Twelve ticks of a player mashing the button: at one swing per six ticks, two land.
        for (int i = 0; i < 12; i++)
        {
            sim.QueueInput(1, Attack());
            sim.Step();
            swings += sim.Gestures.Count;
        }

        Assert.Equal(2, swings);
    }

    // The frame numbers are the client's to write, so they must buy nothing. Claiming a full cooldown
    // passed between consecutive datagrams used to pay out a swing on every one of them.
    [Fact]
    public void Server_DoesNotBelieveAFabricatedFrameRate()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        int swings = 0;
        for (uint i = 0; i < 30; i++)
        {
            uint frame = i * (PlayerEquipment.PunchCooldownTicks + 1);
            sim.QueueInput(1, new InputCommand(frame, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90,
                EPlayerStance.Stand, grounded: true, hasSwing: true, swingSequence: (byte)(i + 1)));
            sim.Step();
            swings += sim.Gestures.Count;
        }

        // Thirty ticks of real time is five cooldowns' worth of swings, whatever the frames claimed.
        Assert.InRange(swings, 4, 6);
    }

    // A swing is answered where it arrives, so the buffer that drops whole stale INPUTS cannot drop it.
    [Fact]
    public void Server_KeepsASwingTheJitterBufferHadToDrop()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack()); // this frame will fall off the stale end unplayed
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
            sim.QueueInput(1, Idle());

        int swings = 0;
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
        {
            sim.Step();
            swings += sim.Gestures.Count;
        }

        Assert.Equal(1, swings);
    }

    // Inputs are read between steps, so the simulation clock always describes the PREVIOUS tick — and
    // after a stall, one a long way back. A swing is dated by when its datagram arrived.
    [Fact]
    public void Server_DatesASwingByWhenItArrived_NotByTheLastTick()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack(), receivedAt: 0);
        sim.Step();
        Assert.Single(sim.Gestures);

        // The loop has only managed one tick, so its clock reads 0.08 s. The socket, though, read this
        // datagram a full cooldown after the first — and that is the elapsed time the rule is about.
        sim.QueueInput(1, Attack(), receivedAt: ServerSimulation.PunchCooldownSeconds + 0.01);
        sim.Step();

        Assert.Single(sim.Gestures);
    }

    // A stall makes the transport hand over a batch of datagrams that really arrived seconds apart, all
    // carrying the one timestamp of the update that drained them. The gap between two swings in that
    // batch reads as zero; the time before it is what actually passed, and what the rule counts.
    [Fact]
    public void Server_AcceptsBothSwingsOfAStalledBatch()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack(), receivedAt: 0);
        sim.Step();
        Assert.Single(sim.Gestures);

        // The server froze for a second. The player threw two more punches, comfortably apart, and both
        // come off the socket in the same drain — BEFORE any step, which is the order NetServer.Update
        // actually works in and the reason a single pending slot would lose one of them.
        sim.QueueInput(1, Attack(), receivedAt: 1.0);
        sim.QueueInput(1, Attack(), receivedAt: 1.0);

        int swings = 0;
        for (int i = 0; i < 4; i++)
        {
            sim.Step();
            swings += sim.Gestures.Count;
        }

        Assert.Equal(2, swings);
    }

    // ...but the batch is not a licence. A client that sends a hundred swings at once gets the ceiling.
    [Fact]
    public void Server_DoesNotPayOutAWholeBatchOfSwings()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        // Five seconds idle is ten cooldowns' worth of time, far past the ceiling.
        sim.QueueInput(1, Attack(), receivedAt: 0);
        sim.Step();

        int swings = 0;
        for (int i = 0; i < 100; i++)
        {
            sim.QueueInput(1, Attack(), receivedAt: 5.0); // one drain, one instant
            sim.Step();
            swings += sim.Gestures.Count;
        }

        // What idling bought is the ceiling, not the ten it would otherwise have accrued — and certainly
        // not the hundred the client asked for.
        Assert.Equal((int)ServerSimulation.MaxSwingAllowance, swings);
    }

    [Fact]
    public void Server_SeparatesPlayersCooldowns()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.AddPlayer(2, Spawn);
        sim.QueueInput(1, Attack());
        sim.QueueInput(2, Attack());

        sim.Step();

        Assert.Equal(2, sim.Gestures.Count); // one player's swing may not put another's on cooldown
    }

    // A stall drops the ticks the server could not make up, but not the seconds that passed. A player who
    // waited out a real cooldown through one must not be refused for it.
    [Fact]
    public void Server_CountsTheSecondsAStallAte()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack());
        sim.Step();
        Assert.Single(sim.Gestures);

        // One tick's worth of loop, but a full cooldown of wall clock — what a stall the server could
        // not catch up on looks like from in here. Counting ticks would say almost no time had passed.
        sim.Step(now: ServerSimulation.TickRate + ServerSimulation.PunchCooldownSeconds);

        sim.QueueInput(1, Attack());
        sim.Step();

        Assert.Single(sim.Gestures);
    }

    // Reliable delivery retransmits but does not order, so a lost early gesture can be re-sent late
    // enough to arrive behind a newer one.
    [Fact]
    public void ARetransmittedGestureCannotReplaceANewerOne()
    {
        var remote = new RemotePlayer("A", new PoseSnapshot(Spawn, 90f, 0f), 0);

        remote.PushGesture(20, EPlayerGesture.PunchLeft);
        remote.PushGesture(10, EPlayerGesture.PunchRight); // the late retransmission of an older swing
        Assert.Equal(EPlayerGesture.PunchLeft, remote.PendingGesture);

        // A genuinely newer one still lands.
        remote.PushGesture(30, EPlayerGesture.PunchRight);
        Assert.Equal(EPlayerGesture.PunchRight, remote.TakeGesture());
    }

    // Player ids are recycled, and a gesture names only an id. A swing the PREVIOUS holder of this id
    // threw, retransmitted late, must not play on whoever holds it now.
    [Fact]
    public void AGestureFromBeforeAnAvatarExistedIsNotTheirs()
    {
        // Spawned knowing the server had reached tick 100 — everything before that predates them.
        var remote = new RemotePlayer("A", new PoseSnapshot(Spawn, 90f, 0f), 0, knownAtVersion: 3,
            spawnedAtTick: 100);

        remote.PushGesture(80, EPlayerGesture.PunchLeft);
        Assert.Equal(EPlayerGesture.None, remote.PendingGesture);

        // Their own swings still land.
        remote.PushGesture(104, EPlayerGesture.PunchLeft);
        Assert.Equal(EPlayerGesture.PunchLeft, remote.PendingGesture);
    }

    // The tick floor is session state, and a restarted host counts ticks from zero. Left at the old
    // host's uptime it would silence every swing in the new session until the new host had run at least
    // as long as the old one — hours, on a server that had been up a while.
    [Fact]
    public void ARestartedServerDoesNotInheritTheOldOnesTickFloor()
    {
        var serverTransport = new LoopbackServerTransport();
        LoopbackClientTransport ct = serverTransport.CreateClient();
        Assert.True(serverTransport.TryReceive(out ServerTransportEvent connected));
        ITransportConnection conn = connected.Connection;
        var client = new NetClient(ct, "A", Level);

        var other = new PlayerListing { PlayerId = 9, Name = "B" };
        void Deliver(byte[] payload, double now)
        {
            conn.Send(payload, ESendType.Reliable);
            client.Update(now);
        }

        // A host that has been up a long while: tick 900000, and we hold B.
        double now = 1000.0;
        Deliver(NetMessages.WriteWelcome(1, 900_000, 1, new[] { other }), now);
        Assert.True(client.Joined);

        // It goes away. Past StateTimeout the client tears the session down and re-Hellos.
        now += NetClient.StateTimeout + 1.0;
        client.Update(now);
        Assert.False(client.Joined);

        // It comes back counting from the beginning, and B is with it.
        Deliver(NetMessages.WriteWelcome(1, 3, 1, new[] { other }), now);
        Assert.True(client.Joined);

        // B swings on the new server's fifth tick. Judged against the old server's clock this is
        // ancient history and never plays.
        Deliver(NetMessages.WritePlayerGesture(9, 5, EPlayerGesture.PunchLeft), now);
        Assert.Equal(EPlayerGesture.PunchLeft, client.Remotes[9].PendingGesture);
    }

    [Fact]
    public void OtherPlayersSeeTheSwing_ButNotTheOneWhoThrewIt()
    {
        var serverTransport = new LoopbackServerTransport();
        var server = new NetServer(serverTransport, FlatSim(), Spawn, Level);
        LoopbackClientTransport ta = serverTransport.CreateClient();
        LoopbackClientTransport tb = serverTransport.CreateClient();
        var a = new NetClient(ta, "A", Level);
        var b = new NetClient(tb, "B", Level);
        var clients = new List<NetClient> { a, b };

        double now = 5000.0;
        void Pump(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                now += ServerSimulation.TickRate;
                server.Update(now);
                foreach (NetClient client in clients)
                    client.Update(now);
            }
        }

        Pump(4);
        Assert.True(a.Joined);
        Assert.True(b.Joined);

        a.SendInput(Attack());
        Pump(3);

        // B sees A punch...
        Assert.Equal(EPlayerGesture.PunchLeft, b.Remotes[a.PlayerId].PendingGesture);
        // ...and A is told nothing, because A's own client already played the swing when the button
        // went down. Replaying it a round trip later would restart the animation mid-swing.
        Assert.Equal(EPlayerGesture.None, a.Remotes[b.PlayerId].PendingGesture);

        // Taken once: a renderer that consumed the gesture does not play it again next frame.
        Assert.Equal(EPlayerGesture.PunchLeft, b.Remotes[a.PlayerId].TakeGesture());
        Assert.Equal(EPlayerGesture.None, b.Remotes[a.PlayerId].PendingGesture);
    }
}
