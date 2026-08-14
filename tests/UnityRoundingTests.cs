using Godot;
using UnturnedGodot.Data;
using Xunit;

namespace UnturnedGodot.Tests;

// QuaternionEx.GetRoundedIfNearlyAxisAligned and Vector3Ex.GetRoundedIfNearlyEqualToOne, which Unturned
// runs over every object transform as it loads one. What they are worth is invisible on the official
// maps — none of PEI's 4,329 placements is off by enough to move — and obvious on the ones that drift:
// two walls a hair out of square z-fight, or show a seam of daylight between them.
public class UnityRoundingTests
{
    [Fact]
    public void RoundIfNearlyAxisAligned_SnapsAnAlmostSquareRotation()
    {
        // The editor's own output: a wall dragged against another one.
        Assert.Equal(new Vector3(0, 90, 0),
            UnityRounding.RoundIfNearlyAxisAligned(new Vector3(0.001f, 89.99f, -0.02f)));
    }

    [Fact]
    public void RoundIfNearlyAxisAligned_LeavesADeliberateAngleAlone()
    {
        var slope = new Vector3(0, 37.5f, 0);
        Assert.Equal(slope, UnityRounding.RoundIfNearlyAxisAligned(slope));

        // Just outside the 0.05 degree tolerance, which is strictly-less-than.
        var justOutside = new Vector3(0, 89.94f, 0);
        Assert.Equal(justOutside, UnityRounding.RoundIfNearlyAxisAligned(justOutside));
    }

    [Fact]
    public void RoundIfNearlyAxisAligned_IsAllThreeAxesOrNone()
    {
        // A transform square on two axes and deliberately tilted on the third is a ramp, not a
        // misaligned wall; snapping the square two would move it.
        var ramp = new Vector3(0.001f, 90.001f, 12f);
        Assert.Equal(ramp, UnityRounding.RoundIfNearlyAxisAligned(ramp));
    }

    [Fact]
    public void RoundIfNearlyAxisAligned_ComparesTheShortWayRound()
    {
        // Mathf.DeltaAngle: 359.99 is 0.01 away from 360, not 359.99 away from 0. Without the wrap the
        // rotations closest to a full turn — the commonest kind of drift there is — would never snap.
        // The answer comes back as 0 rather than 360 because Unity reports Euler angles in [0, 360).
        Assert.Equal(new Vector3(0, 0, 0),
            UnityRounding.RoundIfNearlyAxisAligned(new Vector3(0, 359.99f, 0)));
        // Likewise a negative tilt: the game would report this rotation as 270, never as -90.
        Assert.Equal(new Vector3(270, 0, 0),
            UnityRounding.RoundIfNearlyAxisAligned(new Vector3(-89.98f, 0.01f, -0.01f)));
    }

    [Fact]
    public void RoundIfNearlyAxisAligned_SnapsAtTheEulerSingularity()
    {
        // The case a component-wise test cannot see, and the reason this goes through the quaternion.
        // (90, 44.99, -45) and (90, 89.99, 0) are the SAME orientation — at a 90-degree tilt the y and z
        // turns collapse into one — so the game canonicalizes, sees 89.99 against a right angle, and
        // snaps. Read component-wise it is two 45-degree angles and nothing rounds, leaving the drift in
        // exactly the near-square placement this rounding exists to rescue.
        Vector3 rounded = UnityRounding.RoundIfNearlyAxisAligned(new Vector3(90f, 44.99f, -45f));

        Assert.Equal(90f, rounded.X, 3);
        Assert.Equal(90f, rounded.Y, 3);
        Assert.Equal(0f, rounded.Z, 3);
    }

    [Fact]
    public void UnityEulerAngles_InvertsQuaternionEuler()
    {
        // The decomposition has to be the true inverse of the ZXY composition away from the poles, or
        // every ordinary rotation would be re-spelled on load.
        foreach (Vector3 euler in new[]
        {
            new Vector3(0, 0, 0), new Vector3(0, 90, 0), new Vector3(10, 20, 30),
            new Vector3(45, 135, 200), new Vector3(0, 359.99f, 0),
        })
        {
            Vector3 round = UnityRounding.UnityEulerAngles(UnityMath.EulerToUnityQuaternion(euler));
            // Each of these is already its own canonical spelling, so the decomposition has to hand back
            // what it was given — to within the float the quaternion was built from.
            Assert.Equal(euler.X, round.X, 3);
            Assert.Equal(euler.Y, round.Y, 3);
            Assert.Equal(euler.Z, round.Z, 3);
        }
    }

    [Fact]
    public void UnityEulerAngles_CollapsesYawAndRollAtThePoles()
    {
        // Straight up and straight down: z folds into y, and the sign of the fold follows the tilt.
        //
        // Asserted to within 0.05 degrees rather than exactly, and the slack is the game's, not this
        // port's: the quaternion is built in float, asin is ill-conditioned at the pole, and Unity's own
        // round trip lands ~0.03 degrees off 90 for the same reason. Tightening this would be asserting
        // something the original does not do.
        const float atThePole = 0.05f;

        Vector3 up = UnityRounding.UnityEulerAngles(
            UnityMath.EulerToUnityQuaternion(new Vector3(90f, 30f, -20f)));
        Assert.Equal(90f, up.X, atThePole);
        Assert.Equal(50f, up.Y, atThePole);   // 30 - (-20)
        Assert.Equal(0f, up.Z, atThePole);

        Vector3 down = UnityRounding.UnityEulerAngles(
            UnityMath.EulerToUnityQuaternion(new Vector3(-90f, 30f, 20f)));
        Assert.Equal(270f, down.X, atThePole);
        Assert.Equal(50f, down.Y, atThePole);  // 30 + 20
        Assert.Equal(0f, down.Z, atThePole);
    }

    [Fact]
    public void RoundIfNearlyAxisAligned_LeavesAnExactlySquareRotationUntouched()
    {
        // The common case, and the one PEI is entirely made of: already rounded, so nothing moves.
        var square = new Vector3(0, 180, 0);
        Assert.Equal(square, UnityRounding.RoundIfNearlyAxisAligned(square));
        Assert.Equal(Vector3.Zero, UnityRounding.RoundIfNearlyAxisAligned(Vector3.Zero));
    }

    [Fact]
    public void RoundIfNearlyEqualToOne_SnapsPerComponentAndToMinusOne()
    {
        // A mirrored placement is authored as a negative scale, so -1 gets the same treatment as 1.
        Assert.Equal(new Vector3(1, -1, 0.5f),
            UnityRounding.RoundIfNearlyEqualToOne(new Vector3(1.0000001f, -0.9999f, 0.5f)));
    }

    [Fact]
    public void RoundIfNearlyEqualToOne_LeavesADeliberateScaleAlone()
    {
        var half = new Vector3(0.5f, 2f, 0.998f); // 0.998 is outside the 0.001 tolerance
        Assert.Equal(half, UnityRounding.RoundIfNearlyEqualToOne(half));
        Assert.Equal(Vector3.One, UnityRounding.RoundIfNearlyEqualToOne(Vector3.One));
    }
}
