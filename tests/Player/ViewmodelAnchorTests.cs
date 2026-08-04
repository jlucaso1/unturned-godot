using Godot;
using UnturnedGodot.Player;
using Xunit;

namespace UnturnedGodot.Tests.Player;

public class ViewmodelAnchorTests
{
    // The whole point of the anchor: wherever the pose left the eye bone, the rig moves so that bone
    // ends up on the camera. Unturned gets this by parenting the camera under the skull; the port gets
    // it by moving the rig, and this is the invariant that makes the two the same thing.
    [Theory]
    [InlineData(0f, 1.62f, 0f)]     // standing: the skull sits near eye height
    [InlineData(0.02f, 1.05f, 0.1f)] // crouched: the clip drops it more than half a metre
    [InlineData(0.05f, 0.28f, 0.6f)] // prone: down AND well forward, the body being horizontal
    public void TheEyeBoneLandsOnTheCameraWhateverThePose(float x, float y, float z)
    {
        var eye = new Vector3(x, y, z);
        Assert.Equal(Vector3.Zero, ViewmodelAnchor.RigPosition(eye, Vector3.Zero) + eye);
    }

    // A rest-pose offset is the bug this replaces: it only agrees with the pose the character was bound
    // in, and every stance clip moves the skull away from it. The gap is the whole drop of a crouch,
    // which is exactly how much of the body climbs into frame.
    [Fact]
    public void ARestPoseOffsetDisagreesWithACrouchedPoseByTheDrop()
    {
        var rest = new Vector3(0f, 1.62f, 0f);
        var crouched = new Vector3(0f, 1.05f, 0f);

        Vector3 live = ViewmodelAnchor.RigPosition(crouched, Vector3.Zero);
        Vector3 stale = ViewmodelAnchor.RigPosition(rest, Vector3.Zero);

        // Anchored live, the crouched skull is on the camera; anchored to the rest, it is 0.57 m below it.
        Assert.Equal(0f, (live + crouched).Y, 5);
        Assert.Equal(-0.57f, (stale + crouched).Y, 5);
    }

    // UG_VIEWMODEL_OFFSET is a manual nudge on top, so it displaces the eye by exactly itself and
    // nothing else — a debugging knob, not a second source of truth for the framing.
    [Fact]
    public void TheNudgeDisplacesTheEyeByItself()
    {
        var eye = new Vector3(0.03f, 1.6f, -0.02f);
        var nudge = new Vector3(0f, -0.05f, 0.1f);
        Vector3 displaced = ViewmodelAnchor.RigPosition(eye, nudge) + eye;
        Assert.Equal(nudge.X, displaced.X, 5);
        Assert.Equal(nudge.Y, displaced.Y, 5);
        Assert.Equal(nudge.Z, displaced.Z, 5);
    }
}
