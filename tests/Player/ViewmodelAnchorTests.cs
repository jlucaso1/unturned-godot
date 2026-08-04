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

    // The bug this whole anchor turned on. The head BONE is a joint at the neck; the prefab hangs the
    // viewpoint an authored distance further along the head (Skull/Spot, which the game itself pairs with
    // its first-person camera). Anchoring the joint puts the camera most of half a metre too low — inside
    // the neck, with the head above the view and the shoulders at exactly eye level.
    [Fact]
    public void TheEyeIsTheAuthoredOffset_NotTheJoint()
    {
        var neck = new Transform3D(Basis.Identity, new Vector3(0f, 1.3202f, 0f)); // Skull, as posed
        var alongTheHead = new Vector3(0f, 0.45f, 0f);                            // Spot, in its frame

        Vector3 eye = ViewmodelAnchor.Eye(neck, alongTheHead);
        Assert.Equal(1.7702f, eye.Y, 4);

        // Anchoring the joint leaves the eye 0.45 m ABOVE the camera — which is to say the camera ends up
        // that far down inside the neck, with the whole head still overhead and in shot.
        Assert.Equal(0.45f, (ViewmodelAnchor.RigPosition(neck.Origin, Vector3.Zero) + eye).Y, 4);
        // Anchoring the eye lands it on the camera, which is the invariant above.
        Assert.Equal(0f, (ViewmodelAnchor.RigPosition(eye, Vector3.Zero) + eye).Y, 4);
    }

    // Composed with the bone's pose rather than added to its origin, so the viewpoint turns with the head
    // instead of hovering at a fixed height above a skull that has looked somewhere else.
    [Fact]
    public void TheEyeTurnsWithTheHead()
    {
        var lookingDown = new Transform3D(
            new Basis(new Quaternion(Vector3.Right, Mathf.DegToRad(-90f))), new Vector3(0f, 1.3202f, 0f));

        Vector3 eye = ViewmodelAnchor.Eye(lookingDown, new Vector3(0f, 0.45f, 0f));

        // A quarter turn about X takes the head's own up onto the world's forward (-Z).
        Assert.Equal(1.3202f, eye.Y, 4);
        Assert.Equal(-0.45f, eye.Z, 4);
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
