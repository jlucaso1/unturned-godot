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

    private InputCommand Attack(EPlayerStance stance = EPlayerStance.Stand) =>
        new(_frame++, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, stance, grounded: true,
            attackPrimary: EAttackInputFlags.Start);

    private InputCommand Idle(EPlayerStance stance = EPlayerStance.Stand) =>
        new(_frame++, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, stance, grounded: true);

    private static InputCommand AttackAt(uint frame) =>
        new(frame, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, EPlayerStance.Stand,
            grounded: true, attackPrimary: EAttackInputFlags.Start);

    [Fact]
    public void Server_TurnsAnAttackInputIntoAGesture()
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

    [Fact]
    public void Server_HoldsTheCooldownAcrossTicks()
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

    [Fact]
    public void Server_RefusesToPunchProne()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Attack(EPlayerStance.Prone));
        sim.Step();
        Assert.Empty(sim.Gestures);

        // And the refusal costs nothing: standing up swings on the very next tick.
        sim.QueueInput(1, Attack());
        sim.Step();
        Assert.Single(sim.Gestures);
    }

    // The stance gate reads the stance this tick resolved to, not the one the player was in before it.
    [Fact]
    public void Server_JudgesTheStanceTheTickEndedIn()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Idle()); // stands
        sim.Step();

        sim.QueueInput(1, Attack(EPlayerStance.Prone)); // goes prone and clicks on the same frame
        sim.Step();
        Assert.Empty(sim.Gestures);
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

    // The cooldown is measured in the client's frames, not in server ticks, and the two do not advance
    // together: a tick with nothing to dequeue still ticks. Counting server ticks would make the rule
    // depend on how many datagrams survived the trip.
    [Fact]
    public void Server_MeasuresTheCooldownInTheClientsFrames()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        // Two clicks six of the player's frames apart — outside the cooldown, so both swing on the
        // thrower's screen. Every idle frame between them was lost in flight, so what reaches the server
        // is two attack commands back to back, and it plays them on CONSECUTIVE ticks.
        sim.QueueInput(1, AttackAt(0));
        sim.QueueInput(1, AttackAt(6));

        sim.Step();
        Assert.Single(sim.Gestures);
        sim.Step();

        // Counting the two server ticks would refuse this one, and the punch the thrower already
        // animated would reach nobody. Counting the six client frames agrees with them.
        Assert.Single(sim.Gestures);
    }

    // The other direction: server ticks piling up must not buy a swing the client's own clock refuses.
    [Fact]
    public void Server_StarvedTicksDoNotAgeTheCooldown()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, AttackAt(10));
        sim.Step();
        Assert.Single(sim.Gestures);

        for (int i = 0; i < 20; i++)
            sim.Step();

        sim.QueueInput(1, AttackAt(11)); // one client frame later, however long the server waited
        sim.Step();
        Assert.Empty(sim.Gestures);
    }

    // The jitter buffer drops whole stale inputs when a client bursts past its ceiling. Movement fields
    // survive that because a later frame restates them; an attack EDGE does not, and the discarded frame
    // was the only carrier of that swing.
    [Fact]
    public void Server_KeepsAnAttackEdgeTheJitterBufferHadToDrop()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack()); // this is the one that will fall off the stale end
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
            sim.QueueInput(1, Idle());

        // The punch frame is gone from the queue, but its edge is not lost.
        int swings = 0;
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
        {
            sim.Step();
            swings += sim.Gestures.Count;
        }

        Assert.Equal(1, swings);
    }

    [Fact]
    public void Server_ARescuedEdgeStillSwingsOnlyOnce()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Attack());
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
            sim.QueueInput(1, Idle());

        int swings = 0;
        for (int i = 0; i < 12; i++)
        {
            sim.Step();
            swings += sim.Gestures.Count;
        }

        // Carried forward, not carried forever: the edge is consumed by the first tick that plays it.
        Assert.Equal(1, swings);
    }

    // A rescued edge is judged in the frame it arrived on, not in the one it happens to be announced
    // beside. Attaching the bare edge to a later frame would let that frame's stance answer the press.
    [Fact]
    public void Server_JudgesARescuedEdgeInItsOwnFramesStance()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack()); // thrown standing, and about to fall off the stale end
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
            sim.QueueInput(1, Idle(EPlayerStance.Prone)); // and the player drops prone right after

        int swings = 0;
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
        {
            sim.Step();
            swings += sim.Gestures.Count;
        }

        // The swing was legal when it was thrown, so it still is. Going prone afterwards does not
        // retract a punch already in flight.
        Assert.Equal(1, swings);
    }

    [Fact]
    public void Server_ARescuedEdgeThrownProneStaysRefused()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        sim.QueueInput(1, Attack(EPlayerStance.Prone)); // refused where it was thrown
        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
            sim.QueueInput(1, Idle()); // and standing up afterwards must not resurrect it

        for (int i = 0; i < ServerSimulation.MaxQueuedInputs; i++)
        {
            sim.Step();
            Assert.Empty(sim.Gestures);
        }
    }

    // A tick that resolves both an owed swing and one of its own announces ONE, and it is the newer.
    // Two events under the same tick number would look like a retransmission of each other on arrival.
    [Fact]
    public void Server_AnnouncesOneSwingPerTick_AndItIsTheNewest()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);

        // A left at frame 0 falls off the stale end and is owed; a right at frame 6 — legal, a full
        // cooldown later — is the oldest frame left, so this one tick resolves both.
        sim.QueueInput(1, AttackAt(0));
        sim.QueueInput(1, new InputCommand(6, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90,
            EPlayerStance.Stand, grounded: true, attackSecondary: EAttackInputFlags.Start));
        for (uint f = 7; f <= 9; f++)
            sim.QueueInput(1, new InputCommand(f, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90,
                EPlayerStance.Stand, grounded: true));

        sim.Step();

        PlayerGestureEvent only = Assert.Single(sim.Gestures);
        Assert.Equal(EPlayerGesture.PunchRight, only.Gesture);
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
