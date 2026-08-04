using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace UnturnedGodot.Repro;

// Vectors in a dump are plain float arrays rather than an object with x/y/z keys: three numbers in
// brackets read the way a coordinate reads, and a window holds hundreds of thousands of them, so the
// six bytes of key names per component are the difference between a file that opens and one that does
// not. Everything that crosses that boundary goes through here.
public static class ReproVector
{
    // Millimetres. The simulation's own inputs (navmesh vertices, quantized network positions) do not
    // carry more than this, and a shorter number is a smaller file and a diffable one — two dumps of
    // the same scene differ where the scene differs, not in the last mantissa bit.
    public const int Digits = 4;

    public static float Round(float value) =>
        float.IsFinite(value) ? MathF.Round(value, Digits) : value;

    public static float[] From(Vector3 value) =>
        new[] { Round(value.X), Round(value.Y), Round(value.Z) };

    public static Vector3 To(IReadOnlyList<float>? value) =>
        value is { Count: >= 3 } ? new Vector3(value[0], value[1], value[2]) : Vector3.Zero;

    // Flattens waypoints (or any point run) into one array; the reverse fills a caller's list so the
    // replay can hand a route straight to the brain without allocating a new one per repath.
    public static float[] Flatten(IReadOnlyList<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var flat = new float[points.Count * 3];
        for (int i = 0; i < points.Count; i++)
        {
            flat[(i * 3) + 0] = Round(points[i].X);
            flat[(i * 3) + 1] = Round(points[i].Y);
            flat[(i * 3) + 2] = Round(points[i].Z);
        }
        return flat;
    }

    public static void Unflatten(IReadOnlyList<float>? flat, List<Vector3> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.Clear();
        if (flat == null)
            return;
        for (int i = 0; i + 2 < flat.Count; i += 3)
            into.Add(new Vector3(flat[i], flat[i + 1], flat[i + 2]));
    }

    public static string Describe(IReadOnlyList<float>? value) => value is { Count: >= 3 }
        ? string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##}, {2:0.##})",
            value[0], value[1], value[2])
        : "(none)";
}
