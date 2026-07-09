using Godot;
using UnturnedGodot.Net;
using Xunit;

namespace UnturnedGodot.Tests.Net;

public class SnapshotBufferTests
{
    private static PoseSnapshot At(float x) => new(new Vector3(x, 0, 0), 0f, 0f);

    [Fact]
    public void Empty_ReturnsLastKnownPose()
    {
        var buffer = new SnapshotBuffer();
        buffer.UpdateLastSnapshot(At(5f), now: 0.0);
        Assert.Equal(5f, buffer.GetCurrentSnapshot(1.0).Position.X);
    }

    [Fact]
    public void InterpolatesTowardTheNextSnapshot_AfterTheArrivalDelay()
    {
        var buffer = new SnapshotBuffer(readDuration: 0.08f, readDelay: 0.1f);
        buffer.UpdateLastSnapshot(At(0f), now: 0.0);
        buffer.AddNewSnapshot(At(1f), now: 0.0);

        // Before the 0.1 s delay elapses the read holds (and keeps re-arming readLast — SDK behavior).
        Assert.Equal(0f, buffer.GetCurrentSnapshot(0.05).Position.X);

        // Just after the delay, interpolation advances by (now - readLast) / duration = 0.06/0.08.
        float mid = buffer.GetCurrentSnapshot(0.11).Position.X;
        Assert.True(mid is > 0.4f and < 0.95f, $"expected mid-lerp, got {mid}");

        float done = buffer.GetCurrentSnapshot(0.30).Position.X;
        Assert.Equal(1f, done, 3);
    }

    [Fact]
    public void AnglesLerpTheShortWayAround()
    {
        var buffer = new SnapshotBuffer(readDuration: 0.08f, readDelay: 0f);
        buffer.UpdateLastSnapshot(new PoseSnapshot(Vector3.Zero, 0f, 350f), now: 0.0);
        buffer.AddNewSnapshot(new PoseSnapshot(Vector3.Zero, 0f, 10f), now: 0.0);

        float yaw = Mathf.PosMod(buffer.GetCurrentSnapshot(0.04).Position.X + // force evaluation ordering
            buffer.GetCurrentSnapshot(0.04).Yaw, 360f);
        Assert.True(yaw is < 20f or > 340f, $"yaw lerped the long way: {yaw}");
    }

    [Fact]
    public void FallingFarBehind_SnapsToTheNewest()
    {
        var buffer = new SnapshotBuffer(readDuration: 0.08f, readDelay: 0.1f);
        buffer.UpdateLastSnapshot(At(0f), now: 0.0);
        for (int i = 1; i <= 6; i++) // space > 4 triggers the catch-up branch
            buffer.AddNewSnapshot(At(i), now: 0.0);

        Assert.Equal(6f, buffer.GetCurrentSnapshot(0.01).Position.X);
    }

    [Fact]
    public void ReadAdvancesThroughTheQueue()
    {
        var buffer = new SnapshotBuffer(readDuration: 0.08f, readDelay: 0f);
        buffer.UpdateLastSnapshot(At(0f), now: 0.0);
        buffer.AddNewSnapshot(At(1f), now: 0.0);
        buffer.AddNewSnapshot(At(2f), now: 0.0);

        // Sample well past two read windows: the read pointer must have advanced to the second snapshot.
        float late = buffer.GetCurrentSnapshot(0.2).Position.X;
        Assert.True(late > 1f, $"expected progression past the first snapshot, got {late}");
    }

    [Fact]
    public void CatchUp_WhenTheWriteIndexJustWrapped()
    {
        var buffer = new SnapshotBuffer(readDuration: 0.08f, readDelay: 0.1f);
        buffer.UpdateLastSnapshot(At(0f), now: 0.0);
        for (int i = 1; i <= 16; i++) // exactly Capacity writes: writeIndex wraps to 0
            buffer.AddNewSnapshot(At(i), now: 0.0);

        Assert.Equal(16f, buffer.GetCurrentSnapshot(0.01).Position.X);
    }

    [Fact]
    public void RemotePlayer_TeleportResetsInterpolation_SmallStepsDoNot()
    {
        var remote = new RemotePlayer("R", At(0f), now: 0.0);
        remote.Push(At(1f), UnturnedGodot.Player.EPlayerStance.Stand, moving: true, grounded: false, now: 0.08);
        remote.Push(At(100f), UnturnedGodot.Player.EPlayerStance.Crouch, moving: false, grounded: true,
            now: 0.16); // 99 m skip

        Assert.Equal(UnturnedGodot.Player.EPlayerStance.Crouch, remote.Stance);
        Assert.False(remote.Moving);
        Assert.True(remote.Grounded);

        Assert.Equal(100f, remote.Sample(0.2).Position.X);
    }

    [Fact]
    public void UpdateLastSnapshot_ResetsInterpolation()
    {
        var buffer = new SnapshotBuffer(readDuration: 0.08f, readDelay: 0f);
        buffer.UpdateLastSnapshot(At(0f), now: 0.0);
        buffer.AddNewSnapshot(At(1f), now: 0.0);
        buffer.UpdateLastSnapshot(At(50f), now: 0.1); // teleport reset

        Assert.Equal(50f, buffer.GetCurrentSnapshot(0.2).Position.X);
    }
}
