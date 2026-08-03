using System;

namespace UnturnedGodot;

// Reads the boolean tuning flags the project is configured with.
//
// There were two conventions and neither read the value, only compared it. Opt-out flags asked
// `!= "0"` and opt-in flags asked `== "1"`, so every spelling a person actually types went to the wrong
// branch in one direction or the other: UG_FOLIAGE_RESIDENCY=false enabled residency, because "false" is
// not "0"; UG_NODE_MULTIMESH=true left it off, because "true" is not "1". Both silently, and the flags
// that behave this way are the ones documented in the README for benchmarking, where a flag that reads
// as its opposite invalidates the run rather than failing it.
//
// Kept free of Godot and of the environment itself: the caller passes the value, so this is the same
// function whether it came from OS.GetEnvironment or System.Environment, and it is testable directly.
public static class EnvFlag
{
    // Spellings accepted in each direction, case-insensitively. Deliberately a closed set — anything else
    // is a typo, and guessing at it is how a flag ends up meaning its opposite.
    private static readonly string[] On = { "1", "true", "yes", "on" };
    private static readonly string[] Off = { "0", "false", "no", "off" };

    // True/false when the value names one, null when it is absent, empty, or not a spelling we accept.
    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        string trimmed = value.Trim();
        foreach (string on in On)
            if (string.Equals(trimmed, on, StringComparison.OrdinalIgnoreCase))
                return true;
        foreach (string off in Off)
            if (string.Equals(trimmed, off, StringComparison.OrdinalIgnoreCase))
                return false;
        return null;
    }

    // The flag's effective value. `whenUnset` carries the flag's own default, which is what distinguishes
    // an opt-out (default on) from an opt-in (default off) — the thing the old `!= "0"` / `== "1"` split
    // encoded in the comparison instead of stating.
    //
    // An unrecognised value falls back to the default rather than being guessed at. Callers that can log
    // should check Parse themselves and say so; a silently misread flag is worse than a noisy one.
    public static bool IsOn(string? value, bool whenUnset) => Parse(value) ?? whenUnset;
}
