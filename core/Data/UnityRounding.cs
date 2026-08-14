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

    // The SDK rounds a Quaternion; this port carries a rotation as the three Euler degrees the file
    // itself stores, so the round trip through a quaternion is skipped. It does not change the answer:
    // Unity's decision is made on `quaternion.eulerAngles`, which differs from the raw angles only by
    // wrapping them into [0, 360), and both the comparison (Mathf.DeltaAngle) and the rounding (to a
    // multiple of 90) are wrap-invariant — -90 and 270 round to themselves and describe one rotation.
    public static Vector3 RoundIfNearlyAxisAligned(Vector3 eulerDegrees,
        float toleranceDegrees = AngleToleranceDegrees)
    {
        var rounded = new Vector3(
            RoundToRightAngle(eulerDegrees.X),
            RoundToRightAngle(eulerDegrees.Y),
            RoundToRightAngle(eulerDegrees.Z));

        // All three axes or none: a transform that is square on two axes and deliberately tilted on the
        // third is a slope, not a misaligned wall, and snapping the two would move the object.
        return IsAngleNearlyEqual(eulerDegrees.X, rounded.X, toleranceDegrees)
            && IsAngleNearlyEqual(eulerDegrees.Y, rounded.Y, toleranceDegrees)
            && IsAngleNearlyEqual(eulerDegrees.Z, rounded.Z, toleranceDegrees)
                ? rounded
                : eulerDegrees;
    }

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
