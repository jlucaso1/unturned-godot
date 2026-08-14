using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// What "authoritative server" has to mean for movement, and the three holes where it did not.
//
// The server validates a claimed position against a speed budget, and the position it keeps is the one
// that decides punch damage, zombie aggro and stealth radius. So a refused claim is not a private
// bookkeeping event — it is the moment the two machines start disagreeing about where a player is, and
// until this the server answered it by keeping the old position and sending nothing at all. The client
// never read its own authoritative state (LocalServerState was replicated, exposed, and read by nothing
// but a bot's log line), so it walked on. A genuine desync then read to the player as "my punches miss
// what is standing next to me", with nothing on screen and no counter to point at.
public class PositionAuthorityTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static ServerSimulation FlatSim() => new(new HeightfieldMoveSolver(FlatGround));

    private static readonly Vector3 Spawn = new(0, 10f, 0);

    private static InputCommand Claim(uint frame, Vector3 position,
        EPlayerStance stance = EPlayerStance.Stand, sbyte x = 0, sbyte y = 0, bool grounded = true) =>
        new(frame, x, y, jump: false, sprint: false, yaw: 0, pitch: 90, stance, position, grounded);

    // A refused claim now produces something. Before, it produced silence.
    [Fact]
    public void ARefusedClaimReportsWhereTheServerActuallyHasThePlayer()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();
        Assert.Empty(sim.Corrections);

        sim.QueueInput(1, Claim(1, new Vector3(500f, 10f, 0))); // 500 m in one tick
        sim.Step();

        PlayerPositionCorrection correction = Assert.Single(sim.Corrections);
        Assert.Equal(1, correction.PlayerId);
        Assert.Equal(Spawn, correction.Position); // where the server is holding them, not what they claimed
    }

    // And an accepted one does not: a correction on every tick would be a 17-byte message per player per
    // tick that says nothing, and would train a client to ignore it.
    [Fact]
    public void AnAcceptedClaimProducesNoCorrection()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();
        sim.QueueInput(1, Claim(1, new Vector3(0.3f, 10f, -0.2f)));
        sim.Step();

        Assert.Empty(sim.Corrections);
    }

    // The list is refilled per Step, like Gestures — a correction is about one tick's disagreement.
    [Fact]
    public void CorrectionsDoNotAccumulateAcrossTicks()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();
        sim.QueueInput(1, Claim(1, new Vector3(500f, 10f, 0)));
        sim.Step();
        Assert.Single(sim.Corrections);

        sim.Step(); // starved: nothing claimed, so nothing refused
        Assert.Empty(sim.Corrections);
    }

    // FLIGHT. `MathF.Abs(delta.Y) <= verticalBudget` applied the budget for FALLING to movement in the
    // opposite direction, so a client could rise at 1.5x terminal velocity — about 150 m/s — for as long
    // as it liked, accepted by the server and replicated to everyone watching.
    [Fact]
    public void ClimbingFasterThanAJumpIsRefused()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn, grounded: false));
        sim.Step();

        // 5 m up in one 80 ms tick is 62 m/s. Well inside the old terminal-velocity budget of 12 m.
        sim.QueueInput(1, Claim(1, new Vector3(0, 15f, 0), grounded: false));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(Spawn, state.Position);
        Assert.Single(sim.Corrections);
    }

    // And a real jump is not: the ceiling going up is the jump speed, which is what actually rises.
    [Fact]
    public void AnOrdinaryJumpStillFitsTheUpwardBudget()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();

        // One tick of JumpSpeed is 0.56 m; the slack allows 0.84.
        var jumped = new Vector3(0, 10f + (PlayerConfig.JumpSpeed * ServerSimulation.TickRate), 0);
        sim.QueueInput(1, Claim(1, jumped, grounded: false));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(jumped, state.Position);
        Assert.Empty(sim.Corrections);
    }

    // Falling keeps terminal velocity, because falling is what reaches it. The asymmetry is the point.
    [Fact]
    public void FallingKeepsTheTerminalVelocityBudgetThatClimbingLost()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 100f, 0));
        sim.QueueInput(1, Claim(0, new Vector3(0, 100f, 0), grounded: false));
        sim.Step();

        var fallen = new Vector3(0, 92f, 0); // 8 m down in one tick: 100 m/s
        sim.QueueInput(1, Claim(1, fallen, grounded: false));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(fallen, state.Position);
    }

    // STEALTH. Stance and Moving were taken from the input flags verbatim, and both decide the radius a
    // player is noticed at (ZombieDetection.RadiusFor). A client sending "Prone, no keys held" while its
    // positions walked at sprint speed was granted prone's 3 m radius, for a player crossing open ground
    // at 7 m/s. The displacement is what the server can check, and it is the thing that cannot be lied
    // about — it is the same number the speed budget was measured against.
    [Fact]
    public void AProneClaimAtSprintSpeedIsNotGivenProneStealth()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn, EPlayerStance.Prone));
        sim.Step();

        // One tick of sprint, claimed as prone with no keys held.
        var moved = new Vector3(PlayerConfig.SpeedSprint * ServerSimulation.TickRate, 10f, 0);
        sim.QueueInput(1, Claim(1, moved, EPlayerStance.Prone));
        List<PlayerSnapshotState> states = sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(moved, state.Position); // the position itself is inside the budget: this is not a cheat catch
        Assert.Equal(EPlayerStance.Sprint, state.Stance);
        Assert.True(state.Moving);
        // And what every other client is told matches, so the animation is not a standing prone player.
        Assert.Equal(EPlayerStance.Sprint, states[0].Stance);
        Assert.True(states[0].Moving);

        Assert.True(ZombieDetectionRadius(state) > 10f,
            "a player crossing open ground at sprint speed must not be as quiet as a prone one");
    }

    // A player really can be prone and still, and that must keep working: the rule is a floor on how
    // loud the displacement makes them, not a replacement for the stance.
    [Fact]
    public void AStationaryProneClaimKeepsItsStance()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn, EPlayerStance.Prone));
        sim.Step();
        sim.QueueInput(1, Claim(1, Spawn, EPlayerStance.Prone));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(EPlayerStance.Prone, state.Stance);
        Assert.False(state.Moving);
    }

    // Crawling at prone speed is consistent with the claim, so it is left alone.
    [Fact]
    public void CrawlingAtProneSpeedIsNotPromoted()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn, EPlayerStance.Prone));
        sim.Step();

        var crawled = new Vector3(PlayerConfig.SpeedProne * ServerSimulation.TickRate, 10f, 0);
        sim.QueueInput(1, Claim(1, crawled, EPlayerStance.Prone, y: -1));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(EPlayerStance.Prone, state.Stance);
        Assert.True(state.Moving);
    }

    // The flag may still say "moving" when the displacement says otherwise, and that direction is safe:
    // claiming to move while standing still only makes a player LOUDER, and it is what a player pushing
    // into a wall genuinely is doing — the original animates the walk cycle there.
    [Fact]
    public void HoldingKeysAgainstAWallStillCountsAsMoving()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();
        sim.QueueInput(1, Claim(1, Spawn, EPlayerStance.Stand, y: -1)); // keys held, no displacement
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.True(state.Moving);
    }

    // A climber moves vertically, so its horizontal speed is ~0 and the floor never rises above what was
    // claimed. Worth pinning: a stance floor derived from speed could easily have promoted a fast ladder
    // to Stand and made every climber twice as loud as the game makes them.
    [Fact]
    public void AClimberIsLeftAtItsOwnStance()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn, EPlayerStance.Climb));
        sim.Step();

        var climbed = new Vector3(0, 10f + (PlayerConfig.SpeedClimb * ServerSimulation.TickRate), 0);
        sim.QueueInput(1, Claim(1, climbed, EPlayerStance.Climb, y: -1));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(EPlayerStance.Climb, state.Stance);
    }

    // THE WIDENING BUDGET. `elapsed` is the time since the last claim the server JUDGED, not since the
    // last one it liked. It used to be the latter, so a client that kept claiming impossible positions
    // watched its own budget grow one tick at a time until a wildly wrong claim was finally ACCEPTED:
    // the divergence healed by adopting the client's answer instead of correcting it, and waiting was
    // the entire technique.
    [Fact]
    public void RepeatedRefusalsDoNotWidenTheBudgetUntilOneIsAccepted()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();

        // Twenty five ticks of claiming somewhere 100 m away. Under the old rule the twenty-fifth would
        // have been inside a budget grown to 2 s of sprint and been adopted.
        for (uint frame = 1; frame <= 25; frame++)
        {
            sim.QueueInput(1, Claim(frame, new Vector3(100f, 10f, 0)));
            sim.Step();
        }

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(Spawn, state.Position);
        Assert.Single(sim.Corrections); // and it is still saying so, every tick
    }

    // Starvation is a different thing and must still widen it: a claim the server never heard is not a
    // claim it judged, and the player really did keep moving through the seconds a stalled host could
    // not simulate. This is the existing resync behaviour, pinned so the fix above cannot eat it.
    [Fact]
    public void SilenceStillWidensTheBudget()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Spawn);
        sim.QueueInput(1, Claim(0, Spawn));
        sim.Step();

        for (int i = 0; i < 25; i++)
            sim.Step(); // 2 s of dropped packets while the client keeps sprinting

        var after = new Vector3(14f, 10f, 0); // 2 s of sprint
        sim.QueueInput(1, new InputCommand(2, 0, -1, jump: false, sprint: true, yaw: 64, pitch: 90,
            EPlayerStance.Sprint, after));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(after, state.Position);
        Assert.Empty(sim.Corrections);
    }

    // End to end: the refusal reaches the owning client as a message it can act on, and nobody else is
    // told — everyone else already learns the authoritative position from the state stream.
    [Fact]
    public void TheCorrectionReachesTheOwnerAndOnlyTheOwner()
    {
        var transport = new LoopbackServerTransport();
        var server = new NetServer(transport, FlatSim(), Spawn, "PEI");
        LoopbackClientTransport aTransport = transport.CreateClient();
        LoopbackClientTransport bTransport = transport.CreateClient();
        var a = new NetClient(aTransport, "A", "PEI");
        var b = new NetClient(bTransport, "B", "PEI");

        double now = 5000.0;
        void Pump(int rounds = 1)
        {
            for (int i = 0; i < rounds; i++)
            {
                now += ServerSimulation.TickRate;
                server.Update(now);
                a.Update(now);
                b.Update(now);
            }
        }

        Pump(4);
        Assert.True(a.Joined && b.Joined);

        var corrected = new List<Vector3>();
        a.OnPositionCorrected += corrected.Add;

        a.SendInput(Claim(1, Spawn), now);
        Pump();
        a.SendInput(Claim(2, new Vector3(500f, 10f, 0)), now);
        Pump(2);

        Assert.NotEmpty(corrected);
        Assert.Equal(Spawn, corrected[0]);
        Assert.True(a.HasCorrection);
        Assert.False(b.HasCorrection);
    }

    // Corrections are unreliable and so unordered. Snapping to an older one would put the player back
    // somewhere the server has already moved them past.
    [Fact]
    public void AnOlderCorrectionDoesNotOverrideANewerOne()
    {
        var transport = new FakeInbox();
        var client = new NetClient(transport, "Me", "PEI");
        transport.Deliver(NetMessages.WriteWelcome(1, 0, 1, System.Array.Empty<PlayerListing>()));
        transport.Deliver(NetMessages.WritePositionCorrection(20, new Vector3(5, 0, 0)));
        transport.Deliver(NetMessages.WritePositionCorrection(11, new Vector3(99, 0, 0)));
        client.Update(0);

        Assert.Equal(new Vector3(5, 0, 0), client.Correction);
        Assert.Equal(20u, client.CorrectionTick);
    }

    [Fact]
    public void TheCorrectionRoundTripsThroughItsOwnEncoding()
    {
        byte[] payload = NetMessages.WritePositionCorrection(9, new Vector3(1.5f, -2.25f, 3f));

        Assert.Equal(ENetMessage.PositionCorrection, NetMessages.TypeOf(payload));
        Assert.Equal(17, payload.Length);
        (uint tick, Vector3 position) = NetMessages.ReadPositionCorrection(payload);
        Assert.Equal(9u, tick);
        Assert.Equal(new Vector3(1.5f, -2.25f, 3f), position);
    }

    private static float ZombieDetectionRadius(in PlayerMoveState state) =>
        UnturnedGodot.Zombies.ZombieDetection.RadiusFor(state.Stance, state.Moving);

    private sealed class FakeInbox : IClientTransport
    {
        private readonly Queue<byte[]> _inbox = new();
        public bool IsConnected => true;
        public NetTraffic Traffic { get; } = new();
        public void Deliver(byte[] payload) => _inbox.Enqueue(payload);
        public void Send(byte[] payload, ESendType sendType) { }
        public bool TryReceive(out byte[] payload) => _inbox.TryDequeue(out payload!);
        public void Update(double now) { }
        public void Close() { }
    }
}
