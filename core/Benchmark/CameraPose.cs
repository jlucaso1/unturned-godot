using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;

namespace UnturnedGodot.Benchmark;

// A camera pose named by hand, in the same five numbers the debug overlay's F4 copies and the
// screenshot path's SHOT_CAM already take: position, then pitch and yaw in degrees.
//
// The GPU tier's own poses are derived from the scene bounds, which makes them reproducible on any map
// but leaves them blind to the views that actually cost something — a dense street looking down its own
// length is not where a bounds-relative pose lands. UG_BENCH_POSES lets a measurement name that view,
// and because it is the same string SHOT_CAM takes, the frame that was measured is byte-for-byte the
// frame that can be screenshotted for the visual half of the same A/B.
//
// Parsing lives here rather than in the Godot glue so it is unit-testable, and so a typo is reported as
// a typo instead of quietly benchmarking a different view than the one that was asked for.
public readonly record struct CameraPose(string Name, Vector3 Position, float PitchDegrees, float YawDegrees)
{
    // Godot's Node3D.RotationDegrees composes its Euler angles in YXZ order, and SHOT_CAM is applied
    // through exactly that property. Building the basis any other way would put the benchmark camera
    // somewhere the screenshot camera never goes, for the same five numbers.
    public Transform3D ToTransform() => new(
        Basis.FromEuler(new Vector3(Mathf.DegToRad(PitchDegrees), Mathf.DegToRad(YawDegrees), 0f),
            EulerOrder.Yxz),
        Position);

    /// <summary>
    /// Parses <c>name=px,py,pz,pitch,yaw</c> entries separated by <c>;</c>. A nameless entry is given a
    /// positional name. Null or blank input is an empty list, not an error.
    /// </summary>
    public static bool TryParseList(string? spec, out IReadOnlyList<CameraPose> poses, out string error)
    {
        var parsed = new List<CameraPose>();
        poses = parsed;
        error = "";
        if (string.IsNullOrWhiteSpace(spec))
            return true;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string entry in spec.Split(';', StringSplitOptions.RemoveEmptyEntries
            | StringSplitOptions.TrimEntries))
        {
            if (!TryParse(entry, $"pose{parsed.Count}", out CameraPose pose, out error))
                return false;
            if (!seen.Add(pose.Name))
            {
                error = $"'{entry}': the name '{pose.Name}' is used twice";
                poses = Array.Empty<CameraPose>();
                return false;
            }
            parsed.Add(pose);
        }
        return true;
    }

    /// <summary>Parses one <c>[name=]px,py,pz,pitch,yaw</c> entry.</summary>
    public static bool TryParse(string entry, string fallbackName, out CameraPose pose, out string error)
    {
        pose = default;
        error = "";
        string name = fallbackName;
        string numbers = entry;

        int equals = entry.IndexOf('=');
        if (equals >= 0)
        {
            name = entry[..equals].Trim();
            numbers = entry[(equals + 1)..];
            if (name.Length == 0)
            {
                error = $"'{entry}': the name before '=' is empty";
                return false;
            }
        }

        string[] parts = numbers.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 5)
        {
            error = $"'{entry}': wanted 5 numbers (px,py,pz,pitch,yaw), got {parts.Length}";
            return false;
        }

        Span<float> values = stackalloc float[5];
        for (int i = 0; i < 5; i++)
        {
            if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                || !float.IsFinite(value))
            {
                error = $"'{entry}': '{parts[i]}' is not a finite number";
                return false;
            }
            values[i] = value;
        }

        pose = new CameraPose(name, new Vector3(values[0], values[1], values[2]), values[3], values[4]);
        return true;
    }
}
