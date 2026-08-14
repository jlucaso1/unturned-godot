using System;
using Godot;

namespace UnturnedGodot.Data;

// Ports QuaternionEx.GetRoundedIfNearlyAxisAligned and Vector3Ex.GetRoundedIfNearlyEqualToOne
// (UnityEx/QuaternionEx.cs, UnityEx/Vector3Ex.cs), which Unturned applies to every object transform as
// it reads it (LevelObjects.cs:652 and :656).
//
// They exist because a level editor produces transforms that are almost, but not exactly, the round
// numbers a person aimed at: a wall dragged into place next to another one comes out at 89.9997 degrees
// and 1.0000001 scale. Left alone, two walls that should be coplanar are not, and what the player sees
// is a hairline of daylight through a building, or the two faces flickering against each other as the
// camera moves. Snapping at load is the game's fix, and it is worth having even where it never fires:
// none of PEI's 4,329 placements is off by enough to be rounded, because the modern editor already
// writes them rounded — it is workshop maps, and maps saved by older editors, that carry the drift.
public static class UnityRounding
{
    // MathfEx.IsNearlyEqual's default, and the tolerance Vector3Ex.GetRoundedIfNearlyEqualToOne asks for.
    public const float ScaleTolerance = 0.001f;

    // GetRoundedIfNearlyAxisAligned's default, in degrees.
    public const float AngleToleranceDegrees = 0.05f;

    // The SDK rounds a Quaternion, and the decision is made on `quaternion.eulerAngles` — so the raw
    // triple the file stores has to go through the quaternion first, because those two are NOT the same
    // triple wrapped into [0, 360).
    //
    // At Unity's Euler singularity they can disagree completely. `(90, 44.99, -45)` describes the same
    // orientation as `(90, 89.99, 0)`, and only the second reads as nearly axis-aligned: the first has
    // two 45-degree components and a component-wise test leaves the drift in place, which is exactly the
    // near-square wall this rounding exists to rescue. Canonicalising first makes the answer depend on
    // the orientation rather than on which of its spellings the editor happened to serialize.
    //
    // The unrounded return is the caller's ORIGINAL triple, not the canonical one: the SDK returns the
    // quaternion it was given, and `Quaternion.Euler(raw)` is that same rotation, so re-spelling a
    // rotation this function decided not to touch would be a change the game does not make.
    public static Vector3 RoundIfNearlyAxisAligned(Vector3 eulerDegrees,
        float toleranceDegrees = AngleToleranceDegrees)
    {
        Vector3 canonical = UnityEulerAngles(UnityMath.EulerToUnityQuaternion(eulerDegrees));

        // Wrapped again after the snap, because a canonical angle just under 360 rounds UP to it and 360
        // is not a spelling Unity ever reports. The SDK stores the quaternion so the distinction never
        // surfaces there — Quaternion.Euler(0, 90, 360) is Quaternion.Euler(0, 90, 0) — but this port
        // stores the angles, and 360 would be a value no `.eulerAngles` read could have produced.
        var rounded = new Vector3(
            Wrap360(RoundToRightAngle(canonical.X)),
            Wrap360(RoundToRightAngle(canonical.Y)),
            Wrap360(RoundToRightAngle(canonical.Z)));

        // All three axes or none: a transform that is square on two axes and deliberately tilted on the
        // third is a slope, not a misaligned wall, and snapping the two would move the object.
        return IsAngleNearlyEqual(canonical.X, rounded.X, toleranceDegrees)
            && IsAngleNearlyEqual(canonical.Y, rounded.Y, toleranceDegrees)
            && IsAngleNearlyEqual(canonical.Z, rounded.Z, toleranceDegrees)
                ? rounded
                : eulerDegrees;
    }

    // Unity's `Quaternion.eulerAngles`: the ZXY decomposition that inverts Quaternion.Euler, wrapped
    // into [0, 360). Written out because there is no managed Unity here to ask, and because the
    // singularity branch is the whole reason the canonicalisation above matters.
    //
    // With R = Ry·Rx·Rz, the useful elements fall out directly: R12 is -sin(x), the first column of row
    // one carries cos(x)·sin(z) against cos(x)·cos(z), and R02/R22 carry sin(y)·cos(x) against
    // cos(y)·cos(x). When cos(x) collapses — the tilt is straight up or straight down — y and z stop
    // being separable and Unity attributes the whole remaining turn to y, which is how (90, 44.99, -45)
    // becomes (90, 89.99, 0).
    // The arithmetic is widened to double and narrowed at the end, because asin is ill-conditioned
    // exactly where this matters: near the poles its derivative runs away, so an error of one float ulp
    // in the input comes out multiplied by roughly a thousand.
    //
    // Widening does not make a pole exact, and it is not meant to. The quaternion arrives already built
    // in float, and sqrt of its ~1e-7 error is ~0.03 degrees, so `Quaternion.Euler(90, 30, -20)`
    // decomposes to 89.968 here — and to the same 89.968 in Unity, which runs the identical float
    // round trip. That is the game's own number and is left alone. What the widening buys is not adding
    // a SECOND helping of the same error on top, which would push the total past the caller's
    // 0.05-degree budget and stop near-square placements at the pole from snapping at all.
    public static Vector3 UnityEulerAngles(Quaternion q)
    {
        double qx = q.X, qy = q.Y, qz = q.Z, qw = q.W;
        double r00 = 1d - (2d * ((qy * qy) + (qz * qz)));
        double r01 = 2d * ((qx * qy) - (qw * qz));
        double r02 = 2d * ((qx * qz) + (qw * qy));
        double r10 = 2d * ((qx * qy) + (qw * qz));
        double r11 = 1d - (2d * ((qx * qx) + (qz * qz)));
        double r12 = 2d * ((qy * qz) - (qw * qx));
        double r22 = 1d - (2d * ((qx * qx) + (qy * qy)));

        double sinX = Math.Clamp(-r12, -1d, 1d);
        double x = Math.Asin(sinX);
        double y, z;
        if (Math.Abs(sinX) < GimbalLockSin)
        {
            z = Math.Atan2(r10, r11);
            y = Math.Atan2(r02, r22);
        }
        else
        {
            // Straight up or straight down: z folds into y, and its sign follows the tilt's.
            z = 0d;
            y = Math.Atan2(Math.Sign(sinX) * r01, r00);
        }

        return new Vector3(Wrap360(RadToDeg(x)), Wrap360(RadToDeg(y)), Wrap360(RadToDeg(z)));
    }

    // Unity treats the decomposition as degenerate slightly before the poles rather than exactly at
    // them, because near them y and z are numerically indistinguishable long before cos(x) reaches zero.
    private const double GimbalLockSin = 0.99999d;

    private static float RadToDeg(double radians) => (float)(radians * (180d / Math.PI));

    // Unity reports Euler angles in [0, 360), so a rotation authored as -90 comes back as 270.
    private static float Wrap360(float degrees) => Repeat(degrees, 360f);

    // Vector3Ex.GetRoundedIfNearlyEqualToOne: per-component, and to -1 as readily as to 1, because a
    // mirrored placement is authored as a negative scale. A component near neither is left alone, so an
    // object at 0.5 keeps its 0.5.
    public static Vector3 RoundIfNearlyEqualToOne(Vector3 scale, float tolerance = ScaleTolerance)
        => new(RoundComponent(scale.X, tolerance), RoundComponent(scale.Y, tolerance),
            RoundComponent(scale.Z, tolerance));

    private static float RoundComponent(float value, float tolerance)
    {
        if (IsNearlyEqual(value, 1f, tolerance))
            return 1f;
        if (IsNearlyEqual(value, -1f, tolerance))
            return -1f;
        return value;
    }

    // Mathf.RoundToInt is round-half-to-even, and so is MathF.Round's default, which matters for the
    // exact half: 45 degrees is equidistant from 0 and 90 and has to land somewhere. It never survives
    // the tolerance check afterwards either way — this only keeps the two implementations agreeing.
    private static float RoundToRightAngle(float degrees) => MathF.Round(degrees / 90f) * 90f;

    // MathfEx.IsNearlyEqual: strictly less than, not less-or-equal.
    private static bool IsNearlyEqual(float a, float b, float tolerance) => MathF.Abs(b - a) < tolerance;

    // MathfEx.IsAngleDegreesNearlyEqual, over Mathf.DeltaAngle: the signed distance the short way round,
    // so 359.99 and 0 are apart by 0.01 rather than by 359.99.
    private static bool IsAngleNearlyEqual(float a, float b, float tolerance) =>
        MathF.Abs(DeltaAngle(a, b)) < tolerance;

    // Mathf.DeltaAngle.
    private static float DeltaAngle(float current, float target)
    {
        float delta = Repeat(target - current, 360f);
        if (delta > 180f)
            delta -= 360f;
        return delta;
    }

    // Mathf.Repeat, which — unlike C#'s % — never returns a negative for a positive length.
    private static float Repeat(float t, float length) =>
        Math.Clamp(t - (MathF.Floor(t / length) * length), 0f, length);
}
