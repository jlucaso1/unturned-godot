using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests;

public class FootstepConfigTests
{
    [Fact]
    public void Interval_Is2Point1OverSpeed()
    {
        Assert.Equal(2.1f / 4.5f, FootstepConfig.Interval(4.5f), 5); // walk
        Assert.Equal(2.1f / 7f, FootstepConfig.Interval(7f), 5);     // sprint
        Assert.Equal(float.PositiveInfinity, FootstepConfig.Interval(0f));
    }

    [Theory]
    [InlineData(EPlayerStance.Sprint, "FootstepRun")]
    [InlineData(EPlayerStance.Stand, "FootstepWalk")]
    [InlineData(EPlayerStance.Crouch, "FootstepWalk")]
    [InlineData(EPlayerStance.Prone, "FootstepWalk")]
    [InlineData(EPlayerStance.Climb, "FootstepWalk")]
    public void FootstepKey_SprintRunsEverythingElseWalks(EPlayerStance stance, string key)
    {
        Assert.Equal(key, FootstepConfig.FootstepKey(stance));
    }

    // checkGround hardcodes the surface a climber is heard on, and the terrain under a ladder has no say:
    // "2021-11-16: why does climbing use tile? Sigh."
    [Fact]
    public void ClimbingIsHeardOnTile()
    {
        Assert.Equal("Tile", FootstepConfig.MaterialOverrideFor(EPlayerStance.Climb));
        Assert.Null(FootstepConfig.MaterialOverrideFor(EPlayerStance.Stand));
        Assert.Null(FootstepConfig.MaterialOverrideFor(EPlayerStance.Crouch));
    }

    // A climber is never prone, but the cadence still comes off the stance: 2.1 / SPEED_CLIMB.
    [Fact]
    public void ClimbingIsNotSilent()
    {
        Assert.False(FootstepConfig.IsSilentStance(EPlayerStance.Climb));
        Assert.Equal(2.1f / PlayerConfig.SpeedClimb,
            FootstepConfig.Interval(PlayerConfig.SpeedFor(EPlayerStance.Climb)), 5);
        // Only crouching is quieter, so a climber's rungs are as loud as a walker's footsteps.
        Assert.Equal(0.125f, FootstepConfig.VolumeFor(EPlayerStance.Climb, landing: false));
        Assert.Equal(0.15f, FootstepConfig.VolumeFor(EPlayerStance.Climb, landing: true));
    }

    [Fact]
    public void Volume_MatchesPlayAudioClipRules()
    {
        Assert.Equal(0.125f, FootstepConfig.VolumeFor(EPlayerStance.Stand, landing: false));
        Assert.Equal(0.0625f, FootstepConfig.VolumeFor(EPlayerStance.Crouch, landing: false));
        Assert.Equal(0.15f, FootstepConfig.VolumeFor(EPlayerStance.Sprint, landing: true));
        Assert.Equal(0.075f, FootstepConfig.VolumeFor(EPlayerStance.Crouch, landing: true), 5);
    }

    [Fact]
    public void ProneIsSilent()
    {
        Assert.True(FootstepConfig.IsSilentStance(EPlayerStance.Prone));
        Assert.False(FootstepConfig.IsSilentStance(EPlayerStance.Crouch));
    }
}
