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
        Assert.Equal(new Vector3(0, 360, 0),
            UnityRounding.RoundIfNearlyAxisAligned(new Vector3(0, 359.99f, 0)));
        Assert.Equal(new Vector3(-90, 0, 0),
            UnityRounding.RoundIfNearlyAxisAligned(new Vector3(-89.98f, 0.01f, -0.01f)));
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
