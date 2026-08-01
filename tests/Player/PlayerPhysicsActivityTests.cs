using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class PlayerPhysicsActivityTests
{
    [Fact]
    public void ReadyGroundedIdle_IsTheOnlySkippableState()
    {
        Assert.False(PlayerPhysicsActivity.NeedsIntegration(
            worldReady: true, grounded: true, moving: false, jumping: false, stanceChanged: false));
    }

    [Theory]
    [InlineData(false, true, false, false, false)] // colliders can still join under the player
    [InlineData(true, false, false, false, false)] // gravity/landing
    [InlineData(true, true, true, false, false)]   // walking
    [InlineData(true, true, false, true, false)]   // jumping
    [InlineData(true, true, false, false, true)]   // resized capsule must settle
    [InlineData(false, false, true, true, true)]   // every bad-path condition together
    public void EveryWorldOrMotionTransition_Integrates(bool ready, bool grounded, bool moving,
        bool jumping, bool stanceChanged)
    {
        Assert.True(PlayerPhysicsActivity.NeedsIntegration(
            ready, grounded, moving, jumping, stanceChanged));
    }

    [Fact]
    public void SystemicSessionSequence_SkipsOnlyStableIdleTicks()
    {
        // Loading -> settle -> idle -> move -> jump/fall -> land -> stance change -> idle again.
        var frames = new[]
        {
            (Ready: false, Ground: true,  Move: false, Jump: false, Stance: false, Integrate: true),
            (Ready: true,  Ground: true,  Move: false, Jump: false, Stance: false, Integrate: false),
            (Ready: true,  Ground: true,  Move: true,  Jump: false, Stance: false, Integrate: true),
            (Ready: true,  Ground: true,  Move: false, Jump: true,  Stance: false, Integrate: true),
            (Ready: true,  Ground: false, Move: false, Jump: false, Stance: false, Integrate: true),
            (Ready: true,  Ground: true,  Move: false, Jump: false, Stance: true,  Integrate: true),
            (Ready: true,  Ground: true,  Move: false, Jump: false, Stance: false, Integrate: false),
        };

        foreach (var f in frames)
            Assert.Equal(f.Integrate, PlayerPhysicsActivity.NeedsIntegration(
                f.Ready, f.Ground, f.Move, f.Jump, f.Stance));
    }
}
