using System;
using System.Collections.Generic;

namespace UnturnedGodot.Dat;

// Ports Unturned's legacy flat Data.ParseLine (Unturned/Files/Data.cs), used by localization files.
// Split on the first space: key before, value = remainder (untrimmed). '//' lines ignored. Case-sensitive.
public sealed class LegacyData
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public string? GetString(string key) => _values.TryGetValue(key, out string? v) ? v : null;

    public static LegacyData Parse(string text)
    {
        var data = new LegacyData();
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.Length < 1 || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            int space = line.IndexOf(' ');
            if (space < 0)
                data._values[line] = string.Empty;
            else
                data._values[line.Substring(0, space)] = line.Substring(space + 1);
        }
        return data;
    }
}
