using System.Collections.Generic;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class GestureOverlayTests
{
    // A four-bone chain: 0 -> 1 -> 2, with 3 hanging off 0 as the other arm.
    private static readonly int[] Chain = { -1, 0, 1, 0 };

    [Fact]
    public void NothingIsPlayingUntilAGestureBegins()
    {
        var overlay = new GestureOverlay();
        Assert.False(overlay.IsPlaying);
        Assert.Equal("", overlay.Clip);
        Assert.False(overlay.Advance(0.1f));
    }

    [Fact]
    public void BeginRunsTheClipOnceAndThenEnds()
    {
        var overlay = new GestureOverlay();
        Assert.True(overlay.Begin("Punch_Left", 0.5f, mask: null));
        Assert.True(overlay.IsPlaying);
        Assert.Equal("Punch_Left", overlay.Clip);

        Assert.False(overlay.Advance(0.25f)); // still swinging
        Assert.Equal(0.25f, overlay.Time, 3);
        Assert.True(overlay.IsPlaying);

        // Ends exactly once, on the frame the length elapses.
        Assert.True(overlay.Advance(0.25f));
        Assert.False(overlay.IsPlaying);
        Assert.False(overlay.Advance(0.25f));
    }

    // The movement layer is not this overlay's business any more: it runs underneath the whole time, so
    // a gesture that ends restores nothing. What it DOES carry is the mask, for as long as it plays.
    [Fact]
    public void AGestureCarriesItsMaskUntilItEnds()
    {
        var overlay = new GestureOverlay();
        overlay.Begin("Punch_Left", 0.2f, BoneMask.Subtrees(Chain, new[] { 1 }));

        BoneMask mask = overlay.Mask!;
        Assert.True(mask.Contains(1));
        Assert.True(mask.Contains(2)); // recursive: the hand under the shoulder
        Assert.False(mask.Contains(3));

        overlay.Advance(0.2f);
        Assert.Null(overlay.Mask); // and the bones go back to the layer underneath
    }

    // A mask-less gesture is Unturned's `mixAnimation(name)` with no mixing transforms: layer 1, but
    // restricted to nothing, so it covers the whole rig.
    [Fact]
    public void AGestureWithNoMaskCoversEveryBone()
    {
        var overlay = new GestureOverlay();
        overlay.Begin("Gesture_Rest", 1f, mask: null);

        Assert.True(overlay.IsPlaying);
        Assert.Null(overlay.Mask);
    }

    // A second punch inside the first swings again from the top — CharacterAnimator.play stops the
    // gesture clip it was last given before playing the new one.
    [Fact]
    public void BeginWhileAlreadyPlayingRestartsFromTheTop()
    {
        var overlay = new GestureOverlay();
        overlay.Begin("Punch_Left", 0.4f, mask: null);
        overlay.Advance(0.3f);

        Assert.True(overlay.Begin("Punch_Right", 0.4f, mask: null));
        Assert.Equal("Punch_Right", overlay.Clip);
        Assert.Equal(0f, overlay.Time);
        Assert.False(overlay.Advance(0.3f)); // the clock restarted, so 0.3 s in it is still swinging
    }

    [Fact]
    public void CancelDropsTheGestureImmediately()
    {
        var overlay = new GestureOverlay();
        overlay.Begin("Punch_Left", 0.4f, mask: null);
        overlay.Cancel();

        Assert.False(overlay.IsPlaying);
        Assert.Equal("", overlay.Clip);
        Assert.False(overlay.Advance(1f)); // and cancelling is not "ending": nothing is reported
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void AClipWithNoLengthIsRefused(float length)
    {
        // A clip the character carries but that holds no keyframes would otherwise be an overlay that
        // never ends, permanently masking an arm off the movement layer.
        var overlay = new GestureOverlay();
        Assert.False(overlay.Begin("Punch_Left", length, mask: null));
        Assert.False(overlay.IsPlaying);
    }

    [Fact]
    public void AnUnnamedClipIsRefused()
    {
        var overlay = new GestureOverlay();
        Assert.False(overlay.Begin("", 1f, mask: null));
        Assert.False(overlay.IsPlaying);
    }

    // Seeking is the off-line form of advancing, and answers on the same rule: inside the clip it just
    // moves the playhead, at or past the end the gesture is over.
    [Fact]
    public void SeekToMovesThePlayheadAndEndsPastTheClip()
    {
        var overlay = new GestureOverlay();
        overlay.Begin("Punch_Left", 0.6f, mask: null);

        Assert.False(overlay.SeekTo(0.3f));
        Assert.Equal(0.3f, overlay.Time, 3);
        Assert.True(overlay.IsPlaying);

        Assert.False(overlay.SeekTo(0.1f)); // backwards is fine too
        Assert.Equal(0.1f, overlay.Time, 3);

        Assert.True(overlay.SeekTo(0.6f));
        Assert.False(overlay.IsPlaying);
        Assert.False(overlay.SeekTo(0.2f)); // and there is nothing left to seek within
    }

    [Fact]
    public void OvershootingTheLengthStillEndsExactlyOnce()
    {
        // A frame longer than the whole gesture (a hitch, a loading stall) must not miss the handover.
        var overlay = new GestureOverlay();
        overlay.Begin("Punch_Left", 0.2f, mask: null);
        Assert.True(overlay.Advance(5f));
        Assert.False(overlay.Advance(5f));
    }

    // The masks the game registers, over the player's real bone hierarchy. Punch_Left may move the left
    // arm chain and nothing else — no spine, no skull, and above all no legs.
    [Fact]
    public void ThePunchMasksCoverOneArmChainEach()
    {
        // Player_Client's skeleton, in the order the renderer lists its bones.
        var parents = new List<int>
        {
            -1, // 0  Spine
            0,  // 1  Left_Shoulder
            1,  // 2  Left_Arm
            2,  // 3  Left_Hand
            3,  // 4  Left_Hook
            0,  // 5  Right_Shoulder
            5,  // 6  Right_Arm
            6,  // 7  Right_Hand
            7,  // 8  Right_Hook
            0,  // 9  Skull
            -1, // 10 Left_Hip
            10, // 11 Left_Leg
            11, // 12 Left_Foot
            -1, // 13 Right_Hip
            13, // 14 Right_Leg
            14, // 15 Right_Foot
        };

        BoneMask left = BoneMask.Subtrees(parents, new[] { 1 })!;
        for (int bone = 0; bone < parents.Count; bone++)
            Assert.Equal(bone is >= 1 and <= 4, left.Contains(bone));

        BoneMask right = BoneMask.Subtrees(parents, new[] { 5 })!;
        for (int bone = 0; bone < parents.Count; bone++)
            Assert.Equal(bone is >= 5 and <= 8, right.Contains(bone));
    }
}
