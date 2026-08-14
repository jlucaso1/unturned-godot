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
    public void AirVelocity_NoInput_DeceleratesAtExactlyTwoMetresPerSecondSquared()
    {
        Vector3 v = PlayerMovement.AirVelocity(new Vector3(6f, 1f, 8f), Vector3.Zero, 0f, 0.25f);

        Assert.Equal(9.5f, new Vector2(v.X, v.Z).Length(), 4);
        Assert.Equal(1f + (PlayerConfig.Gravity * 0.25f), v.Y, 4);
    }

    [Fact]
    public void AirVelocity_ZeroDelta_DoesNotChangeVelocity()
    {
        var initial = new Vector3(3f, -4f, -2f);
        Assert.Equal(initial, PlayerMovement.AirVelocity(initial, Vector3.Right, 7f, 0f));
    }

    [Fact]
    public void AirVelocity_NeverAcceleratesPastWishSpeedFromRest()
    {
        Vector3 velocity = Vector3.Zero;
        Vector3 wish = new Vector3(0.6f, 0f, -0.8f);
        for (int i = 0; i < 600; i++)
        {
            velocity = PlayerMovement.AirVelocity(velocity, wish, PlayerConfig.SpeedSprint, 1f / 60f);
            Assert.True(new Vector2(velocity.X, velocity.Z).Length() <= PlayerConfig.SpeedSprint + 1e-4f);
            Assert.True(float.IsFinite(velocity.X) && float.IsFinite(velocity.Y) && float.IsFinite(velocity.Z));
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    public void JumpIntegration_ReachesApexThenFalls_AtCommonPhysicsRates(int hz)
    {
        float dt = 1f / hz;
        Vector3 velocity = Vector3.Up * PlayerConfig.JumpSpeed;
        float height = 0f;
        float apex = 0f;
        bool descended = false;
        for (int i = 0; i < hz * 2; i++)
        {
            velocity = PlayerMovement.AirVelocity(velocity, Vector3.Zero, 0f, dt);
            height += velocity.Y * dt;
            apex = System.MathF.Max(apex, height);
            descended |= velocity.Y < 0f;
            if (descended && height <= 0f)
                break;
        }

        Assert.True(descended);
        Assert.InRange(apex, 0.70f, 0.84f); // semi-implicit Euler converges upward toward analytic 0.832 m
        Assert.True(height <= 0f, $"jump did not land at {hz} Hz; height={height}");
    }

    [Fact]
    public void AirVelocity_StopsAtTerminalVelocityAcrossManyTicks()
    {
        Vector3 velocity = Vector3.Zero;
        for (int i = 0; i < 1000; i++)
        {
            velocity = PlayerMovement.AirVelocity(velocity, Vector3.Zero, 0f, 1f / 60f);
            Assert.True(velocity.Y >= PlayerConfig.TerminalVelocity);
        }
        Assert.Equal(PlayerConfig.TerminalVelocity, velocity.Y);
    }

    [Fact]
    public void JumpSpeedAndGravity_GiveTheGamesJumpHeight()
    {
        // Apex height h = v² / (2|g|) = 49 / (2*29.43) ≈ 0.83 m.
        float apex = (PlayerConfig.JumpSpeed * PlayerConfig.JumpSpeed) / (2f * -PlayerConfig.Gravity);
        Assert.Equal(0.832f, apex, 2);
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

    // "if (... Level.info.configData.Max_Walkable_Slope > -0.5f) maxWalkableSlope =
    // Level.info.configData.Max_Walkable_Slope" (PlayerMovement.cs:1187-1191, and again at 1384-1391 for
    // the floor snap). The threshold is -0.5 rather than 0: the constructor default is -1 and the test is
    // written to catch it with slop, so a map asking for a small negative limit is honoured.
    [Theory]
    [InlineData(-1f, 59f)]      // LevelInfoConfigData's own default: unset
    [InlineData(-0.5f, 59f)]    // the boundary is exclusive
    [InlineData(-1000f, 59f)]
    [InlineData(-0.25f, -0.25f)] // above the sentinel, so it is a real (if absurd) limit
    [InlineData(0f, 0f)]        // a map on which nothing at all is walkable
    [InlineData(45f, 45f)]
    [InlineData(80f, 80f)]
    public void ResolveMaxWalkableSlope_TakesTheMapsValueUnlessItIsTheSentinel(
        float configured, float expected) =>
        Assert.Equal(expected, PlayerConfig.ResolveMaxWalkableSlope(configured));

    // NaN fails the comparison, which is what the original does with it too — a config carrying one is
    // "unset" rather than a limit no floor can satisfy.
    [Fact]
    public void ResolveMaxWalkableSlope_NaNIsUnset() =>
        Assert.Equal(PlayerConfig.MaxWalkableSlopeDegrees,
            PlayerConfig.ResolveMaxWalkableSlope(float.NaN));

    // The default a map with no Config.json at all reaches, through the same door.
    [Fact]
    public void ResolveMaxWalkableSlope_DefaultConfigIsFiftyNineDegrees() =>
        Assert.Equal(59f, PlayerConfig.ResolveMaxWalkableSlope(
            UnturnedGodot.Data.LevelConfigData.Default.MaxWalkableSlope));
}
