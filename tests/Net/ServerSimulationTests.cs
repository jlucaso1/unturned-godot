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

    private static InputCommand Forward(bool sprint = false, byte yaw = 0) =>
        new(0, 0, -1, jump: false, sprint: sprint, yaw: yaw, pitch: 90);

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

        for (int i = 0; i < 10; i++)
        {
            sim.QueueInput(1, new InputCommand(0, 1, 0, false, false, yaw: 0, pitch: 90));
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
}
