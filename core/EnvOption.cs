using System;
using System.Globalization;

namespace UnturnedGodot;

// Reads the tuning values the project is configured with: flags, whole numbers and measurements.
//
// EnvFlag already did this for the booleans, and its header explains what it was written to stop — two
// conventions that compared the value instead of reading it, so every spelling a person actually types
// went to the wrong branch in one direction or the other. The numbers had the same problem in a different
// shape: four private copies of "parse it, clamp it" across the foliage builder, the foliage streamer,
// the object builder and the GPU benchmark, and the fourth one had no clamp at all. A benchmark flag that
// reads as its opposite, or a bound nobody applied, invalidates a measurement run rather than failing it.
//
// So there is one of each here. Kept free of Godot and of the environment itself: the caller passes the
// value, so this is the same function whether it came from OS.GetEnvironment or System.Environment, and
// it is testable directly.
//
// Parsing is invariant on purpose. These are values typed into a shell or a CI file, not localized input,
// and a runtime whose current culture reads "1.5" as fifteen would silently change what a run measured.
public static class EnvOption
{
    // The flag's effective value. One line to EnvFlag, which is the boolean implementation and stays that
    // — this is here so new code has one name to reach for rather than two.
    public static bool IsOn(string? value, bool whenUnset) => EnvFlag.IsOn(value, whenUnset);

    // A count, clamped into the range the caller can actually honour.
    //
    // Clamping rather than rejecting, because every one of these bounds a resource — workers, pending
    // decodes, megabytes of decoded foliage — and the useful answer to "1000000 decode workers" is the
    // most the caller supports, not a crash in the middle of a load. An unreadable value is the default,
    // never an end of the range: a typo must not silently mean "as much as possible".
    public static int Whole(string? value, int whenUnset, int min, int max) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? Math.Clamp(parsed, min, max)
            : whenUnset;

    // The same for the counts that are measured in triangles, where the range runs past int.
    public static long Whole64(string? value, long whenUnset, long min, long max) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? Math.Clamp(parsed, min, max)
            : whenUnset;

    // A measurement — metres, a multiplier — clamped the same way.
    //
    // NaN is treated as unreadable rather than clamped. Math.Clamp throws on it, and it is exactly what a
    // value like "nan" parses to: a caller mid-load must get the default, not an exception.
    public static float Number(string? value, float whenUnset, float min, float max) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
            && !float.IsNaN(parsed)
                ? Math.Clamp(parsed, min, max)
                : whenUnset;
}
