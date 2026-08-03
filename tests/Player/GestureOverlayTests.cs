using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class GestureOverlayTests
{
    [Fact]
    public void Request_PassesStraightThroughWhenNothingIsPlaying()
    {
        var overlay = new GestureOverlay();
        Assert.False(overlay.IsPlaying);
        Assert.True(overlay.Request(EPlayerStance.Stand, moving: true));
        Assert.Null(overlay.Advance(0.1f));
    }

    [Fact]
    public void Begin_HoldsTheRigThenHandsBackTheStateItStartedFrom()
    {
        var overlay = new GestureOverlay();
        overlay.Begin(0.5f, EPlayerStance.Crouch, moving: true);
        Assert.True(overlay.IsPlaying);

        // Requests are swallowed while the gesture plays...
        Assert.False(overlay.Request(EPlayerStance.Crouch, moving: true));
        Assert.Null(overlay.Advance(0.25f));
        Assert.True(overlay.IsPlaying);

        // ...and the state comes back exactly once, on the frame the length elapses.
        (EPlayerStance Stance, bool Moving)? resume = overlay.Advance(0.25f);
        Assert.Equal((EPlayerStance.Crouch, true), resume);
        Assert.False(overlay.IsPlaying);
        Assert.Null(overlay.Advance(0.25f));
    }

    [Fact]
    public void Request_DuringAGestureDecidesWhereItReturnsTo()
    {
        var overlay = new GestureOverlay();
        overlay.Begin(0.4f, EPlayerStance.Stand, moving: false);

        // The player crouches and starts running mid-swing: they must come out of it into that, not into
        // the standing idle the swing began from.
        Assert.False(overlay.Request(EPlayerStance.Crouch, moving: true));

        Assert.Equal((EPlayerStance.Crouch, true), overlay.Advance(0.4f));
    }

    [Fact]
    public void Begin_WhileAlreadyPlayingKeepsTheOriginalFallback()
    {
        var overlay = new GestureOverlay();
        overlay.Begin(0.4f, EPlayerStance.Stand, moving: false);
        overlay.Advance(0.2f);

        // A second swing inside the first extends the overlay but must not adopt the mid-swing pose as
        // the thing to return to — the fallback is still the movement state from before any of it.
        overlay.Begin(0.4f, EPlayerStance.Prone, moving: true);
        Assert.Null(overlay.Advance(0.3f)); // the clock restarted, so 0.3 s in it is still playing
        Assert.Equal((EPlayerStance.Stand, false), overlay.Advance(0.2f));
    }

    [Fact]
    public void Cancel_DropsTheGestureWithoutRestoringAnything()
    {
        var overlay = new GestureOverlay();
        overlay.Begin(0.4f, EPlayerStance.Stand, moving: false);
        overlay.Cancel();

        Assert.False(overlay.IsPlaying);
        Assert.Null(overlay.Advance(1f));            // the caller already chose the next clip
        Assert.True(overlay.Request(EPlayerStance.Prone, moving: false));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void Begin_IgnoresAClipWithNoLength(float length)
    {
        // A clip the character carries but that holds no keyframes would otherwise own the rig forever.
        var overlay = new GestureOverlay();
        overlay.Begin(length, EPlayerStance.Stand, moving: false);
        Assert.False(overlay.IsPlaying);
        Assert.True(overlay.Request(EPlayerStance.Stand, moving: false));
    }

    [Fact]
    public void Advance_OvershootingTheLengthStillEndsExactlyOnce()
    {
        // A frame longer than the whole gesture (a hitch, a loading stall) must not skip the handover.
        var overlay = new GestureOverlay();
        overlay.Begin(0.2f, EPlayerStance.Sprint, moving: true);
        Assert.Equal((EPlayerStance.Sprint, true), overlay.Advance(5f));
        Assert.Null(overlay.Advance(5f));
    }
}
