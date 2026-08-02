using Godot;
using UnturnedGodot.Net;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Net;

// A client's trusted position is the one number it gets to set directly. NaN in it was unrecoverable: the
// first position is adopted outright (there is nothing to measure it against), and every comparison
// against NaN afterwards is false, so the speed budget then rejected every real position forever.
public class NonFinitePositionTests
{
    private static bool FlatGround(float x, float z, out float y)
    {
        y = 0f;
        return true;
    }

    private static ServerSimulation FlatSim() => new(new HeightfieldMoveSolver(FlatGround));

    private static InputCommand At(uint frame, Vector3 position) =>
        new(frame, 0, 0, false, false, 0, 90, EPlayerStance.Stand, position);

    public static TheoryData<string, Vector3> NonFinite() => new()
    {
        { "NaN x", new Vector3(float.NaN, 1f, 2f) },
        { "NaN y", new Vector3(1f, float.NaN, 2f) },
        { "NaN z", new Vector3(1f, 2f, float.NaN) },
        { "all NaN", new Vector3(float.NaN, float.NaN, float.NaN) },
        { "+infinity", new Vector3(float.PositiveInfinity, 1f, 2f) },
        { "-infinity", new Vector3(1f, float.NegativeInfinity, 2f) },
    };

    [Theory]
    [MemberData(nameof(NonFinite))]
    public void ANonFinitePositionIsRefused(string _, Vector3 position)
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        sim.QueueInput(1, At(0, position));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.True(float.IsFinite(state.Position.X));
        Assert.True(float.IsFinite(state.Position.Y));
        Assert.True(float.IsFinite(state.Position.Z));
        Assert.Equal(1, sim.RejectedPositions);
    }

    // The bug in full: one NaN used to wedge the slot, because every later speed-budget comparison against
    // it was false. The player must still be movable afterwards.
    [Theory]
    [MemberData(nameof(NonFinite))]
    public void ThePlayerStillMovesAfterSendingOne(string _, Vector3 position)
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        sim.QueueInput(1, At(0, position));
        sim.Step();

        // A legitimate position a short step away must now be accepted.
        var honest = new Vector3(0.5f, 0f, 0.5f);
        sim.QueueInput(1, At(1, honest));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(honest.X, state.Position.X, 3);
        Assert.Equal(honest.Z, state.Position.Z, 3);
    }

    [Fact]
    public void AFiniteFirstPositionIsStillAdoptedOutright()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        var far = new Vector3(500f, 0f, -300f); // no previous position to measure against
        sim.QueueInput(1, At(0, far));
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(far.X, state.Position.X, 3);
        Assert.Equal(far.Z, state.Position.Z, 3);
        Assert.Equal(0, sim.RejectedPositions);
    }

    // Refusing the command must not cost the player their queued movement: the next honest one applies.
    [Fact]
    public void RefusingOneCommandDoesNotStallTheQueue()
    {
        ServerSimulation sim = FlatSim();
        sim.AddPlayer(1, Vector3.Zero);

        sim.QueueInput(1, At(0, new Vector3(float.NaN, 0f, 0f)));
        sim.QueueInput(1, At(1, new Vector3(0.4f, 0f, 0f)));
        sim.QueueInput(1, At(2, new Vector3(0.8f, 0f, 0f)));
        sim.Step();
        sim.Step();

        Assert.True(sim.TryGetState(1, out PlayerMoveState state));
        Assert.Equal(0.8f, state.Position.X, 3);
        Assert.Equal(1, sim.RejectedPositions);
    }
}
