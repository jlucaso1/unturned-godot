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
