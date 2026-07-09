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
