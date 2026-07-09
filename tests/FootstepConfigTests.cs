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
    public void FootstepKey_SprintRunsEverythingElseWalks(EPlayerStance stance, string key)
    {
        Assert.Equal(key, FootstepConfig.FootstepKey(stance));
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
