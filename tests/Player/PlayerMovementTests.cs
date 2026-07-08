using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PlayerMovementTests
{
    [Fact]
    public void GroundVelocity_IsInstant_NoAccelerationRamp()
    {
        // Unturned's default ground material sets velocity straight to dir*speed with no ramp.
        Vector3 v = PlayerMovement.GroundVelocity(new Vector3(1, 0, 0), PlayerConfig.SpeedStand);
        Assert.Equal(new Vector3(4.5f, 0f, 0f), v);
    }

    [Fact]
    public void GroundVelocity_NormalizedInput_NoDiagonalOrBackwardBonus()
    {
        Vector3 dir = new Vector3(1, 0, 1).Normalized();
        Vector3 v = PlayerMovement.GroundVelocity(dir, PlayerConfig.SpeedSprint);
        Assert.Equal(PlayerConfig.SpeedSprint, new Vector2(v.X, v.Z).Length(), 4); // exactly 7, not 7*sqrt2
    }

    [Fact]
    public void AirVelocity_AppliesTripleGravity()
    {
        Vector3 v = PlayerMovement.AirVelocity(Vector3.Zero, Vector3.Zero, 0f, 1f);
        Assert.Equal(-9.81f * 3f, v.Y, 3); // ~-29.43 m/s after 1 s
    }

    [Fact]
    public void AirVelocity_ClampsToTerminalVelocity()
    {
        Vector3 v = PlayerMovement.AirVelocity(new Vector3(0, -99, 0), Vector3.Zero, 0f, 1f);
        Assert.Equal(-100f, v.Y);
    }

    [Fact]
    public void AirVelocity_StrafesTowardWish_ThenClampsToSpeed()
    {
        // From rest, one step accelerates horizontally toward the wish but not past the target speed.
        Vector3 step = PlayerMovement.AirVelocity(Vector3.Zero, new Vector3(1, 0, 0), 7f, 0.1f);
        Assert.True(step.X > 0f && step.X <= 7f);

        // From above the target speed with no input, it decelerates but never below the (zero) target.
        Vector3 decel = PlayerMovement.AirVelocity(new Vector3(10, 0, 0), Vector3.Zero, 0f, 0.1f);
        Assert.True(decel.X is < 10f and >= 0f);
    }

    [Fact]
    public void JumpApexHeight_MatchesJumpSpeedAndGravity()
    {
        // v² / (2|g|) = 49 / (2*29.43) ≈ 0.83 m.
        Assert.Equal(0.832f, PlayerMovement.JumpApexHeight(), 2);
    }

    [Theory]
    [InlineData(EPlayerStance.Stand, 4.5f)]
    [InlineData(EPlayerStance.Sprint, 7f)]
    [InlineData(EPlayerStance.Crouch, 2.5f)]
    [InlineData(EPlayerStance.Prone, 1.5f)]
    public void SpeedFor_MatchesUnturnedTable(EPlayerStance stance, float expected)
        => Assert.Equal(expected, PlayerConfig.SpeedFor(stance));

    [Theory]
    [InlineData(EPlayerStance.Stand, 2f, 1.75f)]
    [InlineData(EPlayerStance.Sprint, 2f, 1.75f)]
    [InlineData(EPlayerStance.Crouch, 1.2f, 1.2f)]
    [InlineData(EPlayerStance.Prone, 0.8f, 0.35f)]
    public void HeightAndEyeHeight_MatchUnturnedTable(EPlayerStance stance, float height, float eye)
    {
        Assert.Equal(height, PlayerConfig.HeightFor(stance));
        Assert.Equal(eye, PlayerConfig.EyeHeightFor(stance));
    }

    [Theory]
    [InlineData(EPlayerStance.Stand, -89f, 89f)]  // ~full look
    [InlineData(EPlayerStance.Sprint, -89f, 89f)]
    [InlineData(EPlayerStance.Crouch, -70f, 70f)] // Unturned 20..160
    [InlineData(EPlayerStance.Prone, -30f, 30f)]  // Unturned 60..120
    public void PitchLimits_NarrowWithStance(EPlayerStance stance, float down, float up)
    {
        (float d, float u) = PlayerConfig.PitchLimitsFor(stance);
        Assert.Equal(down, d);
        Assert.Equal(up, u);
    }
}
