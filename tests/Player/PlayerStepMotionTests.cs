using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PlayerStepMotionTests
{
    [Fact]
    public void NoWall_DoesNotAttemptStep()
        => Assert.False(PlayerStepMotion.TryRemaining(false, Vector3.Zero, Vector3.Zero,
            Vector3.Forward, out _));

    [Fact]
    public void ZeroRequestedMotion_DoesNotAttemptStep()
        => Assert.False(PlayerStepMotion.TryRemaining(true, Vector3.Zero, Vector3.Zero,
            Vector3.Zero, out _));

    [Theory]
    [InlineData(0.00f)]
    [InlineData(0.25f)]
    [InlineData(0.79f)]
    public void PartialDelivery_CompletesExactlyTheRequestedDisplacement(float delivered)
    {
        var before = new Vector3(10f, 3f, -7f);
        var wanted = new Vector3(1f, 0f, -2f);
        Vector3 after = before + (wanted * delivered) + (Vector3.Up * 0.12f);

        Assert.True(PlayerStepMotion.TryRemaining(true, before, after, wanted, out Vector3 remaining));
        Vector3 completed = after + remaining;
        Assert.Equal(before.X + wanted.X, completed.X, 5);
        Assert.Equal(before.Z + wanted.Z, completed.Z, 5);
        Assert.Equal(0f, remaining.Y);
    }

    [Theory]
    [InlineData(0.80f)]
    [InlineData(0.95f)]
    [InlineData(1.00f)]
    public void DeliveredEnough_DoesNotTurnWallSlideIntoStep(float delivered)
    {
        Vector3 wanted = new(2f, 0f, 0f);
        Assert.False(PlayerStepMotion.TryRemaining(true, Vector3.Zero, wanted * delivered,
            wanted, out _));
    }

    [Fact]
    public void SidewaysSlide_IsRemovedFromRemainingMotion()
    {
        Vector3 before = new(4f, 2f, 5f);
        Vector3 after = before + new Vector3(0.1f, -0.2f, 0.3f);
        Vector3 wanted = new(1f, 0f, 0f);

        Assert.True(PlayerStepMotion.TryRemaining(true, before, after, wanted, out Vector3 remaining));
        Assert.True(remaining.IsEqualApprox(new Vector3(0.9f, 0f, -0.3f)), $"remaining={remaining}");
    }

    [Fact]
    public void VerticalTravel_DoesNotCountAsHorizontalDelivery()
    {
        Vector3 wanted = new(0.5f, 0f, 0f);
        Assert.True(PlayerStepMotion.TryRemaining(true, Vector3.Zero, new Vector3(0f, 20f, 0f),
            wanted, out Vector3 remaining));
        Assert.Equal(wanted, remaining);
    }
}
