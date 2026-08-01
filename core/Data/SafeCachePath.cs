using System;
using System.IO;
using System.Text;

namespace UnturnedGodot.Data;

// Converts names read from third-party bundles into direct children of a cache directory. Path.GetFileName
// is platform-specific (a backslash is ordinary on Unix), so normalize both separator styles first and
// apply the portable Windows-invalid set as well as control characters.
public static class SafeCachePath
{
    private const int MaxStemLength = 120;

    public static string FileName(string? untrusted, string fallback, string extension)
    {
        string stem = CleanStem(untrusted);
        if (stem.Length == 0 || stem.Length > MaxStemLength || IsReservedDeviceName(stem))
            stem = CleanStem(fallback);
        if (stem.Length == 0 || stem.Length > MaxStemLength || IsReservedDeviceName(stem))
            stem = "file";
        return stem + extension;
    }

    public static bool TryResolveChild(string directory, string fileName, out string fullPath)
    {
        fullPath = "";
        if (fileName.Length == 0 || Path.IsPathRooted(fileName)
            || fileName.Contains('/') || fileName.Contains('\\'))
            return false;

        string root = Path.GetFullPath(directory);
        string candidate = Path.GetFullPath(Path.Combine(root, fileName));
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(candidate), root, comparison))
            return false;
        fullPath = candidate;
        return true;
    }

    private static string CleanStem(string? value)
    {
        string normalized = (value ?? "").Replace('\\', '/');
        int slash = normalized.LastIndexOf('/');
        string leaf = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        var clean = new StringBuilder(leaf.Length);
        foreach (char c in leaf)
            clean.Append(char.IsControl(c) || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*'
                ? '_' : c);
        return clean.ToString().Trim(' ', '.');
    }

    private static bool IsReservedDeviceName(string stem)
    {
        int dot = stem.IndexOf('.');
        ReadOnlySpan<char> device = dot >= 0 ? stem.AsSpan(0, dot) : stem.AsSpan();
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || device.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;
        return device.Length == 4
            && (device[..3].Equals("COM", StringComparison.OrdinalIgnoreCase)
                || device[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))
            && device[3] is >= '1' and <= '9';
    }
}
