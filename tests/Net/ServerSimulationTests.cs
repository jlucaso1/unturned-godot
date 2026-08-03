using System.Collections.Generic;
using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

public class ServerSimulationTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 10f;
        return true;
    }

    private static ServerSimulation FlatSim() => new(new HeightfieldMoveSolver(FlatGround));

    // Frames are numbered, because the server drops an input older than one it already has: a real
    // client counts every 0.08 s frame it sends, and reusing one number reads as a stale datagram.
    // xUnit builds a fresh instance per test, so the counter is per test.
    private uint _frame;

    private InputCommand Forward(bool sprint = false, byte yaw = 0) =>
        new(_frame++, 0, -1, jump: false, sprint: sprint, yaw: yaw, pitch: 90);

    [Fact]
    public void SpawnedPlayer_FallsAndLandsOnTheGround()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 20, 0));

        for (int i = 0; i < 50; i++)
            sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(10f, state.Position.Y, 3);
        Assert.True(state.Grounded);
    }

    [Fact]
    public void WalkingForward_MovesAtWalkSpeedAlongFacing()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.Step(); // settle grounded on the flat plane

        const int ticks = 25; // 2 s
        for (int i = 0; i < ticks; i++)
        {
            sim.QueueInput(1, Forward());
            sim.Step();
        }

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        // Facing yaw 0 => forward is -Z; distance = speed * time.
        float expected = PlayerConfig.SpeedStand * ticks * ServerSimulation.TickRate;
        Assert.Equal(-expected, state.Position.Z, 1);
        Assert.Equal(0f, state.Position.X, 1);
    }

    [Fact]
    public void Sprinting_IsFaster_AndYawRotatesTheDirection()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.Step();

        byte yaw90 = NetAngles.QuantizeYaw(90f); // facing 90°: forward becomes -X
        for (int i = 0; i < 25; i++)
        {
            sim.QueueInput(1, Forward(sprint: true, yaw: yaw90));
            sim.Step();
        }

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        float expected = PlayerConfig.SpeedSprint * 25 * ServerSimulation.TickRate;
        Assert.Equal(-expected, state.Position.X, 0);
        Assert.Equal(0f, state.Position.Z, 0);
    }

    [Fact]
    public void StrafingRight_MovesAlongPlusX()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.Step();

        for (uint i = 0; i < 10; i++)
        {
            sim.QueueInput(1, new InputCommand(i, 1, 0, false, false, yaw: 0, pitch: 90));
            sim.Step();
        }

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.True(state.Position.X > 3f, $"strafe moved X={state.Position.X}");
        Assert.Equal(0f, state.Position.Z, 1);
    }

    [Fact]
    public void Jump_ReachesTheJumpApexAndLandsBack()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.Step();

        sim.QueueInput(1, new InputCommand(0, 0, 0, jump: true, sprint: false, yaw: 0, pitch: 90));
        float peak = 10f;
        for (int i = 0; i < 30; i++)
        {
            sim.Step();
            sim.TryGetState(1, out PlayerMoveState s);
            peak = Mathf.Max(peak, s.Position.Y);
        }

        // Continuous apex = jump² / (2g) ≈ 0.83 m; explicit-Euler at the coarse 0.08 s tick lands ~1.1 m.
        Assert.True(peak - 10f > 0.6f && peak - 10f < 1.3f, $"apex {peak - 10f}");
        Assert.True(sim.TryGetState(1, out PlayerMoveState landed));
        Assert.Equal(10f, landed.Position.Y, 3);
        Assert.True(landed.Grounded);
    }

    [Fact]
    public void StarvedPlayer_HoldsStillButKeepsFalling()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 30f, 0));

        for (int i = 0; i < 40; i++)
            sim.Step(); // no inputs at all

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(10f, state.Position.Y, 3); // gravity still ran
        Assert.Equal(0f, state.Position.X, 3);  // but no drift
        Assert.Equal(0f, state.Position.Z, 3);
    }

    // A starved tick means "we did not hear from this player", not "this player stood up". The filler
    // input the server invents carries no stance, so a prone or crouched player snapped upright on every
    // late or lost frame — at 12.5 Hz over UDP that is a visible flicker for everyone watching them, and
    // it is the stance the whole world sees (hitbox, animation, the pose an ambush depends on).
    [Theory]
    [InlineData(EPlayerStance.Prone)]
    [InlineData(EPlayerStance.Crouch)]
    [InlineData(EPlayerStance.Sprint)]
    public void AStarvedTick_KeepsTheStanceThePlayerWasLastIn(EPlayerStance stance)
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(0, 0, 0, jump: false, sprint: false, yaw: 0, pitch: 90, stance));
        sim.Step();

        List<PlayerSnapshotState> starved = sim.Step(); // nothing arrived this tick

        Assert.Equal(stance, Assert.Single(starved).Stance);
        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(stance, state.Stance);
    }

    // default(EPlayerStance) is 0 and the enum starts at Sprint = 2, so an uninitialised stance is not
    // a stance at all — it reaches zombie detection (which sizes its radius by stance) and every
    // client's animator. It went unnoticed while the filler input defaulted to Stand and overwrote it
    // on the very first tick.
    [Fact]
    public void AJustAdmittedPlayer_IsStanding_BeforeSayingAnything()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));

        Assert.True(sim.TryGetState(1, out PlayerMoveState fresh));
        Assert.Equal(EPlayerStance.Stand, fresh.Stance);
        Assert.Equal(EPlayerStance.Stand, Assert.Single(sim.Step()).Stance);
    }

    // The same holds for a client the server trusts positions from: its stance came in with the last
    // frame that arrived, and silence does not revoke it.
    [Fact]
    public void AStarvedTick_AfterATrustedPositionFrame_KeepsTheStance()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90, EPlayerStance.Prone,
            new Vector3(0, 10f, 0)));
        sim.Step();

        Assert.Equal(EPlayerStance.Prone, Assert.Single(sim.Step()).Stance);
    }

    [Fact]
    public void OffTheMap_IsFreeFall()
    {
        static bool NoGround(float x, float z, out float y)
        {
            y = 0f;
            return false;
        }
        var sim = new ServerSimulation(new HeightfieldMoveSolver(NoGround));
        sim.AddPlayer(1, Vector3.Zero);
        for (int i = 0; i < 10; i++)
            sim.Step();
        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.True(state.Position.Y < -5f);
        Assert.False(state.Grounded);
    }

    [Fact]
    public void TrustedPosition_FirstClaimIsTheBaseline_EvenFarFromSpawn()
    {
        // The listen-server bug: the host walked and fell BEFORE opening to LAN, so the server spawned
        // them ~26 m away from where they really are. The first client claim must become the baseline —
        // there is no earlier claim to rate-limit against — or the host stays frozen at spawn forever.
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(300f, 60f, 84f)); // server-invented spawn, high in the air
        sim.Step();

        var real = new Vector3(287f, 34f, 61f); // where the host actually is by now
        sim.QueueInput(1, new InputCommand(0, 0, -1, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Crouch, real));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(real, state.Position);
        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Crouch, state.Stance);
        Assert.True(state.Moving); // input (0,-1) held: replicated for remote walk animation
    }

    [Fact]
    public void JumpingInPlace_KeepsMovingFalse()
    {
        // The remote-animation glitch: a player jumping without directional keys must replicate
        // Moving=false the whole flight, so their avatar holds Idle instead of flickering into walk.
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Stand, new Vector3(0, 10f, 0))); // baseline
        sim.Step();

        for (int i = 1; i <= 12; i++) // a full in-place jump arc, trusted positions rising and falling
        {
            float y = 10f + Mathf.Max(0f, 1f - Mathf.Abs(i - 6) / 6f);
            bool airborne = i is > 1 and < 11;
            sim.QueueInput(1, new InputCommand((uint)i, 0, 0, jump: i == 1, sprint: false, 0, 90,
                UnturnedGodot.Player.EPlayerStance.Stand, new Vector3(0, y, 0), grounded: !airborne));
            List<PlayerSnapshotState> states = sim.Step();
            Assert.False(states[0].Moving, $"tick {i} flickered Moving on");
            Assert.Equal(!airborne, states[0].Grounded); // the owner's real IsOnFloor passes through
        }
    }

    [Fact]
    public void TrustedPosition_ConsecutiveClaims_AdoptWithinTheSpeedBudget()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Stand, new Vector3(0, 10f, 0))); // baseline at spawn
        sim.Step();

        var step = new Vector3(0.3f, 10f, -0.2f); // a normal strafing step, client-resolved
        sim.QueueInput(1, new InputCommand(1, 1, 0, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Stand, step));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(step, state.Position);
        Assert.True(state.Moving); // strafe-only input still counts as moving
    }

    [Fact]
    public void TrustedPosition_TeleportBeyondTheBudget_IsHeldBack()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Stand, new Vector3(0, 10f, 0))); // baseline
        sim.Step();

        sim.QueueInput(1, new InputCommand(1, 0, -1, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Stand, new Vector3(500f, 10f, 0))); // 500 m in one tick
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(new Vector3(0, 10f, 0), state.Position); // rubber-banded to the last verified claim
    }

    [Fact]
    public void TrustedPosition_HorizontalBudgetDoesNotBorrowTerminalFallSpeed()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(10, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, 0)));
        sim.Step();

        // One sprint tick is 0.56 m (0.84 m including the 1.5x jitter allowance). The former combined
        // budget incorrectly accepted this 2 m horizontal claim because terminal fall speed is 100 m/s.
        sim.QueueInput(1, new InputCommand(11, 0, -1, false, true, 0, 90,
            EPlayerStance.Sprint, new Vector3(2f, 10f, 0)));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(Vector3.Zero, state.Position - new Vector3(0, 10f, 0));
    }

    [Fact]
    public void TrustedPosition_FastVerticalFallRemainsValid()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 30f, 0));
        sim.QueueInput(1, new InputCommand(20, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 30f, 0), grounded: false));
        sim.Step();

        var falling = new Vector3(0.2f, 22f, -0.1f); // 100 m/s vertical, ordinary horizontal drift
        sim.QueueInput(1, new InputCommand(21, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, falling, grounded: false));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(falling, state.Position);
        Assert.False(state.Grounded);
    }

    [Fact]
    public void TrustedPosition_DuplicateAndReorderedFramesCannotRewindPlayer()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(100, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, -0.3f)));
        sim.Step();
        sim.QueueInput(1, new InputCommand(101, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, -0.6f)));
        sim.Step();

        sim.QueueInput(1, new InputCommand(101, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, -0.4f))); // duplicate
        sim.QueueInput(1, new InputCommand(99, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, -0.2f))); // reordered
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(new Vector3(0, 10f, -0.6f), state.Position);
    }

    [Fact]
    public void TrustedPosition_FrameSequenceHandlesUintWraparound()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(uint.MaxValue, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, -0.2f)));
        sim.Step();
        sim.QueueInput(1, new InputCommand(0, 0, -1, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(0, 10f, -0.4f)));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(new Vector3(0, 10f, -0.4f), state.Position);
    }

    [Fact]
    public void TrustedPosition_BudgetScalesWithStarvation_NoPermanentDivergence()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0, 10f, 0));
        sim.QueueInput(1, new InputCommand(0, 0, 0, false, false, 0, 90,
            UnturnedGodot.Player.EPlayerStance.Stand, new Vector3(0, 10f, 0))); // baseline
        sim.Step();

        for (int i = 0; i < 25; i++)
            sim.Step(); // 2 s of dropped packets while the client keeps sprinting

        var after = new Vector3(14f, 10f, 0); // 2 s of sprint: over one tick's budget, within 2 s' worth
        sim.QueueInput(1, new InputCommand(2, 0, -1, false, true, 64, 90,
            UnturnedGodot.Player.EPlayerStance.Sprint, after));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(after, state.Position); // the widened window resynced instead of rejecting forever
    }

    [Fact]
    public void RemovePlayer_AndUnknownIds_AreSafe()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);
        sim.QueueInput(2, Forward()); // unknown id: ignored
        sim.RemovePlayer(1);
        Assert.False(sim.TryGetState(1, out _));
        Assert.Empty(sim.Step());
    }

    // Standing a player somewhere outright, for loading a bug-repro dump into a running session. The
    // trusted-position baseline has to go with it: measured against where they used to be, the jump
    // reads as a cheating client and the speed budget rubber-bands them straight back.
    [Fact]
    public void Teleport_MovesThePlayerAndAdoptsTheirNextClaim()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0f, 10f, 0f));
        sim.QueueInput(1, new InputCommand(_frame++, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(1f, 10f, 0f)));
        sim.Step();
        Assert.Equal(new Vector3(1f, 10f, 0f), sim.GetState(1).Position);

        var somewhereElse = new Vector3(-616.22f, 35.02f, -66.58f);
        Assert.True(sim.Teleport(1, somewhereElse));
        Assert.Equal(somewhereElse, sim.GetState(1).Position);
        Assert.Equal(Vector3.Zero, sim.GetState(1).Velocity);

        // The client, now standing there too, is believed rather than rejected as a 600 m sprint.
        Vector3 clientSide = somewhereElse + new Vector3(0.2f, 0f, 0f);
        sim.QueueInput(1, new InputCommand(_frame++, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, clientSide));
        sim.Step();
        Assert.Equal(clientSide, sim.GetState(1).Position);
        Assert.Equal(0, sim.RejectedPositions);
    }

    // A datagram that was already in flight when the dump was loaded still says where the player used
    // to be. With the baseline cleared, adopting it outright would put them straight back and undo
    // the teleport within one tick.
    [Fact]
    public void Teleport_DropsInputsThatPredateIt()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, new Vector3(0f, 10f, 0f));
        sim.QueueInput(1, new InputCommand(_frame++, 0, 0, false, false, 0, 90,
            EPlayerStance.Stand, new Vector3(1f, 10f, 0f)));

        var somewhereElse = new Vector3(-616.22f, 35.02f, -66.58f);
        Assert.True(sim.Teleport(1, somewhereElse));
        sim.Step(); // the queued claim is gone; nothing drags the player back
        Vector3 after = sim.GetState(1).Position;
        Assert.Equal(somewhereElse.X, after.X, 3);
        Assert.Equal(somewhereElse.Z, after.Z, 3);
        Assert.True(after.Y > 34f, $"the player did not stay where they were put: {after}");
    }

    [Fact]
    public void Teleport_RefusesUnknownPlayersAndNonFinitePositions()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);
        Assert.False(sim.Teleport(9, Vector3.One));
        Assert.False(sim.Teleport(1, new Vector3(float.NaN, 0f, 0f)));
        Assert.Equal(Vector3.Zero, sim.GetState(1).Position);
    }
}
